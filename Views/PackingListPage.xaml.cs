using Packo.Models.Pages;
using Packo.ViewModels;
using Microsoft.Maui.Dispatching;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Popups;
using Packo.Services;

namespace Packo.Views;

public partial class PackingListPage : BasePage//ContentPage//, IQueryAttributable
{
    public const string EditorRoute = "PackingListEditor";
    private readonly PackingListViewModel _viewModel;
    private readonly IDispatcherTimer _scheduleTimer;
    private DateTime _nextRefresh;
    private bool _refreshRequested;
    private bool _checkingSync;
    private bool _accessRevoked;

    public PackingListPage(PackingListViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        _scheduleTimer = Dispatcher.CreateTimer();
        _scheduleTimer.Interval = TimeSpan.FromSeconds(1);
        _scheduleTimer.Tick += async (_, _) =>
        {
            _viewModel.RefreshTripSchedule();
            await CheckSyncAsync();
        };
        WeakReferenceMessenger.Default.Register<PackingListPage, PackingSyncMessage>(this, (page, change) =>
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (page._viewModel.CurrentTrip?.RemoteTripId is not > 0
                    || (change.RemoteTripId != 0 && change.RemoteTripId != page._viewModel.CurrentTrip.RemoteTripId))
                    return;
                page._refreshRequested = true;
                if (change.Conflict)
                    page.PackingSyncNotice.IsVisible = true;
                if (change.AccessRevoked)
                {
                    page._viewModel.RevokeAccess();
                    await page.ShowAccessRevokedAsync();
                }
            }));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.RefreshTripSchedule();
        _refreshRequested = true;
        _scheduleTimer.Start();
    }

    private async Task CheckSyncAsync()
    {
        if (_checkingSync || _accessRevoked || Shell.Current?.CurrentPage != this)
            return;
        _checkingSync = true;
        try
        {
            var strings = LocalizationResourceManager.Instance;
            var offline = Connectivity.Current.NetworkAccess != NetworkAccess.Internet;
            var (count, conflict) = await _viewModel.GetPackingSyncStatusAsync();
            if (conflict)
                PackingSyncNotice.IsVisible = true;
            PackingSyncStatus.Text = offline
                ? count > 0 ? strings["Offline"] + " · " + string.Format(strings["PackingSyncPending"], count) : strings["Offline"]
                : count > 0 ? string.Format(strings["PackingSyncPending"], count) : string.Empty;
            PackingSyncStatus.IsVisible = offline || count > 0;
            if (!offline && _viewModel.CanEditItems && (_refreshRequested || DateTime.UtcNow >= _nextRefresh))
            {
                _refreshRequested = false;
                _nextRefresh = DateTime.UtcNow.AddSeconds(20);
                if (!await _viewModel.RefreshCollaborationAsync())
                    await ShowAccessRevokedAsync();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        finally { _checkingSync = false; }
    }

    private async Task ShowAccessRevokedAsync()
    {
        if (_accessRevoked || Shell.Current?.CurrentPage != this)
            return;
        _accessRevoked = true;
        var strings = LocalizationResourceManager.Instance;
        await ConfirmPopup.ShowMessageAsync(strings["TripShared"], strings["TripAccessRevoked"]);
        await Shell.Current.GoToAsync("//TripList");
    }

    private void OnPackedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (sender is not CheckBox { BindingContext: Packo.Models.PackingItem item } checkBox
            || item.IsPacked == e.Value) return; // Ignore binding/realtime updates and recycled rows.
        if (!_viewModel.CanEditItems)
        {
            checkBox.IsChecked = item.IsPacked;
            return;
        }
        item.IsPacked = e.Value;
        _viewModel.ToggleIsPackedCommand.Execute(item);
    }

    //public async void ApplyQueryAttributes(IDictionary<string, object> query)
    //{
    //    if (query.TryGetValue("tripId", out var tripIdObj) && tripIdObj is string tripId)
    //    {
    //        await _viewModel.InitializeRealtimeAsync(tripId);
    //    }
    //}

    //public void ApplyQueryAttributes(IDictionary<string, object> query)
    //{
    //    if (query.TryGetValue("tripId", out var tripIdObj) && tripIdObj is string tripId)
    //    {
    //        _ = _viewModel.InitializeRealtimeAsync(tripId);
    //    }
    //}

    private async void OnBackButtonClicked(object sender, EventArgs e)
    {
        if (Shell.Current.Navigation.NavigationStack.Count > 1)
        {
            await Shell.Current.GoToAsync("..");
        }
    }

    protected override async void OnDisappearing()
    {
        _scheduleTimer.Stop();
        base.OnDisappearing();
        await _viewModel.DisposeRealtimeAsync();
    }

    //private void OnNewItemCompleted(object sender, EventArgs e)
    //{
    //    if (BindingContext is PackingListViewModel vm)
    //    {
    //        vm.AddItemCommand.
    //        vm.AddItemCommand();
    //        vm.NewItemEntry = string.Empty;
    //    }
    //}

    //private void OnNewItemCompleted(object sender, EventArgs e)
    //{
    //    if (sender is Entry entry)
    //    {
    //        var text = entry.Text?.Trim();
    //        if (!string.IsNullOrEmpty(text))
    //        {
    //            // zakładam że BindingContext = Twój ViewModel
    //            if (BindingContext is PackingListViewModel vm)
    //            {
    //                vm.Items.Add(new PackingItem
    //                {
    //                    Name = text,
    //                    IsPacked = false
    //                });
    //            }

    //            // wyczyść pole
    //            entry.Text = string.Empty;
    //        }
    //    }
    //}
}
