using Packo.Repositories.Interfaces;
using Packo.Repositories.Models;
using Packo.Services.Interfaces;

namespace Packo.Services
{
    public class TripListPreloadService : ITripListPreloadService
    {
        private readonly ITripRepository _tripRepository;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<IReadOnlyList<TripWithStats>>? _cachedRequest;

        public TripListPreloadService(ITripRepository tripRepository)
        {
            _tripRepository = tripRepository;
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
            Task<IReadOnlyList<TripWithStats>> request;

            await _gate.WaitAsync();
            try
            {
                if (forceRefresh || _cachedRequest is null)
                    _cachedRequest = _tripRepository.GetActiveTripsWithStatsAsync();

                request = _cachedRequest;
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                return await request;
            }
            catch
            {
                await _gate.WaitAsync();
                try
                {
                    if (ReferenceEquals(_cachedRequest, request))
                        _cachedRequest = null;
                }
                finally
                {
                    _gate.Release();
                }

                throw;
            }
        }

        public void Invalidate()
        {
            _cachedRequest = null;
        }
    }
}
