using Packo.Services.Interfaces;

namespace Packo.Services;

// Desktop can edit/sync configurations. Native delivery currently targets Android.
public sealed class UnsupportedReminderScheduler : IReminderScheduler
{
    public bool IsSupported => false;
    public bool HasExactTiming => false;
    public bool HasPermission => false;
    public Task<bool> RequestPermissionAsync() => Task.FromResult(false);
    public Task ReplaceAsync(string accountId, IReadOnlyList<ScheduledReminder> reminders) => Task.CompletedTask;
    public void OpenTimingSettings() { }
}
