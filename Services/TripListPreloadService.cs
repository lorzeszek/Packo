using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;

namespace Packo.Services
{
    public class TripListPreloadService : ITripListPreloadService
    {
        private readonly ITripRepository _tripRepository;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<IReadOnlyList<TripWithStats>>? _cachedRequest;
        private long _dataVersion;
        private long _cachedVersion;

        public TripListPreloadService(ITripRepository tripRepository)
        {
            _tripRepository = tripRepository;
            WeakReferenceMessenger.Default.Register<TripDataChangedMessage>(this, (recipient, message) =>
                ((TripListPreloadService)recipient).Invalidate());
        }

        public async Task PrefetchAsync()
        {
            try
            {
                await GetTripsAsync();
            }
            catch
            {
                // Startup prefetch is optional. The Trips tab will retry normally.
            }
        }

        public async Task<IReadOnlyList<TripWithStats>> GetTripsAsync(bool forceRefresh = false)
        {
            while (true)
            {
                Task<IReadOnlyList<TripWithStats>> request;
                long requestVersion;
                await _gate.WaitAsync();
                try
                {
                    var version = Volatile.Read(ref _dataVersion);
                    if (forceRefresh || _cachedRequest is null || _cachedVersion != version)
                    {
                        _cachedVersion = version;
                        _cachedRequest = _tripRepository.GetActiveTripsWithStatsAsync();
                    }
                    request = _cachedRequest;
                    requestVersion = _cachedVersion;
                }
                finally { _gate.Release(); }

                try
                {
                    var result = await request;
                    await _gate.WaitAsync();
                    try
                    {
                        if (ReferenceEquals(_cachedRequest, request) && requestVersion == Volatile.Read(ref _dataVersion))
                            return result;
                    }
                    finally { _gate.Release(); }

                    // A local change invalidated this request while it was loading.
                    // Join/start the newer request instead of returning an obsolete counter.
                    forceRefresh = false;
                }
                catch
                {
                    await _gate.WaitAsync();
                    try
                    {
                        if (ReferenceEquals(_cachedRequest, request))
                            _cachedRequest = null;
                    }
                    finally { _gate.Release(); }
                    throw;
                }
            }
        }

        public void Invalidate()
        {
            Interlocked.Increment(ref _dataVersion);
            _cachedRequest = null;
        }
    }
}
