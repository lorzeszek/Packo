using Packo.Models;
using Packo.Models.DTO;
using Packo.Services.Interfaces;

namespace Packo.Helpers;

/// <summary>Retains alarms already handed to Android until delivery or an explicit change.</summary>
public static class ReminderDelivery
{
    public static bool IsActive(TripReminder reminder, TripDTO trip, DateTimeOffset now) =>
        reminder.Enabled && !reminder.Deleted && !trip.IsInTrash && trip.EndDate is { } end
        && now <= ReminderSchedule.Instant(end, ReminderSchedule.TripZone(reminder, trip));

    public static bool CanDeliver(ScheduledReminder item, DateTimeOffset at, DateTimeOffset now) =>
        at <= now && IsActive(item.Definition, item.Trip, now);

    public static DateTimeOffset? PendingFrom(ScheduledReminder? previous, ScheduledReminder current)
    {
        if (previous is null || previous.AccountId != current.AccountId
            || previous.Definition.Id != current.Definition.Id) return null;
        var a = previous.Definition;
        var b = current.Definition;
        return a.Timing == b.Timing && a.Date.Date == b.Date.Date && a.MinuteOfDay == b.MinuteOfDay
            && a.Offset == b.Offset && a.Repeat == b.Repeat && a.Clock == b.Clock
            && a.RepeatUntil?.Date == b.RepeatUntil?.Date && a.TimeZoneId == b.TimeZoneId
            && previous.Trip.StartDate == current.Trip.StartDate && previous.Trip.EndDate == current.Trip.EndDate
            && previous.Trip.TimeZoneId == current.Trip.TimeZoneId ? previous.NextAt : null;
    }

    public static DateTimeOffset? Next(ScheduledReminder item, DateTimeOffset now, TimeZoneInfo deviceZone,
        IReadOnlyCollection<string> delivered, DateTimeOffset? pending = null)
    {
        // An inexact Android alarm can arrive well after its requested time. A sync
        // must not replace that pending occurrence with tomorrow's (or discard a one-off).
        if (pending is { } at && CanDeliver(item, at, now)
            && !delivered.Contains(ReminderSchedule.KeyAt(item.Definition, item.Trip, at, deviceZone))) return at;
        var next = ReminderSchedule.Next(item.Definition, item.Trip, now, deviceZone);
        while (next is { } occurrence
            && delivered.Contains(ReminderSchedule.KeyAt(item.Definition, item.Trip, occurrence, deviceZone)))
            next = ReminderSchedule.Next(item.Definition, item.Trip, occurrence, deviceZone);
        return next;
    }
}
