using Packo.ViewModels;
using Microsoft.Maui.Dispatching;

namespace Packo.Views;

public partial class StartPage : ContentPage
{
    private readonly StartViewModel _viewModel;
    private readonly IDispatcherTimer _scheduleTimer;
    public StartPage(StartViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
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

        if (BindingContext is StartViewModel vm)
            await vm.OnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        _scheduleTimer.Stop();
        base.OnDisappearing();
    }

    //private async void StartButton_Clicked(object? sender, EventArgs e)
    //{
    //    await Shell.Current.GoToAsync("//TripList");
    //}
}
