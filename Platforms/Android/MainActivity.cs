using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Packo.Services;
using Packo.Helpers;

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
            HandleReminder(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleInvite(intent);
            HandleReminder(intent);
        }

        private static void HandleInvite(Intent? intent)
        {
            if (intent?.Action == Intent.ActionView && Uri.TryCreate(intent.DataString, UriKind.Absolute, out var link))
                IPlatformApplication.Current?.Services.GetService<TripInvitationService>()?.Receive(link);
        }

        private static void HandleReminder(Intent? intent)
        {
            if (intent?.Action != ReminderNavigationService.OpenAction) return;
            if ((intent.Flags & ActivityFlags.LaunchedFromHistory) == 0)
                IPlatformApplication.Current?.Services.GetService<ReminderNavigationService>()?.Receive(new ReminderTripTarget(
                    intent.GetStringExtra(ReminderNavigationService.AccountExtra) ?? "",
                    intent.GetIntExtra(ReminderNavigationService.LocalTripExtra, 0),
                    intent.GetIntExtra(ReminderNavigationService.RemoteTripExtra, 0)));
            intent.SetAction(Intent.ActionMain);
            intent.RemoveExtra(ReminderNavigationService.AccountExtra);
            intent.RemoveExtra(ReminderNavigationService.LocalTripExtra);
            intent.RemoveExtra(ReminderNavigationService.RemoteTripExtra);
        }
    }
}


