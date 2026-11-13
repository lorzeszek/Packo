using Packo.Helpers;
using Packo.Models.DTO;
using Packo.Popups;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.ViewModels;

namespace Packo.Services;

public sealed class ReminderNavigationService
{
    public const string OpenAction = "com.travelset.app.OPEN_REMINDER";
    public const string AccountExtra = "reminder_account_id";
    public const string LocalTripExtra = "reminder_local_trip_id";
    public const string RemoteTripExtra = "reminder_remote_trip_id";
    private readonly ISessionService _session;
    private readonly ITripListPreloadService _preload;
    private readonly ITripRepository _trips;
    private readonly ReminderNavigationQueue _queue;
    private Shell? _shell;
    private Page? _watchedPage;
    private bool _active;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    public ReminderNavigationService(ISessionService session, ITripListPreloadService preload, ITripRepository trips)
    {
        (_session, _preload, _trips) = (session, preload, trips);
        _queue = new ReminderNavigationQueue(CanNavigate, () => _session.LocalUserId, ResolveAsync, OpenAsync,
            () => ConfirmPopup.ShowMessageAsync(Strings["Reminders"], Strings["ReminderTripUnavailable"]));
    }

    public void Receive(ReminderTripTarget target) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _queue.Receive(target);
        Schedule();
    });

    public void AttachShell(Shell shell)
    {
        if (_shell is not null) { _shell.Loaded -= OnReady; _shell.Navigated -= OnNavigated; }
        _shell = shell;
        shell.Loaded += OnReady;
        shell.Navigated += OnNavigated;
        WatchPage();
        Schedule();
    }

    public void SetWindowActive(bool active) { _active = active; if (active) Schedule(); }

    private void OnReady(object? sender, EventArgs e) => Schedule();
    private void OnNavigated(object? sender, ShellNavigatedEventArgs e) { WatchPage(); Schedule(); }
    private void WatchPage()
    {
        var page = _shell?.CurrentPage;
        if (ReferenceEquals(page, _watchedPage)) return;
        if (_watchedPage is not null) { _watchedPage.Loaded -= OnReady; _watchedPage.Appearing -= OnReady; }
        _watchedPage = page;
        if (page is not null) { page.Loaded += OnReady; page.Appearing += OnReady; }
    }

    private void Schedule() => _shell?.Dispatcher.Dispatch(() => _ = ProcessPendingAsync());
    private bool CanNavigate() => _active && !_session.GlobalIsBusy && _shell is { IsLoaded: true, CurrentPage.IsLoaded: true }
        && _shell.Navigation.ModalStack.Count == 0;

    public async Task ProcessPendingAsync()
    {
        if (!_queue.HasPending) return;
        WatchPage();
        try { await _queue.ProcessAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Opening reminder trip: {ex.GetType().Name}");
            if (CanNavigate()) await ConfirmPopup.ShowMessageAsync(Strings["Reminders"], Strings["ReminderOpenTripFailed"]);
        }
    }

    private async Task<TripDTO?> ResolveAsync(ReminderTripTarget target)
    {
        if (_session.IsAuthenticated && Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
        {
            // A successful fresh snapshot also detects revoked sharing and deleted trips.
            var entries = await _preload.GetTripsAsync(forceRefresh: true);
            var trip = entries.Select(x => x.Trip).FirstOrDefault(target.Matches);
            if (trip is not null) return trip;
            if (target.RemoteTripId > 0) return null;
            var draft = await CachedAsync(target);
            return draft?.RemoteTripId == 0 ? draft : null;
        }
        return await CachedAsync(target);
    }

    private async Task<TripDTO?> CachedAsync(ReminderTripTarget target)
    {
        if (target.LocalTripId <= 0) return null;
        var trip = await _trips.GetLocalTripAsync(target.LocalTripId, target.AccountId);
        return trip is not null && target.Matches(trip) ? trip : null;
    }

    private async Task OpenAsync(TripDTO trip)
    {
        var shell = _shell!;
        // Reuse the current details screen if it already displays this trip.
        if (shell.CurrentPage.BindingContext is StartViewModel { Trip: { } current }
            && new ReminderTripTarget(trip.LocalUserId, trip.LocalTripId, trip.RemoteTripId).Matches(current.TripModel)) return;
        await shell.GoToAsync("//TripList", animate: false);
        await shell.GoToAsync(StartViewModel.Route, animate: false, new ShellNavigationQueryParameters { ["trip"] = trip });
    }
}
