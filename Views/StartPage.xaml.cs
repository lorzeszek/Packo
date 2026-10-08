using Packo.ViewModels;

namespace Packo.Views;

public partial class StartPage : ContentPage
{
    private readonly StartViewModel _viewModel;
    public StartPage(StartViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    //protected override async void OnAppearing()
    //{
    //    base.OnAppearing();

    //    await _viewModel.InitializeAsync();
    //}

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (BindingContext is StartViewModel vm)
            await vm.OnAppearingAsync();
    }

    //private async void StartButton_Clicked(object? sender, EventArgs e)
    //{
    //    await Shell.Current.GoToAsync("//TripList");
    //}
}