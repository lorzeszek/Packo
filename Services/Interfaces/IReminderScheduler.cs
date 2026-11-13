using Packo.Models;
using Packo.Models.DTO;

namespace Packo.Services.Interfaces;

public sealed class ScheduledReminder
{
    public string AccountId { get; set; } = "";
    public TripReminder Definition { get; set; } = new();
    public TripDTO Trip { get; set; } = new();
    public string? LastOccurrence { get; set; }
    public DateTimeOffset? NextAt { get; set; }
    public int NotificationId { get; set; }
}

public interface IReminderScheduler
{
    bool IsSupported { get; }
    bool HasExactTiming { get; }
    bool HasPermission { get; }
    Task<bool> RequestPermissionAsync();
    Task ReplaceAsync(string accountId, IReadOnlyList<ScheduledReminder> reminders);
    void OpenTimingSettings();
}
