using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Models.DTO;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.ViewModels;
using Packo.Views;

namespace Packo.Services;

public sealed class TripInvitationService(
    ISupabaseService supabase, ISessionService session, ITripRepository trips, IServiceProvider services)
{
    private const string PendingKey = "packo_pending_trip_invite";
    private static readonly string Host = typeof(TripInvitationService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(x => x.Key == "PackoInviteHost").Value!;
    private readonly SemaphoreSlim _handling = new(1, 1);
    private Shell? _shell;
    private Page? _waitingPage;
    private bool _windowActive;
    private bool _readStoredInvite;
    private Guid? _pendingToken;
    private Guid? _storedToken;
    private Guid? _lastLoginPrompt;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    public bool IsConfigured => Uri.CheckHostName(Host) == UriHostNameType.Dns && !Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);
    public bool HasPendingInvite => _pendingToken.HasValue;
    public event EventHandler? InviteReceived;

    public void AttachShell(Shell shell)
    {
        if (_shell is not null)
        {
            _shell.Loaded -= OnShellLoaded;
            _shell.Navigated -= OnShellNavigated;
        }
        _shell = shell;
        shell.Loaded += OnShellLoaded;
        shell.Navigated += OnShellNavigated;
    }

    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (active)
            ScheduleProcessing();
    }

    private void ScheduleProcessing() => _shell?.Dispatcher.Dispatch(() => _ = ProcessPendingAsync());

    private void OnShellLoaded(object? sender, EventArgs e) => ScheduleProcessing();
    private void OnShellNavigated(object? sender, ShellNavigatedEventArgs e) => ScheduleProcessing();

    private void WaitForPage(Page? page)
    {
        if (_waitingPage == page)
            return;
        if (_waitingPage is not null)
            _waitingPage.Loaded -= OnPageLoaded;
        _waitingPage = page;
        if (page is not null)
            page.Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object? sender, EventArgs e)
    {
        WaitForPage(null);
        ScheduleProcessing();
    }

    public void Receive(Uri link)
    {
        if (!IsConfigured || link.Scheme != Uri.UriSchemeHttps || !link.IsDefaultPort || link.UserInfo.Length != 0
            || !link.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || link.AbsolutePath.TrimEnd('/') != "/join"
            || !Guid.TryParseExact(link.Fragment.TrimStart('#'), "N", out var token))
            return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _lastLoginPrompt = null;
            _pendingToken = token;
            InviteReceived?.Invoke(this, EventArgs.Empty);
            ScheduleProcessing();
        });
    }

    public async Task ProcessPendingAsync()
    {
        if (!IsConfigured || !_windowActive || _shell is not { IsLoaded: true } shell || session.GlobalIsBusy
            || (_readStoredInvite && _pendingToken is null))
            return;
        if (shell.CurrentPage is not { IsLoaded: true } page)
        {
            WaitForPage(shell.CurrentPage);
            return;
        }
        if (!await _handling.WaitAsync(0))
            return;

        Guid? processing = null;
        try
        {
            if (!_readStoredInvite)
            {
                if (_pendingToken is null && Guid.TryParseExact(await SecureStorage.GetAsync(PendingKey), "N", out var stored))
                {
                    _storedToken = stored;
                    _pendingToken ??= stored;
                }
                _readStoredInvite = true;
            }
            if (_pendingToken is not { } token)
                return;
            processing = token;

            if (_storedToken != token)
            {
                await WithLoadingAsync(shell, "OpeningTripInvite", async () =>
                {
                    await SecureStorage.SetAsync(PendingKey, token.ToString("N"));
                    _storedToken = token;
                    return true;
                });
            }
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                return;
            if (!session.IsAuthenticated && session.UserId is not null)
                await WithLoadingAsync(shell, "OpeningTripInvite", () => session.TryRecoverSessionAsync());
            if (!_windowActive || shell.CurrentPage != page || session.GlobalIsBusy || _pendingToken != token)
                return;

            if (!session.IsAuthenticated)
            {
                if (_lastLoginPrompt == token)
                    return;
                _lastLoginPrompt = token;
                if (await ConfirmPopup.AskAsync(Strings["JoinTrip"], Strings["JoinTripLogin"], Strings["Login"], Strings["Cancel"]))
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        if (services.GetService<ShellHeaderViewModel>() is { } header)
                            await header.LoginWithGoogleCommand.ExecuteAsync(null);
                    });
                return;
            }

            var account = session.UserId;
            if (!await ConfirmPopup.AskAsync(Strings["JoinTrip"], Strings["JoinTripPrompt"], Strings["JoinTrip"], Strings["Cancel"]))
            {
                ClearPending(token);
                return;
            }
            if (!session.IsAuthenticated || session.UserId != account || shell.CurrentPage != page)
                return;

            var response = await WithLoadingAsync(shell, "JoiningTrip",
                () => supabase.Client.Rpc("accept_trip_invite", new { p_token = token }).WaitAsync(TimeSpan.FromSeconds(20)));
            var status = JsonSerializer.Deserialize<string>(response.Content!);
            if (status == "invalid")
            {
                ClearPending(token);
                await ConfirmPopup.ShowMessageAsync(Strings["JoinTrip"], Strings["InviteInvalid"]);
            }
            else if (status == "joined")
            {
                ClearPending(token);
                WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
                await shell.GoToAsync("//TripList");
                WeakReferenceMessenger.Default.Send(new PackingSyncMessage(0));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.GetType().Name);
            if (processing.HasValue && _windowActive)
                await ConfirmPopup.ShowMessageAsync(Strings["JoinTrip"], Strings["SharingRequestFailed"]);
        }
        finally
        {
            _handling.Release();
            if (_pendingToken is { } next && next != processing)
                ScheduleProcessing();
        }
    }

    private void ClearPending(Guid token)
    {
        if (_pendingToken == token)
            _pendingToken = null;
        if (_storedToken == token)
        {
            SecureStorage.Remove(PendingKey);
            _storedToken = null;
        }
    }

    private static async Task<T> WithLoadingAsync<T>(Shell shell, string statusKey, Func<Task<T>> action)
    {
        var navigation = shell.Navigation;
        var loading = new LoadingPage(statusKey);
        await navigation.PushModalAsync(loading, animated: false);
        try { return await action(); }
        finally
        {
            if (navigation.ModalStack.LastOrDefault() == loading)
                await navigation.PopModalAsync(animated: false);
        }
    }

    public async Task ShareAsync(TripDTO trip)
    {
        var id = trip.RemoteTripId;
        if (id <= 0)
        {
            await trips.SyncPendingChangesAsync();
            id = (await trips.GetLocalTripAsync(trip.LocalTripId, trip.LocalUserId))?.RemoteTripId ?? 0;
        }
        if (id <= 0 || !session.IsAuthenticated || trip.IsJoinedTrip || !IsConfigured)
            throw new InvalidOperationException("The trip cannot be shared yet.");

        var token = Guid.NewGuid();
        var response = await supabase.Client.Rpc("create_trip_invite", new { p_trip_id = id, p_token = token });
        if (!JsonSerializer.Deserialize<bool>(response.Content!))
            throw new InvalidOperationException("The invitation was refused.");

        await Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = Strings["ShareTrip"],
            Text = $"https://{Host}/join/#{token:N}"
        });
    }

    public async Task RevokeAsync(int tripId, string? userId)
    {
        var response = await supabase.Client.Rpc("revoke_trip_access", new { p_trip_id = tripId, p_user_id = userId });
        if (!JsonSerializer.Deserialize<bool>(response.Content!))
            throw new InvalidOperationException("Access could not be revoked.");
        WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
    }
}
