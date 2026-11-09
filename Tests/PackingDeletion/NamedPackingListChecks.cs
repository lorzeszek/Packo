using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Local;
using Packo.Repositories.Models;
using SQLite;

static class NamedPackingListChecks
{
    public static async Task RunAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"packo-lists-{Guid.NewGuid():N}.db");
        var db = new SQLiteAsyncConnection(dbPath);
        try
        {
            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            // Simulate a database from before the PackingListId column existed.
            await db.ExecuteAsync("""
                CREATE TABLE SQLitePackingItem (
                    LocalPackingItemId INTEGER PRIMARY KEY AUTOINCREMENT,
                    LocalUserId TEXT, LocalTripId INTEGER, RemoteTripId INTEGER, IsPacked INTEGER
                )
                """);
            await db.ExecuteAsync("INSERT INTO SQLitePackingItem (LocalUserId, LocalTripId, RemoteTripId, IsPacked) VALUES ('local', 1, 10, 1)");
            await LocalPackingItemRepository.EnsureSchemaAsync(db);
            await LocalPackingItemRepository.EnsureSchemaAsync(db);
            await db.CreateTableAsync<SQLitePendingPackingItemChange>();
            var local = new LocalPackingItemRepository(db);
            var remote = new NamedRemote();
            var trip = new TripDTO { LocalTripId = 1, RemoteTripId = 10, LocalUserId = "local" };
            var repository = new SyncPackingItemRepository(local, remote, new Session(), db, new Trips());
            var legacy = (await repository.GetPackingListsAsync(trip)).Single();
            Check(legacy.Id == Guid.Empty && legacy.Packed == 1 && legacy.Total == 1,
                "SQLite migration preserves the legacy list and its progress across repeated initialization");

            await repository.DeletePackingListAsync(trip, legacy.Id);
            var discarded = await repository.CreatePackingListAsync(trip, "Discarded");
            await repository.AddPackingItemAsync(new PackingItemDTO
            {
                PackingListId = discarded.Id, Name = "Discarded item", LocalUserId = "local", LocalTripId = 1, RemoteTripId = 10
            });
            await repository.DeletePackingListAsync(trip, discarded.Id);
            Check((await db.Table<SQLitePendingPackingItemChange>().CountAsync()) == 0,
                "Deleting an offline list also cancels its queued item uploads");

            var lists = new List<PackingListDTO>();
            for (var index = 0; index < 4; index++)
                lists.Add(await repository.CreatePackingListAsync(trip, $"List {index + 1}"));
            await repository.RenamePackingListAsync(trip, lists[0].Id, "  Kids  ");
            await repository.AddPackingItemsAsync(lists.SelectMany(list => Enumerable.Range(0, 10).Select(index => new PackingItemDTO
            {
                PackingListId = list.Id, Name = $"Item {index}", LocalUserId = "local", LocalTripId = 1,
                RemoteTripId = 10, IsPacked = index == 0
            })).ToList());

            Check((await repository.GetPackingListsAsync(trip)).All(list => list is { Packed: 1, Total: 10 })
                && (await PackingStatsSnapshot.ReadAsync(db, "local")).SummaryFor(1, 10) == "4 / 40",
                "Four SQLite lists show separate progress and one summed trip counter");
            Check((await repository.GetPackingListItemsAsync(trip, lists[0].Id)).All(item => item.PackingListId == lists[0].Id),
                "Reading a selected list excludes other lists with identical item names");
            var allListItems = await repository.GetPackingListItemsAsync(
                trip, lists.Select(list => list.Id).ToArray(), refresh: false);
            Check(allListItems.Count == 40 && allListItems.Select(item => item.PackingListId).Distinct().Count() == 4,
                "Copying from all source lists reads every list from SQLite while offline");

            await repository.SyncPendingChangesAsync();
            var restarted = new SyncPackingItemRepository(local, remote, new Session(), db, new Trips());
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            await restarted.SyncPendingChangesAsync();
            Check(remote.Lists[(10, lists[0].Id)].Name == "Kids" && remote.Items.Count == 40,
                "Restarted sync creates parent lists with their latest names before uploading items");

            var listsRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLists = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.ListsRequested = listsRequested;
            remote.ReleaseLists = releaseLists;
            var detailsRefresh = restarted.RefreshPackingTripAsync(trip);
            Task editorRefresh;
            try
            {
                await listsRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                editorRefresh = restarted.RefreshPackingTripAsync(trip);
                var cachedLists = await restarted.GetPackingListsAsync(trip, refresh: false).WaitAsync(TimeSpan.FromSeconds(5));
                var cachedItems = await restarted.GetPackingListItemsAsync(trip, lists[0].Id, refresh: false)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Check(cachedLists.Count == 4 && cachedItems.Count == 10 && !detailsRefresh.IsCompleted,
                    "Both screens can read SQLite while the cloud refresh is blocked");
            }
            finally
            {
                releaseLists.TrySetResult();
            }
            await Task.WhenAll(detailsRefresh, editorRefresh).WaitAsync(TimeSpan.FromSeconds(5));
            await restarted.RefreshPackingTripAsync(trip);
            Check(remote.ListRequestCount == 1,
                "Opening details and the editor together and quickly reopening reuse one cloud refresh");

            var openOfflineEditor = new PackingItemDTO
            {
                PackingListId = lists[0].Id, Name = "New after reconnect", LocalUserId = "local", LocalTripId = 1
            };
            await restarted.AddPackingItemAsync(openOfflineEditor);
            Check(openOfflineEditor.RemoteTripId == 10, "An editor opened offline uses the trip identity assigned on reconnect");
            await restarted.DeletePackingItemAsync(openOfflineEditor);

            var remoteOnly = new PackingListDTO(Guid.NewGuid(), "Empty cloud list");
            remote.Lists.Add((10, remoteOnly.Id), remoteOnly);
            Check((await restarted.GetPackingListsAsync(trip)).Any(list => list.Id == remoteOnly.Id && list.Total == 0),
                "Empty lists downloaded from Supabase are visible and cached");

            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            await restarted.DeletePackingListAsync(trip, lists[0].Id);
            Check((await db.Table<SQLitePackingItem>().ToListAsync()).Count == 30,
                "Offline list deletion removes only the selected list's SQLite items");

            remote.FailListDelete = true;
            await restarted.SyncPendingChangesAsync();
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            var refreshed = await restarted.GetPackingListsAsync(trip);
            Check(refreshed.All(list => list.Id != lists[0].Id) && refreshed.Sum(list => list.Total) == 30,
                "A failed Supabase delete cannot restore the list or its items during refresh");
            remote.FailListDelete = false;
            await restarted.SyncPendingChangesAsync();
            Check(!remote.Lists.ContainsKey((10, lists[0].Id)) && remote.Items.Count == 30,
                "Retry deletes the selected remote list and cascades only its items");

            await remote.DeletePackingListAsync(10, lists[1].Id);
            Check((await restarted.GetPackingListsAsync(trip)).All(list => list.Id != lists[1].Id)
                && (await restarted.GetPackingListsAsync(trip, refresh: false)).Sum(list => list.Total) == 20,
                "Remote deletion removes the list and its SQLite items on refresh");
        }
        finally
        {
            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            await db.CloseAsync();
            File.Delete(dbPath);
        }
    }

    private static void Check(bool passed, string message)
    {
        if (!passed)
            throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class NamedRemote : Remote, IPackingListRemote
    {
        public Dictionary<(int Trip, Guid Id), PackingListDTO> Lists { get; } = [];
        public bool FailListDelete { get; set; }
        public TaskCompletionSource? ListsRequested { get; set; }
        public TaskCompletionSource? ReleaseLists { get; set; }
        public int ListRequestCount { get; private set; }

        public async Task<IReadOnlyList<PackingListDTO>> GetPackingListsAsync(int remoteTripId)
        {
            ListRequestCount++;
            ListsRequested?.TrySetResult();
            if (ReleaseLists is not null)
                await ReleaseLists.Task;
            return Lists.Where(x => x.Key.Trip == remoteTripId).Select(x => x.Value).ToList();
        }

        public Task SavePackingListAsync(int remoteTripId, PackingListDTO list, bool create)
        {
            if (create || Lists.ContainsKey((remoteTripId, list.Id)))
                Lists[(remoteTripId, list.Id)] = list;
            return Task.CompletedTask;
        }

        public Task DeletePackingListAsync(int remoteTripId, Guid listId)
        {
            if (FailListDelete)
                throw new IOException("Simulated remote list deletion failure.");
            Lists.Remove((remoteTripId, listId));
            Items.RemoveAll(x => x.RemoteTripId == remoteTripId && x.PackingListId == listId);
            return Task.CompletedTask;
        }

        public override Task<int> AddPackingItemAsync(PackingItemDTO item)
        {
            if (!Lists.ContainsKey((item.RemoteTripId!.Value, item.PackingListId)))
                throw new InvalidOperationException("The parent list must exist before uploading an item.");
            return base.AddPackingItemAsync(item);
        }
    }
}
