namespace Packo.Models;

public enum ReminderTiming { Date, HoursBefore, DaysBefore }
public enum ReminderRepeat { Once, Daily, Weekly }
public enum ReminderClock { Device, Trip }
public enum ReminderAudience { Self, All, Selected }

public sealed class TripReminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public long Version { get; set; }
    public string AuthorId { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public ReminderTiming Timing { get; set; }
    public ReminderRepeat Repeat { get; set; }
    public ReminderClock Clock { get; set; }
    public ReminderAudience Audience { get; set; }
    public List<string> RecipientIds { get; set; } = [];
    public DateTime Date { get; set; } = DateTime.Today;
    public int MinuteOfDay { get; set; } = 18 * 60;
    public int Offset { get; set; } = 1;
    public DateTime? RepeatUntil { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool Enabled { get; set; } = true;
    public bool Deleted { get; set; }

    public TripReminder Copy() => System.Text.Json.JsonSerializer.Deserialize<TripReminder>(
        System.Text.Json.JsonSerializer.Serialize(this))!;

    public bool IsRecipient(string userId) => Audience == ReminderAudience.All || (Audience == ReminderAudience.Self && AuthorId == userId)
        || (Audience == ReminderAudience.Selected && RecipientIds.Contains(userId));
}
