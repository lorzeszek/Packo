using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Local;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using SQLite;

SQLitePCL.Batteries_V2.Init();
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
}
finally
{
    await db.CloseAsync();
    File.Delete(path);
}

sealed class Session : ISessionService
{
    public string LocalUserId => "local";
    public string UserId => "user";
    public bool IsAuthenticated => true;
}
sealed class Trips : ITripRepository
{
    public Task<LocalTrip?> GetLocalTripAsync(int id, string user) => Task.FromResult<LocalTrip?>(new(10));
    public Task SyncPendingChangesAsync() => Task.CompletedTask;
}
sealed class Remote : IPackingItemRepository
{
    private int _nextId;
    public List<PackingItemDTO> Items { get; } = [];
    public bool FailDelete { get; set; }
    public event Action<PackingItemChange>? PackingItemChanged;
    public Task<int> AddPackingItemAsync(PackingItemDTO item)
    {
        item.RemotePackingItemId = ++_nextId;
        Items.Add(item);
        return Task.FromResult(_nextId);
    }
    public Task DeletePackingItemAsync(PackingItemDTO item)
    {
        if (FailDelete) throw new IOException("Simulated network failure");
        Items.RemoveAll(x => x.RemotePackingItemId == item.RemotePackingItemId);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int local, int remote) =>
        Task.FromResult<IReadOnlyList<PackingItemDTO>>(Items.Where(x => x.LocalTripId == local).ToList());
    public Task AddPackingItemsAsync(List<PackingItemDTO> items) => throw new NotSupportedException();
    public Task UpdatePackingItemAsync(PackingItemDTO item) => Task.CompletedTask;
    public Task UnsubscribeFromPackingItemChangesAsync() => Task.CompletedTask;
    public Task SyncPendingChangesAsync() => Task.CompletedTask;
    public Task StartRealtimeAsync() => Task.CompletedTask;
    public Task<bool> IsChannelCreatedAsync() => Task.FromResult(false);
    public Task UpdatePendingPackingItems(int local, int remote) => Task.CompletedTask;
}
