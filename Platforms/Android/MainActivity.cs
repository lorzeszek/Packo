using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Packo.Services;

namespace Packo
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter([Intent.ActionView], AutoVerify = true, Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
        DataScheme = "https", DataHost = "${packoInviteHost}", DataPathPrefix = "/join")]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            HandleInvite(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleInvite(intent);
        }

        private static void HandleInvite(Intent? intent)
        {
            if (intent?.Action == Intent.ActionView && Uri.TryCreate(intent.DataString, UriKind.Absolute, out var link))
                IPlatformApplication.Current?.Services.GetService<TripInvitationService>()?.Receive(link);
        }
    }
}


