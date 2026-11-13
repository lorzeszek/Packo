using System.Diagnostics;
using Packo.Helpers;
using Packo.Models;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using SQLite;

internal static class DeviceIntegrationChecks
{
    private const string Owner = "00000000-0000-0000-0000-000000000001";
    private const string Member = "00000000-0000-0000-0000-000000000002";

    public static async Task RunAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tests/Reminders/Database/integration-server.mjs"))) directory = directory.Parent;
        var script = Path.Combine(directory?.FullName ?? throw new Exception("Cannot locate database fixture"), "Tests/Reminders/Database/integration-server.mjs");
        using var process = new Process { StartInfo = new ProcessStartInfo("node")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!
        }};
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(40));
            if (ready is null || !ready.StartsWith("READY ")) throw new Exception("PostgreSQL fixture failed: " + await errors);
            var endpoint = ready[6..];
            await using var first = await Device.CreateAsync(endpoint, Owner, "phone-one", 1);
            await using var second = await Device.CreateAsync(endpoint, Member, "phone-two", 42);
            await using var sameAccount = await Device.CreateAsync(endpoint, Owner, "owner-second-phone", 77);
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            var zone = ReminderSchedule.Zone("Europe/Warsaw");
            var upcoming = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(30), zone).DateTime;
            var reminder = first.Service.New(first.Trip);
            reminder.Title = "Shared date regression"; reminder.Body = "Keep the chosen date";
            reminder.Date = upcoming.Date.AddDays(1);
            reminder.MinuteOfDay = upcoming.Hour * 60 + upcoming.Minute;
            reminder.Audience = ReminderAudience.All;
            await first.Service.SaveAsync(first.Trip, reminder);
            await second.Service.RefreshAsync();
            var received = (await second.Service.GetAsync(second.Trip)).Single();
            Check(!received.Pending && received.Definition.Date == reminder.Date && second.Scheduler.Items.Count == 1,
                "A second participant device downloads the shared reminder from PostgreSQL through the real RPC client");
            var edit = (await first.Service.GetAsync(first.Trip)).Single().Definition.Copy();
            edit.Date = upcoming.Date;
            await first.Service.SaveAsync(first.Trip, edit);
            var onFirst = (await first.Service.GetAsync(first.Trip)).Single().Definition;
            Check(onFirst.Date == upcoming.Date && onFirst.Copy().Date == upcoming.Date && onFirst.Repeat == ReminderRepeat.Once,
                "A one-time date moved one day earlier stays identical after SQLite, RPC, PostgreSQL and reopening the definition");
            var next = ReminderSchedule.Next(onFirst, first.Trip, DateTimeOffset.UtcNow, zone);
            Check(next.HasValue && TimeZoneInfo.ConvertTime(next.Value, zone).Date == upcoming.Date
                && TimeZoneInfo.ConvertTime(next.Value, zone).Hour == upcoming.Hour,
                "The list's next occurrence uses the same calendar date and hour as the saved one-time definition");
            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            await second.Service.RefreshAsync();
            Check((await second.Service.GetAsync(second.Trip)).Single().Definition.Date == reminder.Date,
                "An offline second device keeps its last downloaded configuration");
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            await second.Service.RefreshAsync();
            var onSecond = (await second.Service.GetAsync(second.Trip)).Single().Definition;
            Check(onSecond.Id == onFirst.Id && onSecond.Date == onFirst.Date && onSecond.Version == onFirst.Version
                && second.Scheduler.Items.Single().Trip.LocalTripId == 42,
                "Reconnection updates the second device without duplicating the reminder or confusing local trip IDs");
            var personal = first.Service.New(first.Trip);
            personal.Title = "Private reminder"; personal.Body = "Same account only";
            personal.Date = upcoming.Date.AddDays(1);
            await first.Service.SaveAsync(first.Trip, personal);
            await sameAccount.Service.RefreshAsync();
            await second.Service.RefreshAsync();
            Check((await sameAccount.Service.GetAsync(sameAccount.Trip)).Count == 2
                && (await second.Service.GetAsync(second.Trip)).Count == 1,
                "A second phone on the same account receives personal reminders while another participant only receives the group reminder");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }

    private sealed class Device : IAsyncDisposable
    {
        private readonly SQLiteAsyncConnection _db;
        private readonly string _path;
        public TripDTO Trip { get; }
        public TripReminderService Service { get; }
        public Scheduler Scheduler { get; } = new();
        private Device(SQLiteAsyncConnection db, string path, TripDTO trip, TripReminderService service, Scheduler scheduler)
        { _db = db; _path = path; Trip = trip; Service = service; Scheduler = scheduler; }
        public static async Task<Device> CreateAsync(string endpoint, string userId, string localUserId, int localTripId)
        {
            var path = Path.Combine(Path.GetTempPath(), "packo-rpc-device-" + Guid.NewGuid() + ".db3");
            var db = new SQLiteAsyncConnection(path);
            await db.CreateTableAsync<SQLiteTrip>();
            await db.CreateTableAsync<Packo.Repositories.Models.SQLitePendingTripChange>();
            var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ReminderSchedule.Zone("Europe/Warsaw")).Date;
            var row = new SQLiteTrip { LocalTripId = localTripId, RemoteTripId = 1, LocalUserId = localUserId,
                RemoteUserId = Owner, IsJoinedTrip = userId != Owner, StartDate = today.AddDays(1), EndDate = today.AddDays(7), TimeZoneId = "Europe/Warsaw" };
            await db.InsertAsync(row);
            await db.ExecuteAsync("UPDATE SQLiteTrip SET LocalTripId = ? WHERE LocalTripId = ?", localTripId, row.LocalTripId);
            row.LocalTripId = localTripId;
            var trip = Packo.Repositories.Local.LocalTripRepository.ToTrip(row);
            var scheduler = new Scheduler();
            var service = new TripReminderService(db, new Session(userId, localUserId), new Remote(endpoint, userId), new Trips(), scheduler);
            return new Device(db, path, trip, service, scheduler);
        }
        public async ValueTask DisposeAsync() { await _db.CloseAsync(); File.Delete(_path); }
    }
    private sealed class Session(string userId, string localUserId) : ISessionService
    {
        public string? UserId => userId;
        public string? LocalUserId => localUserId;
        public bool IsAuthenticated => true;
        public TestUser? User => null;
    }
    private sealed class Remote : ISupabaseService
    {
        public FakeClient Client { get; } = new();
        public Remote(string endpoint, string userId)
        {
            var rpc = new global::Supabase.Postgrest.Client(endpoint, new global::Supabase.Postgrest.ClientOptions
                { Headers = new Dictionary<string, string> { ["X-Test-User"] = userId } });
            Client.Respond = async (name, args) => new Response((await rpc.Rpc(name, args)).Content ?? "{}");
        }
        public Task EnsureRealtimeConnectedAsync() => Task.CompletedTask;
    }
    private sealed class Trips : ITripRepository { public Task SyncPendingChangesAsync() => Task.CompletedTask; }
    public sealed class Scheduler : IReminderScheduler
    {
        public IReadOnlyList<ScheduledReminder> Items { get; private set; } = [];
        public bool IsSupported => true;
        public bool HasExactTiming => true;
        public bool HasPermission => true;
        public Task<bool> RequestPermissionAsync() => Task.FromResult(true);
        public Task ReplaceAsync(string accountId, IReadOnlyList<ScheduledReminder> reminders) { Items = reminders; return Task.CompletedTask; }
        public void OpenTimingSettings() { }
    }
}
