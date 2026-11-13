using Packo.ViewModels;
using Microsoft.Maui.Dispatching;
using Packo.Repositories.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Popups;
using Packo.Services;

namespace Packo.Views;

public partial class StartPage : ContentPage
{
    private readonly StartViewModel _viewModel;
    private readonly IDispatcherTimer _scheduleTimer;
    private readonly TripInvitationService _invitations;
    private readonly TripReminderService _reminders;
    private DateTime _nextAccessCheck;
    private bool _refreshRequested;
    private bool _refreshing;
    private bool _sharing;
    private static LocalizationResourceManager Strings => LocalizationResourceManager.Instance;

    public StartPage(StartViewModel viewModel, IPackingListRepository packingLists, TripInvitationService invitations, TripReminderService reminders)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _invitations = invitations;
        _reminders = reminders;
        BindingContext = _viewModel;
        PackingLists.Repository = packingLists;
        _scheduleTimer = Dispatcher.CreateTimer();
        _scheduleTimer.Interval = TimeSpan.FromSeconds(1);
        _scheduleTimer.Tick += async (_, _) =>
        {
            _viewModel.RefreshTripSchedule();
            if (_refreshRequested || (DateTime.UtcNow >= _nextAccessCheck && _viewModel.Trip?.IsShared == true))
                await RefreshSharingAsync();
        };
        WeakReferenceMessenger.Default.Register<StartPage, PackingSyncMessage>(this, (page, change) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (change.RemoteTripId == 0 || change.RemoteTripId == page._viewModel.Trip?.TripModel.RemoteTripId)
                    page._refreshRequested = true;
            }));
    }

    //protected override async void OnAppearing()
    //{
    //    base.OnAppearing();

    //    await _viewModel.InitializeAsync();
    //}

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.RefreshTripSchedule();
        _scheduleTimer.Start();

        await Task.WhenAll(PackingLists.ShowAsync(_viewModel.Trip?.TripModel), _viewModel.OnAppearingAsync());
        await PackingLists.UpdateTripAsync(_viewModel.Trip?.TripModel);
        UpdateShareButtons();
        await UpdateRemindersAsync();
    }

    protected override void OnDisappearing()
    {
        _scheduleTimer.Stop();
        base.OnDisappearing();
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        if (!_viewModel.RetryLoadCommand.CanExecute(null))
            return;
        await _viewModel.RetryLoadCommand.ExecuteAsync(null);
        await PackingLists.ShowAsync(_viewModel.Trip?.TripModel);
        UpdateShareButtons();
    }

    private void UpdateShareButtons()
    {
        var owner = _viewModel.Trip is { CanManageTrip: true };
        ShareTripButton.IsVisible = owner && _invitations.IsConfigured;
        ManageSharingButton.IsVisible = owner && _invitations.IsConfigured && _viewModel.Trip!.TripModel.RemoteTripId > 0;
    }

    private async Task UpdateRemindersAsync()
    {
        if (_viewModel.Trip?.TripModel is not { } trip) return;
        await _reminders.RefreshSafeAsync(force: false);
        var entries = await _reminders.GetAsync(trip);
        var active = entries.Count(x => x.Definition.Enabled && !x.Muted && x.Definition.IsRecipient(_reminders.UserId)
            && Packo.Helpers.ReminderSchedule.Next(x.Definition, trip, DateTimeOffset.UtcNow, Packo.Helpers.TripSchedule.DeviceTimeZone).HasValue);
        RemindersSummary.Text = active == 0 ? Strings["ReminderEmpty"]
            : string.Format(Strings["ReminderCount"], active);
    }

    private async void OnRemindersClicked(object? sender, EventArgs e)
    {
        if (_viewModel.Trip?.TripModel is not { } trip || _viewModel.Session.GlobalIsBusy) return;
        await ReminderListPopup.ShowAsync(this, _reminders, trip);
        await UpdateRemindersAsync();
    }

    private async Task RefreshSharingAsync()
    {
        if (_refreshing || _sharing || PackingLists.IsWorking || Shell.Current?.CurrentPage != this
            || _viewModel.Trip?.TripModel.RemoteTripId is not > 0
            || Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !_viewModel.Session.IsAuthenticated)
            return;
        _refreshing = true;
        _refreshRequested = false;
        _nextAccessCheck = DateTime.UtcNow.AddSeconds(20);
        try
        {
            await _viewModel.RetryLoadCommand.ExecuteAsync(null);
            await PackingLists.ShowAsync(_viewModel.Trip?.TripModel, forceRefresh: true);
            UpdateShareButtons();
        }
        finally { _refreshing = false; }
    }

    private async void OnShareTripClicked(object? sender, EventArgs e)
    {
        if (_viewModel.Trip is { CanManageTrip: true } trip)
            await RunSharingActionAsync(() => _invitations.ShareAsync(trip.TripModel));
    }

    private async void OnManageSharingClicked(object? sender, EventArgs e)
    {
        if (_viewModel.Trip is not { CanManageTrip: true } trip)
            return;
        await RunSharingActionAsync(async () =>
        {
            var members = trip.TripModel.Participants.Where(x => !x.IsOwner)
                .Select((member, i) => (member.UserId, Label: $"{i + 1}. {member.Name}")).ToList();
            var action = await OptionsPopup.ChooseAsync(Strings["ManageTripSharing"],
                [Strings["DisableInviteLinks"], .. members.Select(x => x.Label)]);
            if (action == Strings["DisableInviteLinks"])
            {
                if (await ConfirmPopup.AskAsync(action, Strings["DisableInviteLinksPrompt"]))
                    await _invitations.RevokeAsync(trip.TripModel.RemoteTripId, null);
            }
            else if (members.FirstOrDefault(x => x.Label == action) is { UserId: { } userId } member
                && await ConfirmPopup.AskAsync(Strings["RemoveTripMember"], member.Label, Strings["RemoveTripMember"], Strings["Cancel"]))
                await _invitations.RevokeAsync(trip.TripModel.RemoteTripId, userId);
        });
        _refreshRequested = true;
        await RefreshSharingAsync();
    }

    private async Task RunSharingActionAsync(Func<Task> action)
    {
        if (_sharing)
            return;
        _sharing = true;
        try
        {
            if (!_viewModel.Session.IsAuthenticated || Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                await ConfirmPopup.ShowMessageAsync(Strings["ShareTrip"], Strings["SharingRequiresSignIn"]);
                return;
            }
            await action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.GetType().Name);
            await ConfirmPopup.ShowMessageAsync(Strings["ShareTrip"], Strings["SharingRequestFailed"]);
        }
        finally { _sharing = false; }
    }

    //private async void StartButton_Clicked(object? sender, EventArgs e)
    //{
    //    await Shell.Current.GoToAsync("//TripList");
    //}
}
