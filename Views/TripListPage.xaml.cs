using Packo.Models.Pages;
using Packo.ViewModels;
using Microsoft.Maui.Dispatching;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Popups;
using Packo.Services;

namespace Packo.Views;

public partial class TripListPage : BasePage// ContentPage
{
    private readonly TripListViewModel _viewModel;
    private readonly TripReminderService _reminders;
    private bool _suppressNextAppearing;
    private readonly IDispatcherTimer _scheduleTimer;
    private DateTime _nextCollaborationRefresh;
    private bool _collaborationDirty;
    private bool _updatingReminderBadges;

    public TripListPage(TripListViewModel viewModel, TripReminderService reminders)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _reminders = reminders;
        BindingContext = _viewModel;
        _scheduleTimer = Dispatcher.CreateTimer();
        _scheduleTimer.Interval = TimeSpan.FromSeconds(1);
        _scheduleTimer.Tick += async (_, _) =>
        {
            _viewModel.RefreshTripSchedules();
            if (Shell.Current?.CurrentPage != this || DateTime.UtcNow < _nextCollaborationRefresh
                || Connectivity.Current.NetworkAccess != NetworkAccess.Internet
                || (!_collaborationDirty && !_viewModel.Trips.Any(trip => trip.IsShared)))
                return;
            _nextCollaborationRefresh = DateTime.UtcNow.AddSeconds(_collaborationDirty ? 5 : 20);
            _collaborationDirty = false;
            await _viewModel.RefreshCollaborationAsync();
        };
        WeakReferenceMessenger.Default.Register<TripListPage, PackingSyncMessage>(this, (page, message) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                page._collaborationDirty = true;
                if (message.RemoteTripId == 0 || message.AccessRevoked)
                    page._nextCollaborationRefresh = DateTime.MinValue;
            }));
    }

    private async void OnRemindersClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: TripViewModel trip } && !_viewModel.Session.GlobalIsBusy)
        {
            await ReminderListPopup.ShowAsync(this, _reminders, trip.TripModel);
            await UpdateReminderBadgesAsync();
        }
    }

    private void OnRemindersChanged() => MainThread.BeginInvokeOnMainThread(async () => await UpdateReminderBadgesAsync());

    private async Task UpdateReminderBadgesAsync()
    {
        if (_updatingReminderBadges) return;
        _updatingReminderBadges = true;
        try
        {
            var active = await _reminders.ActiveLocalTripIdsAsync();
            foreach (var trip in _viewModel.Trips.ToList()) trip.HasReminders = active.Contains(trip.TripModel.LocalTripId);
        }
        finally { _updatingReminderBadges = false; }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        _scheduleTimer.Stop();
        _reminders.Changed -= OnRemindersChanged;

        if (_viewModel.IsLoadingModalActive)
        {
            _suppressNextAppearing = true;
            return;
        }

        await _viewModel.DisposeRealtimeAsync();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.RefreshTripSchedules();
        _scheduleTimer.Start();
        _reminders.Changed -= OnRemindersChanged;
        _reminders.Changed += OnRemindersChanged;

        if (_suppressNextAppearing)
        {
            _suppressNextAppearing = false;
            return;
        }
        if (BindingContext is TripListViewModel vm)
            await vm.OnAppearingAsync();
        await _reminders.RefreshSafeAsync(force: false);
        await UpdateReminderBadgesAsync();
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);

        if (BindingContext is TripListViewModel vm)
            await vm.OnAppearingAsync();
    }

    //private async void OnTripSelected(
    //{
    //    if (e.CurrentSelection.FirstOrDefault() is Trip selectedTrip)
    //    {
    //        await Navigation.PushAsync(new PackingListPage(selectedTrip));
    //    }
    //}
}
