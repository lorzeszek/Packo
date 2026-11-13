using Packo.Models;
using Packo.Models.DTO;

namespace Packo.Helpers;

/// <summary>Calendar arithmetic is kept separate from platform alarms and storage.</summary>
public static class ReminderSchedule
{
    public static TimeZoneInfo Zone(string? id) => TimeZoneInfo.FindSystemTimeZoneById(
        string.IsNullOrWhiteSpace(id) ? Packo.Helpers.TripSchedule.DeviceTimeZone.Id : id);

    public static DateTimeOffset Instant(DateTime wallTime, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified);
        // A spring-forward gap moves to the first valid minute. In an autumn overlap,
        // select the first occurrence, so the same calendar occurrence cannot fire twice.
        for (var i = 0; zone.IsInvalidTime(wall) && i < 180; i++) wall = wall.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    public static TimeZoneInfo TripZone(TripReminder reminder, TripDTO trip) => Zone(trip.TimeZoneId ?? reminder.TimeZoneId);

    public static TimeZoneInfo OccurrenceZone(TripReminder reminder, TripDTO trip, TimeZoneInfo deviceZone) =>
        reminder.Timing != ReminderTiming.Date || reminder.Clock == ReminderClock.Trip
            ? TripZone(reminder, trip) : deviceZone;

    public static DateTime FirstWallTime(TripReminder reminder, TripDTO trip) => reminder.Timing switch
    {
        ReminderTiming.HoursBefore => TimeZoneInfo.ConvertTime(Instant(trip.StartDate ?? throw new ArgumentException("ReminderNeedsDates"),
            TripZone(reminder, trip)).AddHours(-reminder.Offset), TripZone(reminder, trip)).DateTime,
        ReminderTiming.DaysBefore => (trip.StartDate ?? throw new ArgumentException("ReminderNeedsDates")).Date
            .AddDays(-reminder.Offset).AddMinutes(reminder.MinuteOfDay),
        _ => reminder.Date.Date.AddMinutes(reminder.MinuteOfDay)
    };

    public static DateTimeOffset? Next(TripReminder reminder, TripDTO trip, DateTimeOffset after,
        TimeZoneInfo deviceZone, string? lastOccurrence = null)
    {
        if (!reminder.Enabled || reminder.Deleted || trip.IsInTrash || trip.EndDate is null) return null;
        var zone = OccurrenceZone(reminder, trip, deviceZone);
        var end = Instant(trip.EndDate.Value, TripZone(reminder, trip));
        if (end <= after) return null;
        var first = FirstWallTime(reminder, trip);
        var step = reminder.Repeat == ReminderRepeat.Weekly ? 7 : 1;
        var nowWall = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var days = reminder.Repeat == ReminderRepeat.Once ? 0 : Math.Max(0, (nowWall.Date - first.Date).Days / step * step);
        // At most the current and next calendar occurrence are normally considered;
        // extra iterations handle a travelled-back date that has already been delivered.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var wall = first.AddDays(days);
            if (reminder.RepeatUntil.HasValue && wall.Date > reminder.RepeatUntil.Value.Date) return null;
            var instant = reminder.Timing == ReminderTiming.HoursBefore && days == 0
                ? Instant(trip.StartDate!.Value, TripZone(reminder, trip)).AddHours(-reminder.Offset)
                : Instant(wall, zone);
            if (instant > end) return null;
            if (instant > after && OccurrenceKey(reminder, wall) != lastOccurrence) return instant;
            if (reminder.Repeat == ReminderRepeat.Once) return null;
            days += step;
        }
        return null;
    }

    public static string OccurrenceKey(TripReminder reminder, DateTime wall) =>
        reminder.Repeat == ReminderRepeat.Once
            ? "once:" + wall.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture) + ":" + reminder.TimeZoneId
            : wall.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string KeyAt(TripReminder reminder, TripDTO trip, DateTimeOffset instant, TimeZoneInfo deviceZone) =>
        OccurrenceKey(reminder, TimeZoneInfo.ConvertTime(instant, OccurrenceZone(reminder, trip, deviceZone)).DateTime);

    public static string? Validate(TripReminder reminder, TripDTO trip, DateTimeOffset now, TimeZoneInfo deviceZone)
    {
        if (string.IsNullOrWhiteSpace(reminder.Title) || reminder.Title.Trim().Length > 80
            || string.IsNullOrWhiteSpace(reminder.Body) || reminder.Body.Trim().Length > 500) return "ReminderTextError";
        if (!Enum.IsDefined(reminder.Timing) || !Enum.IsDefined(reminder.Repeat)
            || !Enum.IsDefined(reminder.Clock) || !Enum.IsDefined(reminder.Audience)
            || reminder.MinuteOfDay is < 0 or > 1439 || reminder.Offset is < 0 or > 8760) return "ReminderScheduleError";
        if (trip.StartDate is null || trip.EndDate is null || trip.EndDate <= trip.StartDate) return "ReminderNeedsDates";
        if (reminder.Audience == ReminderAudience.Selected && reminder.RecipientIds.Count == 0) return "ReminderSelectPeople";
        try
        {
            _ = Zone(reminder.TimeZoneId);
            var first = reminder.Timing == ReminderTiming.HoursBefore
                ? Instant(trip.StartDate!.Value, TripZone(reminder, trip)).AddHours(-reminder.Offset)
                : Instant(FirstWallTime(reminder, trip), OccurrenceZone(reminder, trip, deviceZone));
            if (reminder.RepeatUntil?.Date < FirstWallTime(reminder, trip).Date) return "ReminderScheduleError";
            if (reminder.Repeat == ReminderRepeat.Once && first <= now) return "ReminderPast";
            if (Next(reminder, trip, now, deviceZone) is null) return "ReminderOutsideTrip";
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return "ReminderScheduleError"; }
        return null;
    }

    public static bool Collides(TripReminder candidate, TripReminder other, TripDTO trip,
        DateTimeOffset now, TimeZoneInfo deviceZone)
    {
        if (candidate.Id == other.Id || !other.Enabled || other.Deleted) return false;
        // An intersection can only be warned about if at least one recipient is shared.
        var overlap = candidate.Audience == ReminderAudience.All || other.Audience == ReminderAudience.All
            || candidate.IsRecipient(other.AuthorId) || other.IsRecipient(candidate.AuthorId)
            || candidate.RecipientIds.Intersect(other.RecipientIds).Any();
        if (!overlap) return false;
        var a = Next(candidate, trip, now, deviceZone);
        var b = Next(other, trip, now, deviceZone);
        // Weekly/daily intersections occur within seven days after both series begin.
        var iterations = 0;
        while (a.HasValue && b.HasValue && iterations++ < 100000)
        {
            if (a == b) return true;
            if (a < b) a = Next(candidate, trip, b.Value.AddTicks(-1), deviceZone);
            else b = Next(other, trip, a.Value.AddTicks(-1), deviceZone);
        }
        return false;
    }
}
