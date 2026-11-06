using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Packo.Helpers;
using Packo.Models.DTO;
using Packo.Models.Enums;
using Packo.Models.Supabase;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using Packo.Services.ResponseObjects;
using Packo.ViewModels;

internal static class TripScheduleChecks
{
    public static async Task RunAsync()
    {
        var day = new DateTime(2026, 11, 4);
        var clock = new Clock(day.AddHours(8));
        var morning = new TripViewModel(Trip(day.AddHours(9), day.AddHours(11)), clock);
        var afternoon = new TripViewModel(Trip(day.AddHours(16), day.AddHours(19)), clock);
        Check(morning.Status == TripStatus.Upcoming && afternoon.Status == TripStatus.Upcoming,
            "Two trips on the same day remain upcoming before their individual start times");
        Check(morning.TripCountdown.Contains("09:00"), "An upcoming trip today shows its actual start time");
        var notifications = new HashSet<string?>();
        morning.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        clock.Now = day.AddHours(9);
        morning.RefreshSchedule();
        afternoon.RefreshSchedule();
        Check(morning.Status == TripStatus.Current && afternoon.Status == TripStatus.Upcoming,
            "Exactly at the start only the first trip becomes current");
        Check(morning.TripCountdown.Contains("11:00"), "A current trip ending today shows its actual end time");
        Check(notifications.Contains(nameof(TripViewModel.StatusLabel)) && notifications.Contains(nameof(TripViewModel.IsCompleted)),
            "Clock updates notify the status bindings without reopening the screen");
        clock.Now = day.AddHours(11);
        morning.RefreshSchedule();
        afternoon.RefreshSchedule();
        Check(morning.IsCompleted && morning.TripCountdown == "TripEnded" && afternoon.Status == TripStatus.Upcoming,
            "Exactly at the end a same-day trip is completed while the later trip stays upcoming");
        clock.Now = day.AddHours(16);
        afternoon.RefreshSchedule();
        Check(afternoon.Status == TripStatus.Current && afternoon.StatusLabel == "TripStatusCurrent",
            "The afternoon trip becomes current at its own start time");
        clock.Now = day.AddHours(19);
        afternoon.RefreshSchedule();
        Check(afternoon.IsCompleted && afternoon.StatusLabel == "PastTrip",
            "The second trip receives the same completed label used by the list and details");
        Check(morning.TripDatesRange.Contains("09:00") && morning.TripDatesRange.Contains("11:00"),
            "Trip summaries show both times for trips on a single date");
        var crossYear = new TripViewModel(Trip(new(2026, 12, 31, 23, 30, 0), new(2027, 1, 1, 1, 15, 0)), clock);
        Check(crossYear.TripDatesRange.Contains("2026") && crossYear.TripDatesRange.Contains("2027")
            && crossYear.TripDatesRange.Contains("23:30") && crossYear.TripDatesRange.Contains("01:15"),
            "Overnight trips spanning years retain both dates and times");
        var endOnly = TripSchedule.FormatRange(null, day.AddHours(11));
        Check(TripSchedule.FormatRange(null, null) == string.Empty && endOnly.Contains("11:00")
            && endOnly == TripSchedule.FormatRange(day.AddHours(11), null),
            "Trips with incomplete schedules show only the date that is available");
        Check(TripSchedule.GetStatus(null, null, clock.Now) == TripStatus.Unknown,
            "Incomplete historical dates cannot be labelled as a current trip");

        var repository = new Trips();
        var classification = new Classification();
        var form = Form(repository, classification);
        Check(form.StartTime == TimeSpan.Zero && form.EndTime == new TimeSpan(23, 59, 0),
            "New trips retain the familiar full-day defaults");
        form.Destination = "Same-day trip";
        form.StartDate = DateTime.Today.AddDays(2);
        form.EndDate = form.StartDate;
        form.StartTime = new(9, 15, 0);
        form.EndTime = new(17, 45, 0);
        Check(form.CreateTripCommand.CanExecute(null) && !form.HasScheduleError,
            "A same-day trip with a later end time can be saved");
        form.EndTime = form.StartTime;
        Check(!form.CreateTripCommand.CanExecute(null) && form.HasScheduleError,
            "Equal start and end times disable saving and expose a validation message");
        form.EndTime = new(8, 0, 0);
        Check(!form.CreateTripCommand.CanExecute(null), "An end earlier than the start is rejected");
        form.EndTime = new(17, 45, 0);
        await form.CreateTripCommand.ExecuteAsync(null);
        Check(repository.Added!.StartDate == form.StartDate.AddHours(9).AddMinutes(15)
            && repository.Added.EndDate == form.StartDate.AddHours(17).AddMinutes(45)
            && classification.Start == repository.Added.StartDate && classification.End == repository.Added.EndDate,
            "Creation passes the selected timestamps to storage and trip classification");
        Check(Shell.Current.Route == "PackingListPage" && ReferenceEquals(Shell.Current.Query?["trip"], repository.Added)
            && repository.Added.LocalTripId == 1,
            "Creation opens the saved trip's packing list with its assigned local identity");

        var existing = Trip(day.AddHours(13).AddMinutes(20), day.AddHours(18).AddMinutes(50));
        var edit = Form(repository, classification);
        edit.ApplyQueryAttributes(new Dictionary<string, object> { ["editTrip"] = existing });
        Check(edit.StartTime == new TimeSpan(13, 20, 0) && edit.EndTime == new TimeSpan(18, 50, 0),
            "Editing restores existing clock times instead of resetting them to full-day defaults");
        edit.StartTime = new(14, 10, 0);
        Check(existing.StartDate!.Value.TimeOfDay == new TimeSpan(13, 20, 0),
            "Changing an unsaved time does not mutate the trip being displayed");
        await edit.CreateTripCommand.ExecuteAsync(null);
        Check(repository.Updated!.StartDate!.Value.TimeOfDay == new TimeSpan(14, 10, 0)
            && repository.Updated.EndDate!.Value.TimeOfDay == new TimeSpan(18, 50, 0),
            "Saving an edit persists both the changed start time and the unchanged end time");
        var legacy = Trip(day, day.AddHours(23).AddMinutes(59));
        edit.ApplyQueryAttributes(new Dictionary<string, object> { ["editTrip"] = legacy });
        Check(edit.StartDateTime == legacy.StartDate && edit.EndDateTime == legacy.EndDate,
            "Historical 00:00–23:59 trips keep their original timestamps");

        var remote = Mappers.MapToTripSupabase(repository.Updated);
        Check(remote.StartDate!.Value.Kind == DateTimeKind.Unspecified && remote.EndDate!.Value.Kind == DateTimeKind.Unspecified,
            "Supabase timestamp fields are sent as wall-clock values without a false UTC marker");
        var settings = Supabase.Postgrest.Client.SerializerSettings(new Supabase.Postgrest.ClientOptions());
        var json = JsonConvert.SerializeObject(remote, settings);
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        var payload = JObject.Load(reader);
        Check(payload["StartDate"]!.Value<string>()!.Contains("14:10:00")
            && !payload["StartDate"]!.Value<string>()!.EndsWith('Z'),
            "The real Postgrest serializer preserves the chosen hour without a timezone suffix");
        var returned = JsonConvert.DeserializeObject<TripSupabase>(json, settings)!;
        var local = Mappers.MapToTripDTO(returned);
        Check(local.StartDate == repository.Updated.StartDate && local.EndDate == repository.Updated.EndDate,
            "Supabase serialization and mapping round-trip both selected timestamps unchanged");
    }

    private static TripDTO Trip(DateTime start, DateTime end) => new()
    {
        Destination = "Trip", LocalUserId = "local", RemoteUserId = "remote",
        StartDate = start, EndDate = end
    };
    private static TripSetupViewModel Form(Trips repository, Classification classification)
        => new(null!, null!, new Session(), null!, repository, null!, classification);
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }
    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private sealed class Trips : ITripRepository
    {
        public event Action<TripDTO, string>? TripChanged { add { } remove { } }
        public TripDTO? Added { get; private set; }
        public TripDTO? Updated { get; private set; }
        public Task<int> AddTripAsync(TripDTO trip) { Added = trip; return Task.FromResult(1); }
        public Task UpdateTripAsync(TripDTO trip) { Updated = trip; return Task.CompletedTask; }
        public Task DeleteTripAsync(TripDTO trip) => Task.CompletedTask;
    }
    private sealed class Classification : ITripClassificationService
    {
        public DateTime Start { get; private set; }
        public DateTime End { get; private set; }
        public Task<TripClassificationData> ClassifyTripAsync(string destination, DateTime start, DateTime end, List<string> transports)
        {
            Start = start;
            End = end;
            return Task.FromResult(new TripClassificationData { CoverTheme = "big_city" });
        }
    }
}
