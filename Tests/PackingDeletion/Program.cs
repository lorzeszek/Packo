using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Local;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using SQLite;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Services;

SQLitePCL.Batteries_V2.Init();
await PackingFeatureChecks.RunAsync();
var path = Path.Combine(Path.GetTempPath(), $"packo-deletion-{Guid.NewGuid():N}.db");
var db = new SQLiteAsyncConnection(path);
try
{
    await db.CreateTableAsync<SQLitePackingItem>();
    await db.CreateTableAsync<SQLitePendingPackingItemChange>();
    var local = new LocalPackingItemRepository(db);
    var remote = new Remote();
    var repo = new SyncPackingItemRepository(local, remote, new Session(), db, new Trips());
    PackingItemDTO Item(string name, int trip = 1) => new()
    {
        Name = name, LocalUserId = "local", RemoteUserId = "user", LocalTripId = trip,
        RemoteTripId = 10, CreatedDate = DateTime.UtcNow
    };
    void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }

    Connectivity.Current.NetworkAccess = NetworkAccess.None;
    var unsynced = Item("Offline addition");
    await repo.AddPackingItemAsync(unsynced);
    var other = Item("Keep in other trip", 2);
    await repo.AddPackingItemAsync(other);
    await repo.DeletePackingItemAsync(unsynced);
    Check((await local.GetPackingItemsForTripAsync(1, 10)).Count == 0, "Offline deletion removes SQLite row");
    Check((await db.Table<SQLitePendingPackingItemChange>().ToListAsync()).Count == 1,
        "Deleting unsynced addition cancels its Add and preserves other trip's change");

    Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
    var synced = Item("Synced item");
    await repo.AddPackingItemAsync(synced);
    Check(synced.RemotePackingItemId > 0 &&
        (await local.GetPackingItemsForTripAsync(1, 10)).Single().RemotePackingItemId == synced.RemotePackingItemId,
        "Remote identity is persisted in SQLite");
    Connectivity.Current.NetworkAccess = NetworkAccess.None;
    synced.IsPacked = true;
    await repo.UpdatePackingItemAsync(synced);
    await repo.DeletePackingItemAsync(synced);
    var pending = await db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalTripId == 1).ToListAsync();
    Check(pending.Count == 1 && pending[0].Operation == "Delete", "Delete replaces queued Update with durable tombstone");
    Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
    remote.FailDelete = true;
    var visible = await repo.GetPackingItemsForTripAsync(1, 10);
    Check(visible.Count == 0, "Failed remote delete stays hidden when reading online");
    Check((await db.Table<SQLitePendingPackingItemChange>().Where(x => x.Operation == "Delete").CountAsync()) == 1,
        "Failed remote deletion remains queued");
    remote.FailDelete = false;
    // A fresh repository represents restarting the app with the same durable database.
    var restarted = new SyncPackingItemRepository(local, remote, new Session(), db, new Trips());
    await restarted.SyncPendingChangesAsync();
    Check(!remote.Items.Any(x => x.RemotePackingItemId == synced.RemotePackingItemId), "Restarted sync removes remote item");
    Check((await db.Table<SQLitePendingPackingItemChange>().CountAsync()) == 0, "Successful retry clears pending changes");
    Check((await local.GetPackingItemsForTripAsync(2, 10)).Count == 1, "Other trip's SQLite item is preserved");

    var cloud = Item("From cloud");
    cloud.RemotePackingItemId = 99;
    remote.Items.Add(cloud);
    var loaded = (await repo.GetPackingItemsForTripAsync(1, 10)).Single();
    Check(loaded.LocalPackingItemId > 0, "Cloud item is cached with local identity");
    await repo.DeletePackingItemAsync(loaded);
    Check((await local.GetPackingItemsForTripAsync(1, 10)).Count == 0 && remote.Items.All(x => x.RemotePackingItemId != 99),
        "Online delete removes both cloud item and SQLite cache");

    var legacy = Item("Legacy cached item");
    var legacyId = await local.AddPackingItemAsync(legacy);
    var legacyRemote = Item(legacy.Name);
    legacyRemote.CreatedDate = legacy.CreatedDate;
    legacyRemote.RemotePackingItemId = 100;
    remote.Items.Add(legacyRemote);
    var relinked = (await repo.GetPackingItemsForTripAsync(1, 10)).Single();
    Check(relinked.LocalPackingItemId == legacyId, "Unambiguous legacy cache row is relinked without duplicates");
    await repo.DeletePackingItemAsync(relinked);
    Check((await local.GetPackingItemsForTripAsync(1, 10)).Count == 0, "Relinked legacy item deletes from SQLite");

    // Without categories, equal names/timestamps must still never merge ambiguous cache rows.
    var ambiguousFirst = Item("Same legacy item", 9);
    var ambiguousSecond = Item(ambiguousFirst.Name, 9);
    ambiguousSecond.CreatedDate = ambiguousFirst.CreatedDate;
    var firstLegacyId = await local.AddPackingItemAsync(ambiguousFirst);
    var secondLegacyId = await local.AddPackingItemAsync(ambiguousSecond);
    var ambiguousRemote = Item(ambiguousFirst.Name, 9);
    ambiguousRemote.CreatedDate = ambiguousFirst.CreatedDate;
    ambiguousRemote.RemotePackingItemId = 101;
    remote.Items.Add(ambiguousRemote);
    var ambiguousLoaded = (await repo.GetPackingItemsForTripAsync(9, 10)).Single();
    Check(ambiguousLoaded.LocalPackingItemId != firstLegacyId && ambiguousLoaded.LocalPackingItemId != secondLegacyId
        && (await db.FindAsync<SQLitePackingItem>(firstLegacyId)).RemotePackingItemId is not > 0
        && (await db.FindAsync<SQLitePackingItem>(secondLegacyId)).RemotePackingItemId is not > 0,
        "Removing categories preserves ambiguous legacy rows without assigning the wrong remote identity");
    await repo.DeletePackingItemAsync(ambiguousLoaded);

    Connectivity.Current.NetworkAccess = NetworkAccess.None;
    var batch = new List<PackingItemDTO> { Item("Batch A", 3), Item("Batch B", 3), Item("Batch C", 3) };
    batch[0].Quantity = 6;
    await repo.AddPackingItemsAsync(batch);
    Check(batch.All(x => x.LocalPackingItemId > 0) && batch.Select(x => x.LocalPackingItemId).Distinct().Count() == 3,
        "Batch assigns all local IDs before returning");
    Check((await local.GetPackingItemsForTripAsync(3, 10)).Count == 3 &&
        await db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalTripId == 3).CountAsync() == 3,
        "Whole batch and upload queue are persisted offline");
    Check((await local.GetPackingItemsForTripAsync(3, 10)).Single(x => x.Name == "Batch A").Quantity == 6,
        "AI batch quantity is persisted in SQLite");
    batch[0].IsPacked = true;
    batch[0].Quantity = 8;
    await repo.UpdatePackingItemAsync(batch[0]);
    await repo.DeletePackingItemAsync(batch[1]);
    await repo.SyncPendingChangesAsync(); // Drain any scheduled offline work before reconnecting.
    // A queue written by an older app still contains the retired fields.
    var legacyQueuedItemId = batch[0].LocalPackingItemId;
    var legacyQueued = await db.Table<SQLitePendingPackingItemChange>()
        .FirstAsync(x => x.LocalPackingItemId == legacyQueuedItemId);
    legacyQueued.PackingItemJson = legacyQueued.PackingItemJson[..^1]
        + ",\"Category\":3,\"ModifiedDate\":\"2026-10-25T12:00:00Z\"}";
    await db.UpdateAsync(legacyQueued);
    var restartBatch = new SyncPackingItemRepository(local, remote, new Session(), db, new Trips());
    Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
    await restartBatch.SyncPendingChangesAsync();
    Check(remote.Items.Count(x => x.LocalTripId == 3) == 2 && remote.Items.Single(x => x.Name == "Batch A").IsPacked,
        "Batch restart uploads latest edit and does not recreate deleted item");
    Check(remote.Items.Single(x => x.Name == "Batch A").Quantity == 8,
        "Restarted offline queue with retired fields uploads the edited quantity");

    remote.FailAdd = true;
    var failedBatch = new List<PackingItemDTO> { Item("Retry A", 4), Item("Retry B", 4) };
    await repo.AddPackingItemsAsync(failedBatch);
    await repo.SyncPendingChangesAsync();
    Check(await db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalTripId == 4).CountAsync() == 2 &&
        (await repo.GetPackingItemsForTripAsync(4, 10)).Count == 2,
        "Upload failure keeps the complete batch visible and durable");
    remote.FailAdd = false;
    await repo.SyncPendingChangesAsync();
    Check(remote.Items.Count(x => x.LocalTripId == 4) == 2 &&
        await db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalTripId == 4).CountAsync() == 0,
        "Failed batch retries without duplicate uploads");

    var echoCompletion = new TaskCompletionSource<PackingItemDTO>(TaskCreationOptions.RunContinuationsAsynchronously);
    var identityEvents = 0;
    void OnEcho(PackingItemChange change)
    {
        if (change.Type == PackingItemChangeType.IdentityAssigned && change.Item.Name == "Slow upload")
        {
            // The first event is upload completion; the second is the delayed realtime echo.
            if (Interlocked.Increment(ref identityEvents) == 2)
                echoCompletion.TrySetResult(change.Item);
        }
    }
    repo.PackingItemChanged += OnEcho;
    remote.AddStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    remote.ReleaseAdd = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var slowBatch = new List<PackingItemDTO> { Item("Slow upload", 5) };
    await repo.AddPackingItemsAsync(slowBatch).WaitAsync(TimeSpan.FromSeconds(5));
    await remote.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check((await repo.GetPackingItemsForTripAsync(5, 10).WaitAsync(TimeSpan.FromSeconds(5))).Count == 1,
        "Creating and opening list returns while Supabase upload is blocked");
    var staleUiItem = Item("Slow upload", 5);
    staleUiItem.LocalPackingItemId = slowBatch[0].LocalPackingItemId;
    // Queue deletion while upload is in flight, before the UI has received the remote ID.
    var deleteDuringUpload = repo.DeletePackingItemAsync(staleUiItem);
    remote.ReleaseAdd.TrySetResult();
    await deleteDuringUpload.WaitAsync(TimeSpan.FromSeconds(5));
    await repo.SyncPendingChangesAsync();
    var echo = await echoCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(echo.LocalPackingItemId == staleUiItem.LocalPackingItemId,
        "Realtime echo carries the original local ID for UI deduplication");
    Check(remote.Items.All(x => x.Name != "Slow upload") &&
        (await local.GetPackingItemsForTripAsync(5, 10)).Count == 0,
        "Delete during upload resolves generated remote ID and removes both copies");
    repo.PackingItemChanged -= OnEcho;
    remote.ReleaseAdd = null;

    // Force failure on the second insert to verify that both tables roll back together.
    Connectivity.Current.NetworkAccess = NetworkAccess.None;
    await db.ExecuteAsync("CREATE TRIGGER FailBatch BEFORE INSERT ON SQLitePackingItem WHEN NEW.Name = 'Reject batch' BEGIN SELECT RAISE(ABORT, 'test failure'); END");
    var rollbackBatch = new List<PackingItemDTO> { Item("Rollback A", 6), Item("Reject batch", 6) };
    var failed = false;
    try { await repo.AddPackingItemsAsync(rollbackBatch); }
    catch (SQLiteException) { failed = true; }
    Check(failed && (await local.GetPackingItemsForTripAsync(6, 10)).Count == 0 &&
        await db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalTripId == 6).CountAsync() == 0 &&
        rollbackBatch.All(x => x.LocalPackingItemId == 0),
        "Local batch failure rolls back all rows, queued uploads and exposed IDs");

    var fullList = Enumerable.Range(1, 27).Select(i => Item($"Progress {i}", 7)).ToList();
    var progressRecipient = new object();
    TripDataChangedMessage? lastProgress = null;
    WeakReferenceMessenger.Default.Register<TripDataChangedMessage>(progressRecipient, (_, message) =>
    {
        if (message.LocalTripId == 7) lastProgress = message;
    });
    await repo.AddPackingItemsAsync(fullList);
    Check(lastProgress?.PackingSummary == "0 / 27", "Batch publishes the complete counter once local commit finishes");
    var beforeStatsRequest = await PackingStatsSnapshot.ReadAsync(db, "local");
    var progressTrip = new TripDTO { LocalTripId = 7, LocalUserId = "local", RemoteTripId = 10 };
    foreach (var partial in new[] { "0 / 0", "0 / 12", "0 / 27" })
    {
        var snapshot = await PackingStatsSnapshot.ReadAsync(db, "local");
        var displayed = snapshot.ApplyTo([new TripWithStats(progressTrip, partial)], beforeStatsRequest);
        Check(displayed.Single().PackingSummary == "0 / 27", $"Partial cloud count {partial} displays complete local 0 / 27");
    }
    // Simulate the final upload committing while the remote stats request still holds 0 / 12.
    await db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalTripId == 7).DeleteAsync();
    var afterFinalUpload = await PackingStatsSnapshot.ReadAsync(db, "local");
    Check(afterFinalUpload.ApplyTo([new TripWithStats(progressTrip, "0 / 12")], beforeStatsRequest).Single().PackingSummary == "0 / 27",
        "Finishing upload during a remote request cannot regress the counter");
    Check(afterFinalUpload.ApplyTo([new TripWithStats(progressTrip, "5 / 30")], afterFinalUpload).Single().PackingSummary == "5 / 30",
        "When local uploads finish, unchanged cached data does not hide changes from another device");
    var unrelatedTrip = new TripDTO { LocalTripId = 7, LocalUserId = "another-device", RemoteTripId = 999 };
    Check(afterFinalUpload.ApplyTo([new TripWithStats(unrelatedTrip, "2 / 8")], beforeStatsRequest).Single().PackingSummary == "2 / 8",
        "A matching local numeric ID cannot override an unrelated cloud trip");

    fullList[0].IsPacked = true;
    await repo.UpdatePackingItemAsync(fullList[0]);
    Check(lastProgress?.PackingSummary == "1 / 27", "Packing an item publishes the local numerator immediately");
    await repo.DeletePackingItemAsync(fullList[1]);
    Check(lastProgress?.PackingSummary == "1 / 26", "Deleting an item publishes the local denominator immediately");
    WeakReferenceMessenger.Default.UnregisterAll(progressRecipient);

    var oldResponse = new TaskCompletionSource<IReadOnlyList<TripWithStats>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var statsRepository = new Trips();
    statsRepository.StatsSource = () => oldResponse.Task;
    var preload = new TripListPreloadService(statsRepository);
    var oldLoad = preload.GetTripsAsync();
    statsRepository.StatsSource = () => Task.FromResult<IReadOnlyList<TripWithStats>>([new(progressTrip, "0 / 27")]);
    WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
    oldResponse.SetResult([new(progressTrip, "0 / 0")]);
    Check((await oldLoad.WaitAsync(TimeSpan.FromSeconds(5))).Single().PackingSummary == "0 / 27",
        "An invalidated in-flight preload cannot restore stale 0 / 0 on Home or Trips");
    await VerifyStaleCacheAndAtomicClearAsync();
    await VerifyStartupTripPreloadAsync();
}
finally
{
    await db.CloseAsync();
    File.Delete(path);
}

static async Task VerifyStartupTripPreloadAsync()
{
    var response = new TaskCompletionSource<IReadOnlyList<TripWithStats>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var requests = 0;
    var repository = new Trips
    {
        StatsSource = () => { requests++; return response.Task; }
    };
    var preload = new TripListPreloadService(repository);
    var startup = preload.GetTripsAsync();
    var home = preload.GetTripsAsync();
    var trips = preload.GetTripsAsync();
    if (startup.IsCompleted || home.IsCompleted || trips.IsCompleted || requests != 1)
        throw new Exception("Startup must await trip data and share its request with Home and Trips.");

    response.SetResult([new(new TripDTO { RemoteTripId = 42 }, "3 / 10")]);
    await Task.WhenAll(startup, home, trips).WaitAsync(TimeSpan.FromSeconds(5));
    var firstScreen = preload.GetTripsAsync();
    if (!firstScreen.IsCompletedSuccessfully || requests != 1 || (await firstScreen).Single().PackingSummary != "3 / 10")
        throw new Exception("The first screen must have cached trip data immediately without another network request.");
    Console.WriteLine("PASS: Startup waits for trips; Home and Trips immediately reuse the same result and single request");

    preload.Invalidate();
    repository.StatsSource = () => Task.FromException<IReadOnlyList<TripWithStats>>(new IOException("Network failure"));
    try
    {
        await preload.GetTripsAsync();
        throw new Exception("Startup must receive loading errors so it can show retry.");
    }
    catch (IOException) { }

    repository.StatsSource = () => Task.FromResult<IReadOnlyList<TripWithStats>>([]);
    if ((await preload.GetTripsAsync()).Count != 0)
        throw new Exception("Retry must recover after a failed startup request.");
    Console.WriteLine("PASS: Startup receives loading errors and retry recovers; an empty trip list completes normally");
}

static async Task VerifyStaleCacheAndAtomicClearAsync()
{
    var regressionPath = Path.Combine(Path.GetTempPath(), $"packo-counter-{Guid.NewGuid():N}.db");
    var regressionDb = new SQLiteAsyncConnection(regressionPath);
    var recipient = new object();
    var summaries = new List<string>();
    var cloud = new Remote();
    void Check(bool passed, string message)
    {
        if (!passed) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }
    PackingItemDTO Item(string name) => new()
    {
        Name = name, LocalTripId = 8, RemoteTripId = 80, LocalUserId = "local", RemoteUserId = "user", CreatedDate = DateTime.UtcNow
    };
    try
    {
        // Upgrade a pre-fix database, including an existing item, through the same startup migration.
        await regressionDb.ExecuteAsync("CREATE TABLE SQLitePackingItem (LocalPackingItemId INTEGER PRIMARY KEY AUTOINCREMENT, RemotePackingItemId INTEGER, LocalTripId INTEGER, RemoteTripId INTEGER, Name TEXT, Category INTEGER, LocalUserId TEXT, RemoteUserId TEXT, CreatedDate BIGINT, ModifiedDate BIGINT, IsPacked INTEGER)");
        await regressionDb.ExecuteAsync("INSERT INTO SQLitePackingItem (Name, LocalUserId, LocalTripId, RemoteTripId, RemotePackingItemId, IsPacked) VALUES ('Obsolete item 0', 'local', 8, 80, 1000, 0)");
        await LocalPackingItemRepository.EnsureSchemaAsync(regressionDb);
        Check((await regressionDb.Table<SQLitePackingItem>().ToListAsync()).Single().Quantity == 1,
            "Upgrading a legacy database initializes quantity to one");
        await regressionDb.CreateTableAsync<SQLitePendingPackingItemChange>();
        var local = new LocalPackingItemRepository(regressionDb);
        var repository = new SyncPackingItemRepository(local, cloud, new Session(), regressionDb, new Trips());
        Check((await local.GetPackingItemsForTripAsync(8, 80)).Count == 1,
            "Upgrading an existing SQLite database preserves its items and initializes the new cache flag");
        // Old rows are present only in SQLite, as after deletion in a previous app version.
        for (var i = 1; i < 26; i++)
        {
            var old = Item($"Obsolete item {i}");
            old.RemotePackingItemId = 1000 + i;
            await local.AddPackingItemAsync(old);
        }
        Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
        await repository.GetPackingItemsForTripAsync(8, 80);
        Check(await regressionDb.Table<SQLitePackingItem>().CountAsync() == 26 &&
            (await local.GetPackingItemsForTripAsync(8, 80)).Count == 0,
            "Obsolete cache entries are preserved for recovery but excluded from the current list");
        await LocalPackingItemRepository.EnsureSchemaAsync(regressionDb);
        Check((await local.GetPackingItemsForTripAsync(8, 80)).Count == 0,
            "Restarting schema initialization does not reactivate obsolete cache entries");

        WeakReferenceMessenger.Default.Register<TripDataChangedMessage>(recipient, (_, message) =>
        {
            if (message.LocalTripId == 8 && message.PackingSummary != null)
                summaries.Add(message.PackingSummary);
        });
        Connectivity.Current.NetworkAccess = NetworkAccess.None;
        var newList = Enumerable.Range(1, 22).Select(i => Item($"AI item {i}")).ToList();
        await repository.AddPackingItemsAsync(newList);
        var snapshot = await PackingStatsSnapshot.ReadAsync(regressionDb, "local");
        var trip = new TripDTO { LocalTripId = 8, RemoteTripId = 80, LocalUserId = "local" };
        Check(await regressionDb.Table<SQLitePackingItem>().CountAsync() == 48 && snapshot.SummaryFor(8) == "0 / 22",
            "26 obsolete cache rows plus 22 AI items display 0 / 22, never 0 / 48");
        Check(snapshot.ApplyTo([new(trip, "0 / 7")], snapshot).Single().PackingSummary == "0 / 22",
            "Home/Trips use 22 current items throughout partial upload");
        Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
        await repository.SyncPendingChangesAsync();
        var displayed = await repository.GetPackingItemsForTripAsync(8, 80);
        Check(displayed.Count == 22 && (await PackingStatsSnapshot.ReadAsync(regressionDb, "local")).SummaryFor(8) == "0 / 22",
            "A completed upload and cache reload keep the same 22-item count");

        // A trip originating on another device can share its numeric local ID.
        for (var i = 0; i < 26; i++)
        {
            var otherTripItem = Item($"Other trip {i}");
            otherTripItem.RemoteTripId = 99;
            otherTripItem.RemotePackingItemId = 2000 + i;
            await local.AddPackingItemAsync(otherTripItem);
        }
        var scoped = await PackingStatsSnapshot.ReadAsync(regressionDb, "local");
        Check(scoped.SummaryFor(8, 80) == "0 / 22" && scoped.SummaryFor(8, 99) == "0 / 26",
            "Two cloud trips sharing a numeric local ID keep separate 22/26-item counters");
        Check((await repository.GetPackingItemsForTripAsync(8, 80)).Count == 22 &&
            (await local.GetPackingItemsForTripAsync(8, 99)).Count == 26,
            "Refreshing one trip preserves the other trip's cached items");

        cloud.DeleteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cloud.ReleaseDelete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await repository.DeletePackingItemsAsync(displayed.ToList()).WaitAsync(TimeSpan.FromSeconds(5));
        await cloud.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleared = await PackingStatsSnapshot.ReadAsync(regressionDb, "local");
        Check(cleared.SummaryFor(8, 80) == "0 / 0" && (await local.GetPackingItemsForTripAsync(8, 80)).Count == 0,
            "Clear list commits all 22 local removals before blocked cloud deletion finishes");
        Check(summaries.SequenceEqual(new[] { "0 / 22", "0 / 0" }),
            "Progress jumps directly 0 / 22 to 0 / 0 without intermediate decrements");
        Check(cleared.ApplyTo([new(trip, "0 / 22")], cleared).Single().PackingSummary == "0 / 0",
            "Pending cloud deletions cannot restore an old counter on Home/Trips");
        var otherCloudTrip = new TripDTO { LocalTripId = 8, RemoteTripId = 99, LocalUserId = "another-device" };
        Check(cleared.ApplyTo([new(otherCloudTrip, "3 / 30")], cleared).Single().PackingSummary == "3 / 30",
            "Pending deletion for one trip cannot override the counter of another trip sharing its local ID");
        Check(await regressionDb.Table<SQLitePendingPackingItemChange>().Where(x => x.Operation == "Delete").CountAsync() == 22,
            "Clear list durably queues every remote deletion in its single transaction");
        cloud.ReleaseDelete.SetResult();
        await repository.SyncPendingChangesAsync();
        Check(cloud.Items.Count == 0 && (await PackingStatsSnapshot.ReadAsync(regressionDb, "local")).SummaryFor(8, 80) == "0 / 0",
            "Cloud eventually reaches zero while the displayed count remains zero");

        Connectivity.Current.NetworkAccess = NetworkAccess.None;
        var rollback = new List<PackingItemDTO> { Item("Keep first"), Item("Reject delete") };
        await repository.AddPackingItemsAsync(rollback);
        await regressionDb.ExecuteAsync("CREATE TRIGGER FailClear BEFORE DELETE ON SQLitePackingItem WHEN OLD.Name = 'Reject delete' BEGIN SELECT RAISE(ABORT, 'test failure'); END");
        var didFail = false;
        try { await repository.DeletePackingItemsAsync(rollback); }
        catch (SQLiteException) { didFail = true; }
        Check(didFail && (await local.GetPackingItemsForTripAsync(8, 80)).Count == 2 &&
            await regressionDb.Table<SQLitePendingPackingItemChange>().CountAsync() == 2,
            "Failed atomic clear restores both items and their pending additions");
    }
    finally
    {
        cloud.ReleaseDelete?.TrySetResult();
        WeakReferenceMessenger.Default.UnregisterAll(recipient);
        await regressionDb.CloseAsync();
        File.Delete(regressionPath);
    }
}

sealed class Session : ISessionService
{
    public string LocalUserId => "local";
    public string UserId => "user";
    public bool IsAuthenticated => true;
}
sealed class Trips : ITripRepository
{
    public Func<Task<IReadOnlyList<TripWithStats>>> StatsSource { get; set; } = () => Task.FromResult<IReadOnlyList<TripWithStats>>([]);
    public Task<IReadOnlyList<TripWithStats>> GetActiveTripsWithStatsAsync() => StatsSource();
    public Task<LocalTrip?> GetLocalTripAsync(int id, string user) => Task.FromResult<LocalTrip?>(new(10));
    public Task SyncPendingChangesAsync() => Task.CompletedTask;
}
sealed class Remote : IPackingItemRepository
{
    private int _nextId;
    public List<PackingItemDTO> Items { get; } = [];
    public bool FailDelete { get; set; }
    public bool FailAdd { get; set; }
    public TaskCompletionSource? AddStarted { get; set; }
    public TaskCompletionSource? ReleaseAdd { get; set; }
    public TaskCompletionSource? DeleteStarted { get; set; }
    public TaskCompletionSource? ReleaseDelete { get; set; }
    public event Action<PackingItemChange>? PackingItemChanged;
    public async Task<int> AddPackingItemAsync(PackingItemDTO item)
    {
        if (FailAdd) throw new IOException("Simulated upload failure");
        AddStarted?.TrySetResult();
        if (ReleaseAdd != null)
            await ReleaseAdd.Task;
        item.RemotePackingItemId = ++_nextId;
        Items.Add(item);
        // Supabase realtime does not provide the SQLite identity.
        PackingItemChanged?.Invoke(new(PackingItemChangeType.Insert, new PackingItemDTO
        {
            RemotePackingItemId = item.RemotePackingItemId, RemoteTripId = item.RemoteTripId,
            Name = item.Name, CreatedDate = item.CreatedDate
        }));
        return _nextId;
    }
    public async Task DeletePackingItemAsync(PackingItemDTO item)
    {
        if (FailDelete) throw new IOException("Simulated network failure");
        DeleteStarted?.TrySetResult();
        if (ReleaseDelete != null)
            await ReleaseDelete.Task;
        Items.RemoveAll(x => x.RemotePackingItemId == item.RemotePackingItemId);
    }
    public Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int local, int remote) =>
        Task.FromResult<IReadOnlyList<PackingItemDTO>>(Items.Where(x => x.LocalTripId == local).ToList());
    public Task AddPackingItemsAsync(List<PackingItemDTO> items) => throw new NotSupportedException();
    public async Task DeletePackingItemsAsync(List<PackingItemDTO> items)
    {
        foreach (var item in items)
            await DeletePackingItemAsync(item);
    }
    public Task UpdatePackingItemAsync(PackingItemDTO item) => Task.CompletedTask;
    public Task UnsubscribeFromPackingItemChangesAsync() => Task.CompletedTask;
    public Task SyncPendingChangesAsync() => Task.CompletedTask;
    public Task StartRealtimeAsync() => Task.CompletedTask;
    public Task<bool> IsChannelCreatedAsync() => Task.FromResult(false);
    public Task UpdatePendingPackingItems(int local, int remote) => Task.CompletedTask;
}
