using Packo.Models;
using Packo.Models.DTO;

namespace Packo.Helpers;

public static class ReminderEditorCalendar
{
    public static DateTime Today(TripReminder reminder, TripDTO trip, DateTimeOffset now, TimeZoneInfo deviceZone) =>
        TimeZoneInfo.ConvertTime(now, ReminderSchedule.OccurrenceZone(reminder, trip, deviceZone)).Date;

    public static bool PreserveStart(TripReminder initial, ReminderRepeat repeat, bool existing, bool changingStart, DateTime today) =>
        existing && !changingStart && initial.Timing == ReminderTiming.Date && initial.Repeat != ReminderRepeat.Once
        && repeat != ReminderRepeat.Once && initial.Date.Date < today.Date;
}
