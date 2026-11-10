using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Models.DTO;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.ViewModels;

namespace Packo.Services;

public sealed class TripInvitationService(
    ISupabaseService supabase, ISessionService session, ITripRepository trips, IServiceProvider services)
{
    private const string PendingKey = "packo_pending_trip_invite";
    private static readonly string Host = typeof(TripInvitationService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(x => x.Key == "PackoInviteHost").Value!;
    private readonly SemaphoreSlim _handling = new(1, 1);
    private string? _lastLoginPrompt;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    public bool IsConfigured => Uri.CheckHostName(Host) == UriHostNameType.Dns && !Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);

    public void Receive(Uri link)
    {
        if (!IsConfigured || link.Scheme != Uri.UriSchemeHttps || !link.IsDefaultPort || link.UserInfo.Length != 0
            || !link.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || link.AbsolutePath.TrimEnd('/') != "/join"
            || !Guid.TryParseExact(link.Fragment.TrimStart('#'), "N", out var token))
            return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                _lastLoginPrompt = null;
                await SecureStorage.SetAsync(PendingKey, token.ToString("N"));
                await ProcessPendingAsync();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        });
    }

    public async Task ProcessPendingAsync()
    {
        if (!IsConfigured || Shell.Current?.CurrentPage is not { } page || session.GlobalIsBusy || !await _handling.WaitAsync(0))
            return;
        try
        {
            var pending = await SecureStorage.GetAsync(PendingKey);
            if (!Guid.TryParseExact(pending, "N", out var token))
                return;
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                return;
            if (!session.IsAuthenticated)
            {
                if (_lastLoginPrompt == pending)
                    return;
                _lastLoginPrompt = pending;
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
                await ClearPendingAsync(pending!);
                return;
            }
            if (!session.IsAuthenticated || session.UserId != account || Shell.Current?.CurrentPage != page)
                return;

            var response = await supabase.Client.Rpc("accept_trip_invite", new { p_token = token });
            var status = JsonSerializer.Deserialize<string>(response.Content!);
            if (status == "invalid")
            {
                await ClearPendingAsync(pending!);
                await ConfirmPopup.ShowMessageAsync(Strings["JoinTrip"], Strings["InviteInvalid"]);
            }
            else if (status == "joined")
            {
                await ClearPendingAsync(pending!);
                WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
                await Shell.Current.GoToAsync("//TripList");
                WeakReferenceMessenger.Default.Send(new PackingSyncMessage(0));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.GetType().Name);
            await ConfirmPopup.ShowMessageAsync(Strings["JoinTrip"], Strings["SharingRequestFailed"]);
        }
        finally { _handling.Release(); }
    }

    private static async Task ClearPendingAsync(string pending)
    {
        if (await SecureStorage.GetAsync(PendingKey) == pending)
            SecureStorage.Remove(PendingKey);
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
