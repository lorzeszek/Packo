using CommunityToolkit.Mvvm.Messaging;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Enums;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using SQLite;
using System.Text.Json;

namespace Packo.Repositories
{
    public class SyncPackingItemRepository : IPackingItemRepository
    {
        private readonly IPackingItemRepository _local;
        private readonly IPackingItemRepository _remote;
        private readonly ITripRepository _tripRepository;
        private readonly SQLiteAsyncConnection _pendingDb;
        private readonly ISessionService _sessionService;
        private readonly SemaphoreSlim _syncGate = new(1, 1);
        private readonly SemaphoreSlim _pendingSyncGate = new(1, 1);
        // Bounded fallback for an INSERT echo arriving after the user already deleted its local row.
        private readonly Dictionary<int, (int ItemId, int TripId, string UserId)> _recentUploads = new();

        public event Action<PackingItemChange>? PackingItemChanged;

        public SyncPackingItemRepository(IPackingItemRepository local, IPackingItemRepository remote, ISessionService sessionService, SQLiteAsyncConnection pendingDb, ITripRepository tripRepository)
        {
            _local = local;
            _remote = remote;
            _sessionService = sessionService;

            _pendingDb = pendingDb;
            _tripRepository = tripRepository;
            _remote.PackingItemChanged += OnRemotePackingItemChanged;
            Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        }

        private async void OnRemotePackingItemChanged(PackingItemChange change)
        {
            try
            {
                // The echo can arrive before Insert returns its generated remote ID.
                // Wait for that item's local identity to be saved before notifying the UI.
                await _syncGate.WaitAsync();
                try
                {
                    var local = await _pendingDb.Table<SQLitePackingItem>().FirstOrDefaultAsync(x =>
                        x.LocalUserId == _sessionService.LocalUserId && x.RemotePackingItemId == change.Item.RemotePackingItemId);
                    if (local != null)
                    {
                        change.Item.LocalPackingItemId = local.LocalPackingItemId;
                        change.Item.LocalTripId = local.LocalTripId;
                        change.Item.LocalUserId = local.LocalUserId;
                        if (change.Type == PackingItemChangeType.Insert)
                            change = new PackingItemChange(PackingItemChangeType.IdentityAssigned, change.Item);
                    }
                    else if (change.Type == PackingItemChangeType.Insert && change.Item.RemotePackingItemId is int remoteId
                        && _recentUploads.TryGetValue(remoteId, out var uploaded) && uploaded.UserId == _sessionService.LocalUserId)
                    {
                        change.Item.LocalPackingItemId = uploaded.ItemId;
                        change.Item.LocalTripId = uploaded.TripId;
                        change.Item.LocalUserId = uploaded.UserId;
                        change = new PackingItemChange(PackingItemChangeType.IdentityAssigned, change.Item);
                    }
                    if (change.Type == PackingItemChangeType.IdentityAssigned && change.Item.RemotePackingItemId is int echoId)
                        _recentUploads.Remove(echoId);
                }
                finally { _syncGate.Release(); }
                PackingItemChanged?.Invoke(change);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        private void SchedulePendingSync()
        {
            _ = Task.Run(async () =>
            {
                try { await SyncPendingChangesAsync(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            });
        }

        private void RememberUploadedIdentity(PackingItemDTO item)
        {
            if (item.RemotePackingItemId is not int remoteId)
                return;
            _recentUploads[remoteId] = (item.LocalPackingItemId, item.LocalTripId, item.LocalUserId);
            if (_recentUploads.Count > 256)
                _recentUploads.Remove(_recentUploads.Keys.First());
        }

        private async void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        {
            if (e.NetworkAccess != NetworkAccess.Internet || !_sessionService.IsAuthenticated)
                return;
            try
            {
                await _tripRepository.SyncPendingChangesAsync();
                await SyncPendingChangesAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        public async Task<int> AddPackingItemAsync(PackingItemDTO item)
        {
            int id;
            await _syncGate.WaitAsync();
            try { id = await AddPackingItemCoreAsync(item); }
            finally { _syncGate.Release(); }
            await PublishPackingProgressAsync(item.LocalTripId, item.RemoteTripId, item.LocalUserId);
            return id;
        }

        private async Task<int> AddPackingItemCoreAsync(PackingItemDTO item)
        {
            int packinItemId = await _local.AddPackingItemAsync(item);

            item.LocalPackingItemId = packinItemId;

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                try
                {
                    item.RemotePackingItemId = await _remote.AddPackingItemAsync(item);
                    RememberUploadedIdentity(item);
                    await _local.UpdatePackingItemAsync(item);
                }
                catch
                {
                    var pending = new SQLitePendingPackingItemChange
                    {
                        LocalUserId = item.LocalUserId,
                        LocalTripId = item.LocalTripId,
                        LocalPackingItemId = item.LocalPackingItemId,
                        Operation = "Add",
                        PackingItemJson = JsonSerializer.Serialize(item)
                    };
                    await _pendingDb.InsertAsync(pending);
                }
            }

            else
            {
                var pending = new SQLitePendingPackingItemChange
                {
                    LocalUserId = item.LocalUserId,
                    LocalTripId = item.LocalTripId,
                    LocalPackingItemId = item.LocalPackingItemId,
                    Operation = "Add",
                    PackingItemJson = JsonSerializer.Serialize(item)
                };
                await _pendingDb.InsertAsync(pending);
            }

            return packinItemId;
        }

        public async Task AddPackingItemsAsync(List<PackingItemDTO> items)
        {
            if (items.Count == 0)
                return;

            await _syncGate.WaitAsync();
            try
            {
                var rows = items.Select(item => new SQLitePackingItem
                {
                    LocalUserId = item.LocalUserId,
                    RemoteUserId = item.RemoteUserId,
                    LocalTripId = item.LocalTripId,
                    RemoteTripId = item.RemoteTripId,
                    Name = item.Name,
                    Category = item.Category,
                    CreatedDate = item.CreatedDate,
                    ModifiedDate = item.ModifiedDate,
                    IsPacked = item.IsPacked,
                    Quantity = Math.Max(1, item.Quantity)
                }).ToList();
                // The rows and their upload queue commit together, even if the app closes next.
                await _pendingDb.RunInTransactionAsync(db =>
                {
                    for (var i = 0; i < rows.Count; i++)
                    {
                        db.Insert(rows[i]);
                        var item = items[i];
                        var queuedItem = new PackingItemDTO
                        {
                            LocalPackingItemId = rows[i].LocalPackingItemId,
                            LocalUserId = item.LocalUserId,
                            RemoteUserId = item.RemoteUserId,
                            LocalTripId = item.LocalTripId,
                            RemoteTripId = item.RemoteTripId,
                            Name = item.Name,
                            Category = item.Category,
                            CreatedDate = item.CreatedDate,
                            ModifiedDate = item.ModifiedDate,
                            IsPacked = item.IsPacked,
                            Quantity = Math.Max(1, item.Quantity)
                        };
                        db.Insert(new SQLitePendingPackingItemChange
                        {
                            LocalUserId = item.LocalUserId,
                            LocalTripId = item.LocalTripId,
                            RemoteTripId = item.RemoteTripId ?? 0,
                            LocalPackingItemId = rows[i].LocalPackingItemId,
                            Operation = "Add",
                            PackingItemJson = JsonSerializer.Serialize(queuedItem)
                        });
                    }
                });
                for (var i = 0; i < items.Count; i++)
                    items[i].LocalPackingItemId = rows[i].LocalPackingItemId;
            }
            finally { _syncGate.Release(); }

            foreach (var trip in items.Select(x => (x.LocalTripId, x.RemoteTripId, x.LocalUserId)).Distinct())
                await PublishPackingProgressAsync(trip.LocalTripId, trip.RemoteTripId, trip.LocalUserId);
            SchedulePendingSync();
        }

        public async Task DeletePackingItemAsync(PackingItemDTO item)
        {
            await _syncGate.WaitAsync();
            try { await DeletePackingItemCoreAsync(item); }
            finally { _syncGate.Release(); }
            await PublishPackingProgressAsync(item.LocalTripId, item.RemoteTripId, item.LocalUserId);
        }

        private async Task DeletePackingItemCoreAsync(PackingItemDTO item)
        {
            await ResolveRemoteIdentityAsync(item);
            // Only cancel queued changes for the explicitly selected item.
            // A pending Add must not recreate an item removed while offline.
            SQLitePendingPackingItemChange? deletion = null;
            await _pendingDb.RunInTransactionAsync(db => deletion = QueueLocalDeletion(db, item));
            if (deletion != null && Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                try
                {
                    await _remote.DeletePackingItemAsync(item);
                    await _pendingDb.DeleteAsync(deletion);
                }
                catch
                {
                    // Keep the deletion for retry after reconnecting or restarting.
                }
            }
        }

        public async Task DeletePackingItemsAsync(List<PackingItemDTO> items)
        {
            if (items.Count == 0)
                return;
            await _syncGate.WaitAsync();
            try
            {
                // Clear the selected list and persist every tombstone atomically, before network work.
                await _pendingDb.RunInTransactionAsync(db =>
                {
                    foreach (var item in items)
                        QueueLocalDeletion(db, item);
                });
                foreach (var trip in items.Select(x => (x.LocalTripId, x.RemoteTripId, x.LocalUserId)).Distinct())
                    await PublishPackingProgressAsync(trip.LocalTripId, trip.RemoteTripId, trip.LocalUserId);
            }
            finally { _syncGate.Release(); }
            SchedulePendingSync();
        }

        private static SQLitePendingPackingItemChange? QueueLocalDeletion(SQLiteConnection db, PackingItemDTO item)
        {
            var saved = item.LocalPackingItemId > 0
                ? db.Table<SQLitePackingItem>().FirstOrDefault(x => x.LocalPackingItemId == item.LocalPackingItemId
                    && x.LocalUserId == item.LocalUserId && x.LocalTripId == item.LocalTripId)
                : null;
            if (saved?.RemotePackingItemId > 0)
            {
                item.RemotePackingItemId = saved.RemotePackingItemId;
                item.RemoteTripId = saved.RemoteTripId;
            }
            var pending = db.Table<SQLitePendingPackingItemChange>()
                .Where(x => x.LocalUserId == item.LocalUserId && x.LocalTripId == item.LocalTripId).ToList();
            foreach (var change in pending)
            {
                var queued = JsonSerializer.Deserialize<PackingItemDTO>(change.PackingItemJson);
                if (queued != null && ((item.RemotePackingItemId > 0 && queued.RemotePackingItemId == item.RemotePackingItemId)
                    || (item.LocalPackingItemId > 0 && queued.LocalPackingItemId == item.LocalPackingItemId)))
                    db.Delete(change);
            }
            db.Table<SQLitePackingItem>().Where(x => x.LocalUserId == item.LocalUserId
                && x.LocalTripId == item.LocalTripId
                && ((item.LocalPackingItemId > 0 && x.LocalPackingItemId == item.LocalPackingItemId)
                    || (item.RemotePackingItemId > 0 && x.RemotePackingItemId == item.RemotePackingItemId))).Delete();
            if (item.RemotePackingItemId > 0)
            {
                var deletion = new SQLitePendingPackingItemChange
                {
                    LocalUserId = item.LocalUserId,
                    LocalTripId = item.LocalTripId,
                    RemoteTripId = item.RemoteTripId ?? 0,
                    LocalPackingItemId = item.LocalPackingItemId,
                    Operation = "Delete",
                    PackingItemJson = JsonSerializer.Serialize(item)
                };
                db.Insert(deletion);
                return deletion;
            }
            return null;
        }

        public async Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int localTripId, int remoteTripId)
        {
            // A newly created list is already durable locally; opening it must not wait for uploads.
            var hasPendingAdds = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x =>
                x.LocalUserId == _sessionService.LocalUserId && x.LocalTripId == localTripId && x.Operation == "Add").CountAsync() > 0;
            if (hasPendingAdds)
            {
                SchedulePendingSync();
                return await GetLocalItemsForCurrentUserAsync(localTripId, remoteTripId);
            }
            if (remoteTripId > 0 && Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                await SyncPendingChangesAsync();
                await _syncGate.WaitAsync();
                try
                {
                    var packingItems = await _remote.GetPackingItemsForTripAsync(localTripId, remoteTripId);
                    var pending = await _pendingDb.Table<SQLitePendingPackingItemChange>()
                        .Where(x => x.LocalUserId == _sessionService.LocalUserId && x.LocalTripId == localTripId).ToListAsync();
                    var deletedIds = pending.Where(x => x.Operation == "Delete")
                        .Select(x => JsonSerializer.Deserialize<PackingItemDTO>(x.PackingItemJson)?.RemotePackingItemId).ToHashSet();
                    var cached = await GetLocalItemsForCurrentUserAsync(localTripId, remoteTripId);
                    var result = new List<PackingItemDTO>();
                    foreach (var item in packingItems.Where(x => !deletedIds.Contains(x.RemotePackingItemId)))
                    {
                        var localItem = cached.FirstOrDefault(x => x.RemotePackingItemId == item.RemotePackingItemId);
                        if (localItem == null)
                        {
                            // Older app versions did not save the returned remote ID.
                            // Relink only an unambiguous exact match, without removing cache rows.
                            var legacyMatches = cached.Where(x => x.RemotePackingItemId is not > 0
                                && x.Name == item.Name && x.Category == item.Category
                                && (x.CreatedDate == item.CreatedDate
                                    || x.CreatedDate.ToUniversalTime() == item.CreatedDate.ToUniversalTime())).ToList();
                            if (legacyMatches.Count == 1 && !pending.Any(x => x.Operation == "Add" &&
                                JsonSerializer.Deserialize<PackingItemDTO>(x.PackingItemJson)?.LocalPackingItemId == legacyMatches[0].LocalPackingItemId))
                                localItem = legacyMatches[0];
                        }
                        item.LocalUserId = _sessionService.LocalUserId ?? string.Empty;
                        item.LocalTripId = localTripId;
                        item.LocalPackingItemId = localItem?.LocalPackingItemId ?? 0;
                        if (localItem == null)
                            item.LocalPackingItemId = await _local.AddPackingItemAsync(item);
                        else if (!pending.Any(x => x.Operation == "Update" &&
                            JsonSerializer.Deserialize<PackingItemDTO>(x.PackingItemJson)?.RemotePackingItemId == item.RemotePackingItemId))
                            await _local.UpdatePackingItemAsync(item);
                        else
                        {
                            result.Add(localItem);
                            continue;
                        }
                        result.Add(item);
                    }
                    var pendingAddIds = pending.Where(x => x.Operation == "Add" || x.Operation == "Update")
                        .Select(x => JsonSerializer.Deserialize<PackingItemDTO>(x.PackingItemJson)?.LocalPackingItemId).ToHashSet();
                    result.AddRange(cached.Where(x => pendingAddIds.Contains(x.LocalPackingItemId)
                        && !result.Any(r => r.LocalPackingItemId == x.LocalPackingItemId)));
                    // A successful full fetch defines current membership. Keep older cache rows
                    // recoverable, but stop counting entries no longer present in this trip.
                    var currentIds = result.Select(x => x.LocalPackingItemId).ToHashSet();
                    var membershipChanged = false;
                    await _pendingDb.RunInTransactionAsync(db =>
                    {
                        var rows = db.Table<SQLitePackingItem>().Where(x => x.LocalUserId == _sessionService.LocalUserId
                            && x.LocalTripId == localTripId && (x.RemoteTripId == null || x.RemoteTripId == 0 || x.RemoteTripId == remoteTripId)).ToList();
                        foreach (var row in rows)
                        {
                            var stale = !currentIds.Contains(row.LocalPackingItemId);
                            if (row.IsStaleCache == stale)
                                continue;
                            row.IsStaleCache = stale;
                            db.Update(row);
                            membershipChanged = true;
                        }
                    });
                    if (membershipChanged)
                        await PublishPackingProgressAsync(localTripId, remoteTripId, _sessionService.LocalUserId ?? string.Empty);
                    return result;
                }
                finally { _syncGate.Release(); }
            }

            var localTrip = await GetLocalItemsForCurrentUserAsync(localTripId, remoteTripId);

            return localTrip;
        }

        private async Task<IReadOnlyList<PackingItemDTO>> GetLocalItemsForCurrentUserAsync(int localTripId, int remoteTripId)
        {
            var items = await _local.GetPackingItemsForTripAsync(localTripId, remoteTripId);
            return items.Where(x => x.LocalUserId == _sessionService.LocalUserId
                && (x.RemoteTripId is not > 0 || x.RemoteTripId == remoteTripId)).ToList();
        }

        public async Task UnsubscribeFromPackingItemChangesAsync()
        {
            if (_remote is null) return;
            await _remote.UnsubscribeFromPackingItemChangesAsync();
        }

        public async Task UpdatePackingItemAsync(PackingItemDTO item)
        {
            await _syncGate.WaitAsync();
            try { await UpdatePackingItemCoreAsync(item); }
            finally { _syncGate.Release(); }
            await PublishPackingProgressAsync(item.LocalTripId, item.RemoteTripId, item.LocalUserId);
        }

        private async Task PublishPackingProgressAsync(int localTripId, int? remoteTripId, string localUserId)
        {
            var progress = await PackingStatsSnapshot.ReadAsync(_pendingDb, localUserId);
            WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage
            {
                LocalTripId = localTripId,
                RemoteTripId = remoteTripId,
                LocalUserId = localUserId,
                PackingSummary = progress.SummaryFor(localTripId, remoteTripId)
            });
        }

        private async Task UpdatePackingItemCoreAsync(PackingItemDTO item)
        {
            await ResolveRemoteIdentityAsync(item);
            await _local.UpdatePackingItemAsync(item);

            if (item.RemotePackingItemId is not > 0)
            {
                // An edit made before upload replaces the queued Add, keeping its local identity.
                await _pendingDb.RunInTransactionAsync(db =>
                {
                    db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == item.LocalUserId
                        && x.LocalTripId == item.LocalTripId && x.LocalPackingItemId == item.LocalPackingItemId).Delete();
                    db.Insert(new SQLitePendingPackingItemChange
                    {
                        LocalUserId = item.LocalUserId,
                        LocalTripId = item.LocalTripId,
                        LocalPackingItemId = item.LocalPackingItemId,
                        RemoteTripId = item.RemoteTripId ?? 0,
                        Operation = "Add",
                        PackingItemJson = JsonSerializer.Serialize(item)
                    });
                });
                SchedulePendingSync();
                return;
            }

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                try
                {
                    await _remote.UpdatePackingItemAsync(item);
                }
                catch
                {
                    if (item.RemotePackingItemId != null)
                    {
                        var pending = new SQLitePendingPackingItemChange
                        {
                            LocalUserId = item.LocalUserId,
                            LocalTripId = item.LocalTripId,
                            Operation = "Update",
                            PackingItemJson = JsonSerializer.Serialize(item)
                        };
                        await _pendingDb.InsertAsync(pending);
                    }
                    else
                    {
                        var existingPendingChange = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId && x.LocalTripId == item.LocalTripId && x.LocalPackingItemId == item.LocalPackingItemId).DeleteAsync();

                        var pending = new SQLitePendingPackingItemChange
                        {
                            LocalUserId = item.LocalUserId,
                            LocalTripId = item.LocalTripId,
                            Operation = "Add",
                            PackingItemJson = JsonSerializer.Serialize(item)
                        };
                        await _pendingDb.InsertAsync(pending);
                    }
                }
            }

            else
            {
                if (item.RemotePackingItemId != null)
                {
                    var pending = new SQLitePendingPackingItemChange
                    {
                        LocalUserId = item.LocalUserId,
                        LocalTripId = item.LocalTripId,
                        Operation = "Update",
                        PackingItemJson = JsonSerializer.Serialize(item)
                    };
                    await _pendingDb.InsertAsync(pending);
                }
                else
                {
                    var existingPendingChange = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId && x.LocalTripId == item.LocalTripId && x.LocalPackingItemId == item.LocalPackingItemId).DeleteAsync();

                    var pending = new SQLitePendingPackingItemChange
                    {
                        LocalUserId = item.LocalUserId,
                        LocalTripId = item.LocalTripId,
                        Operation = "Add",
                        PackingItemJson = JsonSerializer.Serialize(item)
                    };
                    await _pendingDb.InsertAsync(pending);
                }

                var pendingChanges = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId && x.LocalTripId == item.LocalTripId).ToListAsync();

            }
        }

        public async Task UpdatePendingPackingItems(int localTripId, int remoteTripId)
        {
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !_sessionService.IsAuthenticated)
                return;

            var pendingChanges = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId && x.LocalTripId == localTripId).ToListAsync();

            foreach (var pendingChange in pendingChanges)
            {
                pendingChange.RemoteTripId = remoteTripId;
            }

            await _pendingDb.UpdateAllAsync(pendingChanges);
        }

        public async Task SyncPendingChangesAsync()
        {
            await _pendingSyncGate.WaitAsync();
            try { await SyncPendingChangesCoreAsync(); }
            finally { _pendingSyncGate.Release(); }
        }

        private async Task SyncPendingChangesCoreAsync()
        {
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !_sessionService.IsAuthenticated)
                return;

            var pendingChanges = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId).ToListAsync();
            var syncingLocalUser = _sessionService.LocalUserId;
            var syncingRemoteUser = _sessionService.UserId;

            foreach (var pendingChange in pendingChanges)
            {
                await _syncGate.WaitAsync();
                try
                {
                    if (!_sessionService.IsAuthenticated || _sessionService.LocalUserId != syncingLocalUser
                        || _sessionService.UserId != syncingRemoteUser)
                        break;

                    // A user may have edited/deleted the item since the queue snapshot was read.
                    var current = await _pendingDb.FindAsync<SQLitePendingPackingItemChange>(pendingChange.Id);
                    if (current == null)
                        continue;
                    var packingItem = JsonSerializer.Deserialize<PackingItemDTO>(current.PackingItemJson);
                    if (packingItem is null)
                    {
                        await _pendingDb.DeleteAsync(current);
                        continue;
                    }

                    if (current.Operation != "Delete" && packingItem.RemoteTripId is not > 0)
                    {
                        var trip = await _tripRepository.GetLocalTripAsync(current.LocalTripId, syncingLocalUser!);
                        if (trip is null)
                        {
                            // The parent was removed, e.g. during logout.
                            await _pendingDb.DeleteAsync(current);
                            continue;
                        }
                        if (trip.RemoteTripId <= 0)
                            continue; // Wait until the parent trip has a remote ID.
                        packingItem.RemoteTripId = trip.RemoteTripId;
                    }
                    packingItem.RemoteUserId = syncingRemoteUser ?? string.Empty;

                    try
                    {
                        switch (current.Operation)
                        {
                            case "Add":
                                await ResolveRemoteIdentityAsync(packingItem);
                                if (packingItem.RemotePackingItemId > 0)
                                    await _remote.UpdatePackingItemAsync(packingItem);
                                else
                                {
                                    packingItem.RemotePackingItemId = await _remote.AddPackingItemAsync(packingItem);
                                    RememberUploadedIdentity(packingItem);
                                }
                                await _local.UpdatePackingItemAsync(packingItem);
                                PackingItemChanged?.Invoke(new PackingItemChange(PackingItemChangeType.IdentityAssigned, packingItem));
                                break;
                            case "Update":
                                await _remote.UpdatePackingItemAsync(packingItem);
                                break;
                            case "Delete":
                                await _remote.DeletePackingItemAsync(packingItem);
                                break;
                        }
                        await _pendingDb.DeleteAsync(current);
                    }
                    catch
                    {
                        // Keep the durable change for the next retry.
                    }
                }
                finally { _syncGate.Release(); }
            }

            WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());

        }

        private async Task ResolveRemoteIdentityAsync(PackingItemDTO item)
        {
            if (item.LocalPackingItemId <= 0 || item.RemotePackingItemId > 0)
                return;
            var saved = await _pendingDb.Table<SQLitePackingItem>().FirstOrDefaultAsync(x =>
                x.LocalPackingItemId == item.LocalPackingItemId && x.LocalTripId == item.LocalTripId
                && x.LocalUserId == item.LocalUserId);
            if (saved?.RemotePackingItemId > 0)
            {
                item.RemotePackingItemId = saved.RemotePackingItemId;
                item.RemoteTripId = saved.RemoteTripId;
                item.RemoteUserId = _sessionService.UserId ?? saved.RemoteUserId;
            }
        }

        public Task StartRealtimeAsync()
        {
            return _remote.StartRealtimeAsync();
        }

        public Task<bool> IsChannelCreatedAsync()
        {
            return _remote.IsChannelCreatedAsync();
        }
    }
}
