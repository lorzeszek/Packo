using Packo.ViewModels;

using CommunityToolkit.Maui.Extensions;

namespace Packo.Models.Pages
{
    public partial class BasePage : ContentPage
    {
        protected override async void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            // A popup covers the page temporarily; keep its subscriptions alive.
            if (!args.IsDestinationPageACommunityToolkitPopupPage() && BindingContext is BaseViewModel vm)
            {
                _ = vm.OnNavigatedFromAsync(new Dictionary<string, object>());
            }

            base.OnNavigatedFrom(args);
        }
    }
}
