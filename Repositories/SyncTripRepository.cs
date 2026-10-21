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
                //if (remoteTrip != null)
                //{
                //    await _local.UpdateTripAsync(remoteTrip); // synchronizacja lokalna
                //}
                return remoteTrip;
            }

            var localTrip = await _local.GetTripAsync(trip);

            return localTrip;
        }

        public async Task UpdateTripAsync(TripDTO trip)
        {
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
                    Operation = "Update",
                    TripJson = JsonSerializer.Serialize(trip)
                };
                await _pendingDb.InsertAsync(pending);
            }
        }

        public async Task SyncPendingChangesAsync()
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

            return await _remote.GetActiveTripsWithStatsAsync();
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
