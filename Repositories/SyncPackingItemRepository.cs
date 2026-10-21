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
        }

        public async Task<int> AddPackingItemAsync(PackingItemDTO item)
        {
            var id = await AddPackingItemCoreAsync(item);
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
                    await _remote.AddPackingItemAsync(item);
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
            await DeletePackingItemCoreAsync(item);
            WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());
        }

        private async Task DeletePackingItemCoreAsync(PackingItemDTO item)
        {
            await _local.DeletePackingItemAsync(item);

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                try
                {
                    await _remote.DeletePackingItemAsync(item);
                }
                catch
                {
                    var pending = new SQLitePendingPackingItemChange
                    {
                        LocalUserId = item.LocalUserId,
                        LocalTripId = item.LocalTripId,
                        Operation = "Delete",
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
                    Operation = "Delete",
                    PackingItemJson = JsonSerializer.Serialize(item)
                };
                await _pendingDb.InsertAsync(pending);
            }
        }

        public async Task<IReadOnlyList<PackingItemDTO>> GetPackingItemsForTripAsync(int localTripId, int remoteTripId)
        {
            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                var packingItems = await _remote.GetPackingItemsForTripAsync(remoteTripId, remoteTripId);
                if (packingItems != null)
                {
                    //await _local.UpdatePackingItemAsync(packingItems); // synchronizacja lokalna
                }
                return packingItems;
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
            await UpdatePackingItemCoreAsync(item);
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
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !_sessionService.IsAuthenticated)
                return;

            var pendingChanges = await _pendingDb.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId).ToListAsync();

            //var tripIdsForDelete = new List<int>();

            foreach (var pendingChange in pendingChanges)
            {
                var packingItem = JsonSerializer.Deserialize<PackingItemDTO>(pendingChange.PackingItemJson);
                // Resolve RemoteTripId if it wasn't known when the change was queued.
                if (pendingChange.RemoteTripId == 0)
                {
                    var trip = await _tripRepository.GetLocalTripAsync(pendingChange.LocalTripId, _sessionService.LocalUserId!);
                    //var trip = await _tripRepository.GetTripAsync(new TripDTO
                    //{
                    //    LocalUserId = _sessionService.LocalUserId!,
                    //    LocalTripId = pendingChange.LocalTripId
                    //});

                    if (trip?.RemoteTripId > 0)
                    {
                        //pendingChange.RemoteTripId = trip.RemoteTripId;
                        packingItem.RemoteTripId = trip.RemoteTripId;
                        //await _pendingDb.UpdateAsync(pendingChange);
                    }
                }

                //packingItem.RemoteTripId = pendingChange.RemoteTripId;
                packingItem.RemoteUserId = _sessionService.UserId;

                try
                {
                    switch (pendingChange.Operation)
                    {
                        case "Add":
                            await _remote.AddPackingItemAsync(packingItem!);
                            break;
                        case "Update":
                            await _remote.UpdatePackingItemAsync(packingItem!);
                            break;
                        case "Delete":
                            await _remote.DeletePackingItemAsync(packingItem!);
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