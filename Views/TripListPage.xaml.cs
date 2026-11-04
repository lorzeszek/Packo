using Packo.Models.Pages;
using Packo.ViewModels;
using Microsoft.Maui.Dispatching;

namespace Packo.Views;

public partial class TripListPage : BasePage// ContentPage
{
    private readonly TripListViewModel _viewModel;
    private bool _suppressNextAppearing;
    private readonly IDispatcherTimer _scheduleTimer;

    public TripListPage(TripListViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        _scheduleTimer = Dispatcher.CreateTimer();
        _scheduleTimer.Interval = TimeSpan.FromSeconds(1);
        _scheduleTimer.Tick += (_, _) => _viewModel.RefreshTripSchedules();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        _scheduleTimer.Stop();

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

        if (_suppressNextAppearing)
        {
            _suppressNextAppearing = false;
            return;
        }
        if (BindingContext is TripListViewModel vm)
            await vm.OnAppearingAsync();
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
