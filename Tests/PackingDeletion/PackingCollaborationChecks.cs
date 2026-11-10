using System.Text.Json;
using Packo.Models.DTO;
using Packo.Repositories;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Local;
using Packo.Repositories.Models;
using SQLite;

internal static class PackingCollaborationChecks
{
    public static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"packo-collaboration-{Guid.NewGuid():N}.db");
        var db = new SQLiteAsyncConnection(path);
        try
        {
            await LocalPackingItemRepository.EnsureSchemaAsync(db);
            await db.CreateTableAsync<SQLitePendingPackingItemChange>();
            var local = new LocalPackingItemRepository(db);
            var remote = new OperationRemote();
            var repository = new SyncPackingItemRepository(local, remote, new Session(), db, new Trips());

            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            var item = new PackingItemDTO { Name = "Shoes", Quantity = 1, LocalUserId = "local",
                LocalTripId = 1, RemoteTripId = 10, RemoteUserId = "user" };
            await repository.AddPackingItemAsync(item);
            item.Quantity = 2;
            item.PreviousQuantity = 1;
            await repository.UpdatePackingItemAsync(item);
            item.Quantity = 3;
            item.PreviousQuantity = 2;
            await repository.UpdatePackingItemAsync(item);
            Check(await db.Table<SQLitePendingPackingItemChange>().CountAsync() == 3
                && (await local.GetPackingItemsForTripAsync(1, 10)).Single().Quantity == 3,
                "Offline edits commit optimistically with a durable queue");

            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            await repository.SyncPendingChangesAsync();
            var requests = remote.Requests.ToList();
            var firstEdit = requests[1];
            var secondEdit = requests[2];
            Check(requests.Count == 3 && firstEdit.GetProperty("p_changes").EnumerateObject().Single().Name == "Quantity"
                && firstEdit.GetProperty("p_before").GetProperty("QuantityVersion").GetInt64() == 0
                && secondEdit.GetProperty("p_before").GetProperty("QuantityVersion").GetInt64() == 1
                && secondEdit.GetProperty("p_item_id").GetInt32() == 1,
                "Replay sends only the edited field, its base version and the assigned cloud ID");

            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            var unsure = new PackingItemDTO { Name = "Coat", LocalUserId = "local", LocalTripId = 1,
                RemoteTripId = 10, RemoteUserId = "user" };
            await repository.AddPackingItemAsync(unsure);
            remote.LoseNextResponse = true;
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            await repository.SyncPendingChangesAsync();
            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            await repository.DeletePackingItemAsync(unsure);
            var restarted = new SyncPackingItemRepository(local, remote, new Session(), db, new Trips());
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            await restarted.SyncPendingChangesAsync();
            Check(remote.ItemIds.SetEquals([1]) && await db.Table<SQLitePendingPackingItemChange>().CountAsync() == 0,
                "A lost add response followed by offline delete retries safely after restart");

            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            item.Quantity = 4;
            item.PreviousQuantity = 3;
            await restarted.UpdatePackingItemAsync(item);
            remote.ConflictNextUpdate = true;
            Connectivity.Current.NetworkAccess = NetworkAccess.Internet;
            await restarted.SyncPendingChangesAsync();
            var trip = new TripDTO { LocalTripId = 1, RemoteTripId = 10, LocalUserId = "local" };
            Check(await repository.GetPackingSyncStatusAsync(trip) == (0, true)
                && await repository.GetPackingSyncStatusAsync(trip) == (0, false),
                "An acknowledged conflict is shown once after returning to the editor and is not retried");
        }
        finally
        {
            Connectivity.Current.NetworkAccess = NetworkAccess.None;
            await db.CloseAsync();
            File.Delete(path);
        }
    }

    private static void Check(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS: {description}");
    }

    private sealed class OperationRemote : Remote, IPackingOperationRemote
    {
        private readonly Dictionary<string, PackingOperationResult> _receipts = new();
        private int _nextId;
        public List<JsonElement> Requests { get; } = [];
        public HashSet<int> ItemIds { get; } = [];
        public bool LoseNextResponse { get; set; }
        public bool ConflictNextUpdate { get; set; }

        public Task<PackingOperationResult> ApplyOperationAsync(object parameters)
        {
            var request = JsonSerializer.SerializeToElement(parameters);
            Requests.Add(request);
            var token = request.GetProperty("p_operation_id").GetString()!;
            if (_receipts.TryGetValue(token, out var receipt))
                return Task.FromResult(receipt);

            var operation = request.GetProperty("p_operation").GetString();
            int? id = null;
            if (operation == "Add")
            {
                id = ++_nextId;
                ItemIds.Add(id.Value);
            }
            if (operation == "Delete")
                ItemIds.Remove(request.GetProperty("p_item_id").GetInt32());
            var status = operation == "Update" && ConflictNextUpdate ? "conflict" : "ok";
            if (status == "conflict")
                ConflictNextUpdate = false;
            receipt = new PackingOperationResult(status, id);
            _receipts.Add(token, receipt);
            if (LoseNextResponse)
            {
                LoseNextResponse = false;
                throw new IOException("The server committed the request before the connection was lost.");
            }
            return Task.FromResult(receipt);
        }
    }
}
