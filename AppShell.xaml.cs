using Packo.Views;
using Packo.Services;

namespace Packo
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            LocalizationResourceManager.Instance.PropertyChanged += OnLanguageChanged;

            Routing.RegisterRoute(nameof(TripSetupPage), typeof(TripSetupPage));
            Routing.RegisterRoute(nameof(PackingListPage), typeof(PackingListPage));

            Navigating += OnShellNavigating;
        }

        private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var strings = LocalizationResourceManager.Instance;
                Title = "Packo";
                var tabs = Items.OfType<TabBar>().FirstOrDefault();
                if (tabs is null || tabs.Items.Count < 5)
                    return;

                tabs.Items[0].Title = strings["TabHome"];
                tabs.Items[1].Title = strings["TabTrips"];
                tabs.Items[2].Title = strings["TabWeather"];
                tabs.Items[3].Title = strings["TabDocs"];
                tabs.Items[4].Title = strings["TabSettings"];
            });
        }

        private async void OnShellNavigating(object? sender, ShellNavigatingEventArgs e)
        {
            if (e.Source == ShellNavigationSource.ShellSectionChanged
                && Current.Navigation.NavigationStack.Count > 1)
            {
                var deferral = e.GetDeferral();
                await Current.Navigation.PopToRootAsync(animated: false);
                deferral.Complete();
            }
        }
    }
}
