using Packo.Models.Enums;

namespace Packo.Helpers;

public static class TripSchedule
{
    // Trip timestamps are local wall-clock values, matching Supabase's timestamp columns.
    public static DateTime Combine(DateTime date, TimeSpan time) =>
        DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);

    public static bool IsValid(DateTime start, DateTime end) => end > start;

    public static TripStatus GetStatus(DateTime? start, DateTime? end, DateTime now)
    {
        if (end.HasValue && now >= end.Value)
            return TripStatus.Completed;
        if (start.HasValue && now < start.Value)
            return TripStatus.Upcoming;
        return start.HasValue ? TripStatus.Current : TripStatus.Unknown;
    }
}
