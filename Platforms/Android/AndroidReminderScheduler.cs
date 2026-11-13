using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Packo.Helpers;
using Packo.Services.Interfaces;
using System.Text.Json;
using AApplication = Android.App.Application;

namespace Packo.Platforms.Android;

public sealed class AndroidReminderScheduler : IReminderScheduler
{
    private const string ChannelId = "trip_reminders";
    private const string AlarmAction = "com.travelset.app.TRIP_REMINDER";
    private static readonly object StateGate = new();
    private static Context Context => AApplication.Context;
    private static string StatePath => Path.Combine(Context.NoBackupFilesDir!.AbsolutePath, "trip-reminders.json");
    private static AlarmManager Alarms => (AlarmManager)Context.GetSystemService(Context.AlarmService)!;
    public bool IsSupported => true;
    public bool HasExactTiming => !OperatingSystem.IsAndroidVersionAtLeast(31) || Alarms.CanScheduleExactAlarms();
    public bool HasPermission => NotificationManagerCompat.From(Context)!.AreNotificationsEnabled()
        && (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu
            || Context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") == global::Android.Content.PM.Permission.Granted);

    public async Task<bool> RequestPermissionAsync()
    {
        EnsureChannel();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            await Permissions.RequestAsync<ReminderNotificationPermission>();
        return HasPermission;
    }

    public void OpenTimingSettings()
    {
        var intent = Build.VERSION.SdkInt >= BuildVersionCodes.S
            ? new Intent("android.settings.REQUEST_SCHEDULE_EXACT_ALARM", global::Android.Net.Uri.Parse("package:" + Context.PackageName))
            : new Intent("android.settings.APP_NOTIFICATION_SETTINGS").PutExtra("android.provider.extra.APP_PACKAGE", Context.PackageName);
        intent.AddFlags(ActivityFlags.NewTask);
        Context.StartActivity(intent);
    }

    public Task ReplaceAsync(string accountId, IReadOnlyList<ScheduledReminder> reminders)
    {
        lock (StateGate)
        {
            var state = ReadState();
            foreach (var old in state.Entries) Cancel(old, state.AccountId != accountId
                || !reminders.Any(x => x.Definition.Id == old.Definition.Id));
            var oldEntries = state.AccountId == accountId ? state.Entries : [];
            state.AccountId = accountId;
            state.Entries = reminders.ToList();
            foreach (var item in state.Entries)
                item.NotificationId = oldEntries.FirstOrDefault(x => x.Definition.Id == item.Definition.Id)?.NotificationId ?? state.NextId++;
            Reschedule(state);
        }
        return Task.CompletedTask;
    }

    internal static void Receive(Intent intent)
    {
        lock (StateGate)
        {
            var state = ReadState();
            if (intent.Action != AlarmAction)
            {
                foreach (var item in state.Entries) Cancel(item, false);
                if (intent.Action == Intent.ActionTimezoneChanged && intent.GetStringExtra("time-zone") is { Length: > 0 } zoneId)
                    Java.Util.TimeZone.Default = Java.Util.TimeZone.GetTimeZone(zoneId);
                TimeZoneInfo.ClearCachedData();
                Reschedule(state);
                return;
            }
            var itemToShow = state.Entries.FirstOrDefault(x => x.Definition.Id == intent.GetStringExtra("reminder_id")
                && x.AccountId == state.AccountId && x.AccountId == intent.GetStringExtra("account_id"));
            if (itemToShow?.NextAt is not { } next || next.ToUnixTimeMilliseconds() != intent.GetLongExtra("scheduled_at", -1)) return;
            var now = DateTimeOffset.UtcNow;
            // Process all due entries together. Rescheduling after only one of two
            // simultaneous alarms would otherwise skip the other one's occurrence.
            foreach (var due in state.Entries.Where(x => x.NextAt.HasValue && x.NextAt <= now))
            {
                var at = due.NextAt!.Value;
                var key = ReminderSchedule.KeyAt(due.Definition, due.Trip, at, Packo.Helpers.TripSchedule.DeviceTimeZone);
                var history = History(state, due);
                var end = due.Trip.EndDate is { } date
                    ? ReminderSchedule.Instant(date, ReminderSchedule.TripZone(due.Definition, due.Trip)) : DateTimeOffset.MinValue;
                if (now <= end && now - at < TimeSpan.FromMinutes(15) && !history.Contains(key))
                {
                    history.Add(key);
                    WriteState(state);
                    ShowNotification(due);
                }
            }
            Reschedule(state);
        }
    }

    private static void Reschedule(NativeState state)
    {
        foreach (var item in state.Entries)
        {
            try
            {
                var after = DateTimeOffset.UtcNow;
                var history = History(state, item);
                item.NextAt = ReminderSchedule.Next(item.Definition, item.Trip, after, Packo.Helpers.TripSchedule.DeviceTimeZone);
                while (item.NextAt is { } next && history.Contains(ReminderSchedule.KeyAt(item.Definition, item.Trip, next, Packo.Helpers.TripSchedule.DeviceTimeZone)))
                    item.NextAt = ReminderSchedule.Next(item.Definition, item.Trip, next, Packo.Helpers.TripSchedule.DeviceTimeZone);
            }
            catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { item.NextAt = null; }
        }
        // Store first: an alarm may be delivered immediately after registration.
        WriteState(state);
        foreach (var item in state.Entries.Where(x => x.NextAt.HasValue))
        {
            var pending = AlarmIntent(item);
            var milliseconds = item.NextAt!.Value.ToUnixTimeMilliseconds();
            try
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(31) || Alarms.CanScheduleExactAlarms())
                    Alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, milliseconds, pending!);
                else Alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, milliseconds, pending!);
            }
            catch (Java.Lang.SecurityException)
            { Alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, milliseconds, pending!); }
        }
    }

    private static List<string> History(NativeState state, ScheduledReminder item)
    {
        var historyKey = item.AccountId + ":" + item.Definition.Id;
        if (!state.Delivered.TryGetValue(historyKey, out var history))
            state.Delivered[historyKey] = history = [];
        return history;
    }

    private static PendingIntent? AlarmIntent(ScheduledReminder item) => PendingIntent.GetBroadcast(Context,
        item.NotificationId, new Intent(Context, typeof(ReminderAlarmReceiver)).SetAction(AlarmAction)
            .SetData(global::Android.Net.Uri.Parse($"travelset://reminder/{item.AccountId}/{item.Definition.Id}"))
            .PutExtra("account_id", item.AccountId).PutExtra("reminder_id", item.Definition.Id)
            .PutExtra("scheduled_at", item.NextAt?.ToUnixTimeMilliseconds() ?? -1),
        PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

    private static void Cancel(ScheduledReminder item, bool cancelNotification = true)
    {
        var pending = AlarmIntent(item);
        if (pending is not null) { Alarms.Cancel(pending); pending.Cancel(); }
        if (cancelNotification) NotificationManagerCompat.From(Context)!.Cancel(item.NotificationId);
    }

    private static void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(ChannelId, "TravelSet · " +
            Packo.Services.LocalizationResourceManager.Instance["Reminders"], NotificationImportance.Default);
        ((NotificationManager)Context.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(channel);
    }

    private static void ShowNotification(ScheduledReminder item)
    {
        EnsureChannel();
        if (!NotificationManagerCompat.From(Context)!.AreNotificationsEnabled()) return;
        var open = new Intent(Context, typeof(MainActivity)).SetAction("com.travelset.app.OPEN_REMINDER")
            .AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        var pending = PendingIntent.GetActivity(Context, item.NotificationId, open,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = new NotificationCompat.Builder(Context, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.ic_trip_reminder);
        builder.SetContentTitle(item.Definition.Title);
        builder.SetContentText(item.Definition.Body);
        builder.SetSubText(item.Trip.Destination);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(item.Definition.Body));
        builder.SetContentIntent(pending);
        builder.SetAutoCancel(true);
        builder.SetVisibility((int)NotificationVisibility.Private);
        var notification = builder.Build();
        try { NotificationManagerCompat.From(Context)!.Notify(item.NotificationId, notification); }
        catch (Java.Lang.SecurityException) { /* Permission may have been revoked since scheduling. */ }
    }

    private static NativeState ReadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<NativeState>(File.ReadAllText(StatePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    private static void WriteState(NativeState state)
    {
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, StatePath, true);
    }

    private sealed class NativeState
    {
        public string AccountId { get; set; } = "";
        public int NextId { get; set; } = 10000;
        public List<ScheduledReminder> Entries { get; set; } = [];
        public Dictionary<string, List<string>> Delivered { get; set; } = [];
    }
}

public sealed class ReminderNotificationPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu ? [("android.permission.POST_NOTIFICATIONS", true)] : [];
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class ReminderAlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent is not null) AndroidReminderScheduler.Receive(intent);
    }
}

[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionTimezoneChanged, Intent.ActionTimeChanged,
    "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED", Intent.ActionMyPackageReplaced])]
public sealed class ReminderClockReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is Intent.ActionBootCompleted or Intent.ActionTimezoneChanged or Intent.ActionTimeChanged
            or "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED" or Intent.ActionMyPackageReplaced)
            AndroidReminderScheduler.Receive(intent);
    }
}
