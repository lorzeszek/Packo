using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories;
using Packo.Repositories.Local;
using Packo.Repositories.Models;
using Packo.Repositories.Supabase;
using Packo.Services;
using Packo.Services.Interfaces;
using SQLite;

internal static class TripCreationChecks
{
    public static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"packo-trip-creation-{Guid.NewGuid():N}.db");
        var db = new SQLiteAsyncConnection(path);
        try
        {
            await db.CreateTableAsync<SQLiteTrip>();
            await db.CreateTableAsync<SQLitePackingItem>();
            await db.CreateTableAsync<SQLitePackingList>();
            await db.CreateTableAsync<SQLitePendingTripChange>();
            await db.CreateTableAsync<SQLitePendingPackingItemChange>();
            var session = new OnlineSession();
            var service = new Service();
            var local = new LocalTripRepository(db, session);
            var remote = new SupabaseTripRepository(service);
            var repository = new SyncTripRepository(local, remote, session, db);
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;

            service.Client.FailTripInserts = true;
            var warsaw = Trip("Warszawa");
            var gdynia = Trip("Gdynia");
            await repository.AddTripAsync(warsaw);
            await repository.AddTripAsync(gdynia);
            Check(warsaw.LocalTripId > 0 && gdynia.LocalTripId != warsaw.LocalTripId
                && await db.Table<SQLitePendingTripChange>().CountAsync() == 2,
                "Two failed uploads preserve both trips locally and queue both for retry");
            Check((await repository.GetTripAsync(warsaw))?.Destination == "Warszawa",
                "Refreshing a pending trip while online reads its durable local record");

            var preload = new TripListPreloadService(repository);
            var pending = await preload.GetTripsAsync();
            Check(pending.Select(entry => entry.Trip.Destination).Order().SequenceEqual(new[] { "Gdynia", "Warszawa" }),
                "Returning to the online Trips list keeps both new trips visible after failed uploads");

            // Exercise retry after restarting the repository, with the deployed sharing policies.
            service.Client.FailTripInserts = false;
            service.Client.RejectInsertReturning = true;
            var restarted = new SyncTripRepository(local, remote, session, db);
            await restarted.SyncPendingChangesAsync();
            var synced = await preload.GetTripsAsync();
            Check(await db.Table<SQLitePendingTripChange>().CountAsync() == 0
                && synced.Count == 2 && synced.All(entry => entry.Trip.RemoteTripId > 0),
                "Retry uploads both trips without INSERT RETURNING and invalidates the cached list");
            Check(synced.Single(entry => entry.Trip.Destination == "Warszawa").Trip.LocalTripId == warsaw.LocalTripId,
                "Synchronizing preserves the identity used by the already-open trip details");

            var saved = (await local.GetTripAsync(warsaw))!;
            saved.RemoteTripId = 0; // A retry payload can predate the successful server commit.
            var repeatedId = await remote.AddTripAsync(saved);
            Check(repeatedId > 0 && service.Client.TripInsertCount == 2,
                "Retrying a committed upload finds the existing trip without creating a duplicate");

            service.Client.HideTripReads = true;
            var rejected = false;
            try { await remote.AddTripAsync(Trip("Missing identity")); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "An unreadable server identity is treated as a failed upload instead of returning ID zero");
            service.Client.HideTripReads = false;
            service.Client.TripRows.RemoveAll(row => row.Destination == "Missing identity");

            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            var offline = Trip("Offline draft");
            await repository.AddTripAsync(offline);
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            Check((await repository.GetActiveTripsWithStatsAsync()).Count == 3,
                "The online list combines cloud trips with a draft created offline without duplicates");

            session.LocalUserId = "another-account";
            session.UserId = "another-owner";
            service.Client.TripRows.Clear();
            Check((await repository.GetActiveTripsWithStatsAsync()).Count == 0,
                "Pending local trips never leak into another account's online list");
        }
        finally
        {
            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            await db.CloseAsync();
            File.Delete(path);
        }
    }

    private static TripDTO Trip(string destination) => new()
    {
        Destination = destination, LocalUserId = "trip-owner-local", RemoteUserId = "trip-owner",
        StartDate = DateTime.Today.AddDays(1), EndDate = DateTime.Today.AddDays(3),
        TimeZoneId = "Europe/Warsaw", IsActive = true
    };

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class Service : ISupabaseService { public AuthClient Client { get; } = new(); }
    private sealed class OnlineSession : ISessionService
    {
        public string? LocalUserId { get; set; } = "trip-owner-local";
        public string? UserId { get; set; } = "trip-owner";
        public bool IsAuthenticated => true;
        public bool GlobalIsBusy { get; set; }
    }
}
