using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Services.Interfaces;
using Packo.ViewModels;

internal static class TripGroupingChecks
{
    public static void Run()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var clock = new Clock(now);
        var list = new TripListViewModel(null!, null!, new Session(), null!, new LocalTripLookup(), null!, new Preload(), new Weather());
        var current = Trip("Current", now.AddHours(-1), now.AddHours(1), clock);
        var upcoming = Trip("Upcoming", now.AddDays(2), now.AddDays(3), clock);
        var next = Trip("Next", now.AddDays(1), now.AddDays(2), clock);
        var past = Trip("Past", now.AddDays(-3), now.AddDays(-2), clock);
        var older = Trip("Older", now.AddDays(-6), now.AddDays(-5), clock);
        list.Trips.AddRange([older, upcoming, current, past, next]);

        Check(list.TripGroups.Select(group => group.Status).SequenceEqual(
            [TripStatus.Current, TripStatus.Upcoming, TripStatus.Completed]),
            "Trip sections put current journeys before upcoming plans and past trips");
        Check(list.TripGroups[1].SequenceEqual([next, upcoming]) && list.TripGroups[2].SequenceEqual([past, older]),
            "Plans sort by the nearest departure and past trips sort with the most recent first");
        Check(ReferenceEquals(list.TripGroups[0][0], current),
            "Grouped tiles reuse the live trip objects for commands, reminders and packing updates");

        var plans = list.TripGroups[1];
        var titleChanged = false;
        ((System.ComponentModel.INotifyPropertyChanged)plans).PropertyChanged += (_, args) => titleChanged |= args.PropertyName == nameof(TripGroup.Title);
        Packo.Services.LocalizationResourceManager.Instance.ChangeLanguage();
        Check(titleChanged, "Changing the language also refreshes trip section headings");
        current.PackingSummary = "3 / 5";
        Check(list.TripGroups[0][0].PackingSummary == "3 / 5" && ReferenceEquals(list.TripGroups[1], plans),
            "Packing changes update the existing tiles without rebuilding section collections");

        list.Trips.Remove(past);
        Check(list.TripGroups[2].Count == 1, "Deleting a trip updates its section count immediately");
        list.Trips.Remove(older);
        Check(list.TripGroups.All(group => group.Status != TripStatus.Completed),
            "The past-trips section disappears when it has no trips");

        clock.Now = now.AddHours(2);
        list.RefreshTripSchedules();
        Check(list.TripGroups.All(group => group.Status != TripStatus.Current)
            && list.TripGroups.Single(group => group.Status == TripStatus.Completed).Single() == current,
            "A trip moves to the past section automatically when its end time passes");

        clock.Now = now.AddDays(1).AddHours(1);
        list.RefreshTripSchedules();
        Check(list.TripGroups[0].Status == TripStatus.Current && list.TripGroups[0].Single() == next,
            "A new current section appears automatically when a planned trip starts");

        var undated = Trip("Without dates", null, null, clock);
        list.Trips.Add(undated);
        Check(list.TripGroups.Single(group => group.Status == TripStatus.Upcoming).Contains(undated),
            "Trips without dates remain accessible among upcoming plans");
        list.Trips.Clear();
        Check(list.TripGroups.Count == 0, "Clearing trips also removes every empty section");
    }

    private static TripViewModel Trip(string name, DateTime? start, DateTime? end, Clock clock) => new(new TripDTO
    {
        Destination = name, StartDate = start, EndDate = end, TimeZoneId = "UTC"
    }, clock);

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private sealed class Weather : IWeatherPreloadService { public void Invalidate() { } }
}
