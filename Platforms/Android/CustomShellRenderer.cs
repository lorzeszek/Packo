using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;

namespace Packo.Platforms.Android
{
    public class CustomShellRenderer : ShellRenderer
    {
        protected override IShellItemRenderer CreateShellItemRenderer(ShellItem item)
        {
            return new TripShellItemRenderer(this);
        }

        protected override IShellToolbarTracker CreateTrackerForToolbar(AndroidX.AppCompat.Widget.Toolbar toolbar)
        {
            toolbar.ContentInsetStartWithNavigation = 0;
            toolbar.SetContentInsetsAbsolute(0, 0);
            return base.CreateTrackerForToolbar(toolbar);
        }

        private sealed class TripShellItemRenderer(IShellContext shellContext) : ShellItemRenderer(shellContext)
        {
            private bool _isReturningToTrips;

            protected override async void OnTabReselected(ShellSection shellSection)
            {
                base.OnTabReselected(shellSection);

                if (shellSection.CurrentItem?.Route != "TripList"
                    || shellSection.Navigation.NavigationStack.Count <= 1
                    || _isReturningToTrips)
                    return;

                try
                {
                    _isReturningToTrips = true;
                    await shellSection.Navigation.PopToRootAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Returning to Trips failed: {ex}");
                }
                finally
                {
                    _isReturningToTrips = false;
                }
            }
        }
    }
}
