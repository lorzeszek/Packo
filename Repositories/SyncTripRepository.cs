using CommunityToolkit.Mvvm.Messaging;
using Packo.Models.DTO;
using Packo.Models.SQLite;
using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using SQLite;
using System.Text.Json;

namespace Packo.Repositories
{
    public class SyncTripRepository : ITripRepository
    {
        private readonly ITripRepository _local;
        private readonly ITripRepository _remote;
        private readonly SQLiteAsyncConnection _pendingDb;
        private readonly ISessionService _sessionService;
        private readonly SemaphoreSlim _pendingSyncGate = new(1, 1);

        public event Action<TripDTO, string>? TripChanged
        {
            add => _remote.TripChanged += value;
            remove => _remote.TripChanged -= value;
        }

        public SyncTripRepository(ITripRepository local, ITripRepository remote, ISessionService sessionService, SQLiteAsyncConnection pendingDb)
        {
            _local = local;
            _remote = remote;
            _sessionService = sessionService;
            _pendingDb = pendingDb;
        }

        // Wyłączenie subskrypcji Realtime
        public async Task UnsubscribeFromTripChangesAsync()
        {
            if (_remote is null) return;
            await _remote.UnsubscribeFromTripChangesAsync();
        }

        public async Task<int> AddTripAsync(TripDTO trip)
        {
            var id = await AddTripCoreAsync(trip);
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());
            return id;
        }

        private async Task<int> AddTripCoreAsync(TripDTO trip)
        {
            int localTripId = await _local.AddTripAsync(trip);

            trip.LocalTripId = localTripId;

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                try
                {
                    var remoteTripId = await _remote.AddTripAsync(trip);
                    trip.RemoteTripId = remoteTripId;
                    await _local.UpdateTripAsync(trip);
                }
                catch
                {
                    var pending = new SQLitePendingTripChange
                    {
                        LocalUserId = trip.LocalUserId,
                        Operation = "Add",
                        TripJson = JsonSerializer.Serialize(trip)
                    };
                    await _pendingDb.InsertAsync(pending);
                }
            }
            else
            {
                var pending = new SQLitePendingTripChange
                {
                    LocalUserId = trip.LocalUserId,
                    Operation = "Add",
                    TripJson = JsonSerializer.Serialize(trip)
                };
                await _pendingDb.InsertAsync(pending);
            }

            return localTripId;
        }

        public async Task DeleteTripAsync(TripDTO trip)
        {
            if (trip.IsJoinedTrip)
                throw new InvalidOperationException("Only the trip owner can delete a trip.");
            await DeleteTripCoreAsync(trip);
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());
        }

        private async Task DeleteTripCoreAsync(TripDTO trip)
        {
            await _local.DeleteTripAsync(trip);

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet)
            {
                try
                {
                    await _remote.DeleteTripAsync(trip);
                }
                catch
                {
                    var pending = new SQLitePendingTripChange
                    {
                        LocalUserId = trip.LocalUserId,
                        Operation = "Delete",
                        TripJson = JsonSerializer.Serialize(trip)
                    };
                    await _pendingDb.InsertAsync(pending);
                }
            }
            else
            {
                var pending = new SQLitePendingTripChange
                {
                    LocalUserId = trip.LocalUserId,
                    Operation = "Delete",
                    TripJson = JsonSerializer.Serialize(trip)
                };
                await _pendingDb.InsertAsync(pending);
            }
        }

        public async Task<TripDTO?> GetTripAsync(TripDTO trip)
        {
            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                var remoteTrip = await _remote.GetTripAsync(trip);
                if (remoteTrip is not null)
                {
                    remoteTrip.LocalTripId = trip.LocalTripId;
                    remoteTrip.LocalUserId = trip.LocalUserId;
                    remoteTrip.IsJoinedTrip = trip.IsJoinedTrip;
                    remoteTrip.Participants = trip.Participants;
                }
                return remoteTrip;
            }

            var localTrip = await _local.GetTripAsync(trip);

            return localTrip;
        }

        public async Task UpdateTripAsync(TripDTO trip)
        {
            if (trip.IsJoinedTrip)
                throw new InvalidOperationException("Only the trip owner can edit a trip.");
            await UpdateTripCoreAsync(trip);
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Packo.Messages.TripDataChangedMessage());
        }

        private async Task UpdateTripCoreAsync(TripDTO trip)
        {
            await _local.UpdateTripAsync(trip);

            if (Connectivity.Current.NetworkAccess == NetworkAccess.Internet && _sessionService.IsAuthenticated)
            {
                try
                {
                    await _remote.UpdateTripAsync(trip);
                }
                catch
                {
                    var pending = new SQLitePendingTripChange
                    {
                        LocalUserId = trip.LocalUserId,
                        Operation = "Update",
                        TripJson = JsonSerializer.Serialize(trip)
                    };
                    await _pendingDb.InsertAsync(pending);
                }
            }
            else
            {
                var pending = new SQLitePendingTripChange
                {
                    LocalUserId = trip.LocalUserId,
                    Operation = "Update",
                    TripJson = JsonSerializer.Serialize(trip)
                };
                await _pendingDb.InsertAsync(pending);
            }
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

            var pendingChanges = await _pendingDb.Table<SQLitePendingTripChange>().Where(x => x.LocalUserId == _sessionService.LocalUserId).ToListAsync();

            foreach (var change in pendingChanges)
            {
                var tripDeserialized = JsonSerializer.Deserialize<TripDTO>(change.TripJson);

                if (tripDeserialized == null)
                    return;

                tripDeserialized.RemoteUserId = _sessionService.UserId;
                tripDeserialized.LocalUserId = change.LocalUserId;

                try
                {
                    switch (change.Operation)
                    {
                        case "Add":
                            var remoteTripId = await _remote.AddTripAsync(tripDeserialized!);
                            tripDeserialized.RemoteTripId = remoteTripId;
                            await _local.UpdateTripAsync(tripDeserialized);
                            break;
                        case "Update":
                            await _remote.UpdateTripAsync(tripDeserialized!);
                            break;
                        case "Delete":
                            await _remote.DeleteTripAsync(tripDeserialized!);
                            break;
                    }

                    // Po sukcesie – usuń z kolejki
                    await _pendingDb.DeleteAsync(change);
                }
                catch
                {
                    // Jeśli nie uda się wysłać – zostaje w kolejce
                }
            }
        }



        public async Task<IReadOnlyList<TripWithStats>> GetActiveTripsWithStatsAsync()
        {
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !_sessionService.IsAuthenticated)
            {
                var localTrips = await _local.GetActiveTripsWithStatsAsync();
                return localTrips.ToList();
            }

            var localUserId = _sessionService.LocalUserId ?? string.Empty;
            var account = _sessionService.UserId;
            var beforeRequest = await PackingStatsSnapshot.ReadAsync(_pendingDb, localUserId);
            IReadOnlyList<TripWithStats> remoteTrips;
            try
            {
                remoteTrips = await _remote.GetActiveTripsWithStatsAsync();
            }
            catch when (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                return await _local.GetActiveTripsWithStatsAsync();
            }
            if (_sessionService.LocalUserId != localUserId || _sessionService.UserId != account)
                return await _local.GetActiveTripsWithStatsAsync();

            var removed = new List<int>();
            await _pendingDb.RunInTransactionAsync(db =>
            {
                var cached = db.Table<SQLiteTrip>().Where(x => x.LocalUserId == localUserId).ToList();
                foreach (var entry in remoteTrips)
                {
                    var trip = entry.Trip;
                    var row = cached.FirstOrDefault(x => x.RemoteTripId == trip.RemoteTripId)
                        ?? cached.FirstOrDefault(x => trip.LocalUserId == localUserId && x.LocalTripId == trip.LocalTripId);
                    trip.LocalTripId = row?.LocalTripId ?? 0;
                    trip.LocalUserId = localUserId;
                    trip.IsJoinedTrip = trip.RemoteUserId != account;
                    var updated = Local.LocalTripRepository.ToRow(trip);
                    if (row is null)
                        db.Insert(updated);
                    else
                        db.Update(updated);
                    trip.LocalTripId = updated.LocalTripId;
                }

                var visible = remoteTrips.ToDictionary(x => x.Trip.RemoteTripId, x => x.Trip.LocalTripId);
                foreach (var pair in visible)
                {
                    db.Execute("UPDATE SQLitePackingItem SET LocalTripId = ? WHERE LocalUserId = ? AND RemoteTripId = ? AND LocalTripId <> ?",
                        pair.Value, localUserId, pair.Key, pair.Value);
                    db.Execute("UPDATE SQLitePackingList SET LocalTripId = ? WHERE LocalUserId = ? AND RemoteTripId = ? AND LocalTripId <> ?",
                        pair.Value, localUserId, pair.Key, pair.Value);
                }
                foreach (var change in db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == localUserId))
                {
                    var item = JsonSerializer.Deserialize<PackingItemDTO>(change.PackingItemJson);
                    var remoteId = item?.RemoteTripId ?? change.RemoteTripId;
                    if (item is null || !visible.TryGetValue(remoteId, out var localId) || localId == change.LocalTripId)
                        continue;
                    item.LocalTripId = change.LocalTripId = localId;
                    change.PackingItemJson = JsonSerializer.Serialize(item);
                    db.Update(change);
                }
                foreach (var row in cached.Where(x => x.IsJoinedTrip && x.RemoteTripId.HasValue && !visible.ContainsKey(x.RemoteTripId.Value)))
                {
                    db.Table<SQLitePackingItem>().Where(x => x.LocalUserId == localUserId && x.LocalTripId == row.LocalTripId).Delete();
                    db.Table<SQLitePackingList>().Where(x => x.LocalUserId == localUserId && x.LocalTripId == row.LocalTripId).Delete();
                    db.Table<SQLitePendingPackingItemChange>().Where(x => x.LocalUserId == localUserId && x.LocalTripId == row.LocalTripId).Delete();
                    db.Delete(row);
                    removed.Add(row.RemoteTripId!.Value);
                }
            });
            foreach (var id in removed)
                WeakReferenceMessenger.Default.Send(new Packo.Messages.PackingSyncMessage(id, AccessRevoked: true));
            var latest = await PackingStatsSnapshot.ReadAsync(_pendingDb, localUserId);
            return latest.ApplyTo(remoteTrips, beforeRequest);
        }

        public async Task<IReadOnlyList<string>> GetActiveDestinationsAsync()
        {
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet || !_sessionService.IsAuthenticated)
                return await _local.GetActiveDestinationsAsync();

            return await _remote.GetActiveDestinationsAsync();
        }

        public Task StartRealtimeAsync()
        {
            // delegujemy – BEZ sprawdzania internetu
            return _remote.StartRealtimeAsync();
        }

        public Task<bool> IsChannelCreatedAsync()
        {
            return _remote.IsChannelCreatedAsync();
        }

        public Task<TripDTO?> GetLocalTripAsync(int localTripId, string localUserId)
        {
            return _local.GetLocalTripAsync(localTripId, localUserId);
        }

        public Task DeleteLocalTrips()
        {
            return _local.DeleteLocalTrips();
        }

        public Task SaveDocAsync(TripDocumentDTO document)
        {
            return _local.SaveDocAsync(document);
        }

        public async Task<List<SQLiteTripDocument>> GetByTripDocsAsync()
        {
            return await _local.GetByTripDocsAsync();
        }

        public async Task DeleteDocAsync(Guid id)
        {
            await _local.DeleteDocAsync(id);
        }
    }
}
