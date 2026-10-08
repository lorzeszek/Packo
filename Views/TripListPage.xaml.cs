using Packo.Models.Pages;
using Packo.ViewModels;

namespace Packo.Views;

public partial class TripListPage : BasePage// ContentPage
{
    private readonly TripListViewModel _viewModel;
    private bool _suppressNextAppearing;

    public TripListPage(TripListViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

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

        if (_suppressNextAppearing)
        {
            _suppressNextAppearing = false;
            return;
        }
        if (BindingContext is TripListViewModel vm)
            await vm.OnAppearingAsync();
    }

    //private async void OnTripSelected(object sender, SelectionChangedEventArgs e)
    //{
    //    if (e.CurrentSelection.FirstOrDefault() is Trip selectedTrip)
    //    {
    //        await Navigation.PushAsync(new PackingListPage(selectedTrip));
    //    }
    //}
}
