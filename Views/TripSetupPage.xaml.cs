using Packo.Models.Pages;
using Packo.ViewModels;

namespace Packo.Views;

public partial class TripSetupPage : BasePage
{
    private readonly TripSetupViewModel _viewModel;

    public TripSetupPage(TripSetupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        await _viewModel.LoadTimeZonesAsync();
    }

    private void OnStartDateIconTapped(object sender, TappedEventArgs e)
    {
#if ANDROID
        (StartDatePicker.Handler?.PlatformView as Android.Views.View)?.PerformClick();
#else
        StartDatePicker.Focus();
#endif
    }

    private void OnEndDateIconTapped(object sender, TappedEventArgs e)
    {
#if ANDROID
        (EndDatePicker.Handler?.PlatformView as Android.Views.View)?.PerformClick();
#else
        EndDatePicker.Focus();
#endif
    }
}
