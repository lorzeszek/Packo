using Packo.Models.Enums;

namespace Packo.Helpers;

public static class TripSchedule
{
    public static TimeZoneInfo DeviceTimeZone
    {
        get
        {
#if ANDROID
            // The Android runtime can retain .NET's original Local zone after travel.
            // Read the platform's current zone instead of relying on that cached value.
            var id = global::Java.Util.TimeZone.Default?.ID;
            if (!string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.FindSystemTimeZoneById(id);
#endif
            return TimeZoneInfo.Local;
        }
    }
    public static string NormalizeZoneId(string id) => TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    // Trip timestamps are local wall-clock values, matching Supabase's timestamp columns.
    public static DateTime Combine(DateTime date, TimeSpan time) =>
        DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);

    public static bool IsValid(DateTime start, DateTime end) => end > start;

    public static string FormatRange(DateTime? start, DateTime? end)
    {
        if (!start.HasValue)
            return $"{end:dd MMM yyyy, HH:mm}";
        if (!end.HasValue)
            return $"{start:dd MMM yyyy, HH:mm}";

        return start.Value.Date == end.Value.Date
            ? $"{start:dd MMM yyyy} · {start:HH:mm}–{end:HH:mm}"
            : $"{start:dd MMM yyyy, HH:mm} – {end:dd MMM yyyy, HH:mm}";
    }

    public static TripStatus GetStatus(DateTime? start, DateTime? end, DateTime now)
    {
        if (end.HasValue && now >= end.Value)
            return TripStatus.Completed;
        if (start.HasValue && now < start.Value)
            return TripStatus.Upcoming;
        return start.HasValue ? TripStatus.Current : TripStatus.Unknown;
    }
}
