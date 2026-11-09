using Packo.ViewModels;
using Microsoft.Maui.Dispatching;
using Packo.Repositories.Interfaces;

namespace Packo.Views;

public partial class StartPage : ContentPage
{
    private readonly StartViewModel _viewModel;
    private readonly IDispatcherTimer _scheduleTimer;
    public StartPage(StartViewModel viewModel, IPackingListRepository packingLists)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
        PackingLists.Repository = packingLists;
        _scheduleTimer = Dispatcher.CreateTimer();
        _scheduleTimer.Interval = TimeSpan.FromSeconds(1);
        _scheduleTimer.Tick += (_, _) => _viewModel.RefreshTripSchedule();
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

        await _viewModel.OnAppearingAsync();
        await PackingLists.ShowAsync(_viewModel.Trip?.TripModel);
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
    }

    //private async void StartButton_Clicked(object? sender, EventArgs e)
    //{
    //    await Shell.Current.GoToAsync("//TripList");
    //}
}
