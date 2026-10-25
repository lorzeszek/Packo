using CommunityToolkit.Mvvm.Messaging;
using Packo.Models.DTO;
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

        public event Action<PackingItemChange>? PackingItemChanged
        {
            add => _remote.PackingItemChanged += value;
            remove => _remote.PackingItemChanged -= value;
        }

        public SyncPackingItemRepository(IPackingItemRepository local, IPackingItemRepository remote, ISessionService sessionService, SQLiteAsyncConnection pendingDb, ITripRepository tripRepository)
        {
            _local = local;
            _remote = remote;
            _sessionService = sessionService;

            _pendingDb = pendingDb;
            _tripRepository = tripRepository;
            Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
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
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());
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
            foreach (var item in items)
            {
                await AddPackingItemAsync(item);
            }
        }

        public async Task DeletePackingItemAsync(PackingItemDTO item)
        {
            await _syncGate.WaitAsync();
            try { await DeletePackingItemCoreAsync(item); }
            finally { _syncGate.Release(); }
            WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());
        }

        private async Task DeletePackingItemCoreAsync(PackingItemDTO item)
        {
            // Only cancel queued changes for the explicitly selected item.
            // A pending Add must not recreate an item removed while offline.
            SQLitePendingPackingItemChange? deletion = null;
            await _pendingDb.RunInTransactionAsync(db =>
            {
                var pending = db.Table<SQLitePendingPackingItemChange>()
                    .Where(x => x.LocalUserId == item.LocalUserId && x.LocalTripId == item.LocalTripId).ToList();
                foreach (var change in pending)
                {
                    var queued = JsonSerializer.Deserialize<PackingItemDTO>(change.PackingItemJson);
                    if (queued != null && ((item.RemotePackingItemId > 0 && queued.RemotePackingItemId == item.RemotePackingItemId)
                        || (item.LocalPackingItemId > 0 && queued.LocalPackingItemId == item.LocalPackingItemId)))
                        db.Delete(change);
                }
                db.Table<Packo.Models.SQLite.SQLitePackingItem>().Where(x => x.LocalUserId == item.LocalUserId
                    && x.LocalTripId == item.LocalTripId
                    && ((item.LocalPackingItemId > 0 && x.LocalPackingItemId == item.LocalPackingItemId)
                        || (item.RemotePackingItemId > 0 && x.RemotePackingItemId == item.RemotePackingItemId))).Delete();
                if (item.RemotePackingItemId > 0)
                {
                    deletion = new SQLitePendingPackingItemChange
                    {
                        LocalUserId = item.LocalUserId,
                        LocalTripId = item.LocalTripId,
                        RemoteTripId = item.RemoteTripId ?? 0,
                        LocalPackingItemId = item.LocalPackingItemId,
                        Operation = "Delete",
                        PackingItemJson = JsonSerializer.Serialize(item)
                    };
                    db.Insert(deletion);
                }
            });
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

        public async Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int localTripId, int remoteTripId)
        {
            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
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
                    var cached = await _local.GetPackingItemsForTripAsync(localTripId, remoteTripId);
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
                    var pendingAddIds = pending.Where(x => x.Operation == "Add")
                        .Select(x => JsonSerializer.Deserialize<PackingItemDTO>(x.PackingItemJson)?.LocalPackingItemId).ToHashSet();
                    result.AddRange(cached.Where(x => pendingAddIds.Contains(x.LocalPackingItemId)
                        && !result.Any(r => r.LocalPackingItemId == x.LocalPackingItemId)));
                    return result;
                }
                finally { _syncGate.Release(); }
            }

            var localTrip = await _local.GetPackingItemsForTripAsync(localTripId, remoteTripId);

            return localTrip;
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
            WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());
        }

        private async Task UpdatePackingItemCoreAsync(PackingItemDTO item)
        {
            await _local.UpdatePackingItemAsync(item);

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
            await _syncGate.WaitAsync();
            try { await SyncPendingChangesCoreAsync(); }
            finally { _syncGate.Release(); }
        }

        private async Task SyncPendingChangesCoreAsync()
        {
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !_sessionService.IsAuthenticated)
                return;

            var pendingChanges = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId).ToListAsync();

            //var tripIdsForDelete = new List<int>();

            foreach (var pendingChange in pendingChanges)
            {
                var packingItem = JsonSerializer.Deserialize<PackingItemDTO>(pendingChange.PackingItemJson);
                if (packingItem is null)
                {
                    // A malformed entry can never be synced successfully.
                    await _pendingDb.DeleteAsync(pendingChange);
                    continue;
                }

                // Resolve RemoteTripId if it wasn't known when the change was queued.
                if (pendingChange.Operation != "Delete" && packingItem.RemoteTripId is not > 0)
                {
                    var trip = await _tripRepository.GetLocalTripAsync(pendingChange.LocalTripId, _sessionService.LocalUserId!);
                    //var trip = await _tripRepository.GetTripAsync(new TripDTO
                    //{
                    //    LocalUserId = _sessionService.LocalUserId!,
                    //    LocalTripId = pendingChange.LocalTripId
                    //});

                    if (trip is null)
                    {
                        // The trip was removed during logout. This pending change
                        // belongs to the previous account and must not be synced
                        // to the account that has just signed in.
                        await _pendingDb.DeleteAsync(pendingChange);
                        continue;
                    }

                    if (trip.RemoteTripId > 0)
                    {
                        //pendingChange.RemoteTripId = trip.RemoteTripId;
                        packingItem.RemoteTripId = trip.RemoteTripId;
                        //await _pendingDb.UpdateAsync(pendingChange);
                    }
                }

                //packingItem.RemoteTripId = pendingChange.RemoteTripId;
                packingItem.RemoteUserId = _sessionService.UserId ?? string.Empty;

                try
                {
                    switch (pendingChange.Operation)
                    {
                        case "Add":
                            packingItem.RemotePackingItemId = await _remote.AddPackingItemAsync(packingItem);
                            await _local.UpdatePackingItemAsync(packingItem);
                            break;
                        case "Update":
                            await _remote.UpdatePackingItemAsync(packingItem);
                            break;
                        case "Delete":
                            await _remote.DeletePackingItemAsync(packingItem);
                            break;
                    }

                    // Po sukcesie – usuń z kolejki
                    await _pendingDb.DeleteAsync(pendingChange);

                    //tripIdsForDelete.Add(pendingChange.LocalTripId);
                }
                catch
                {
                    // Jeśli nie uda się wysłać – zostaje w kolejce
                }
            }

            //await _tripRepository.DeleteLocalTrips(tripIdsForDelete);
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
