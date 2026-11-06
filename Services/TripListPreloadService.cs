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
        private readonly object _gate = new();
        private Task<IReadOnlyList<TripWithStats>>? _cachedRequest;
        private long _dataVersion;
        private long _cachedVersion;

        public TripListPreloadService(ITripRepository tripRepository)
        {
            _tripRepository = tripRepository;
            WeakReferenceMessenger.Default.Register<TripListPreloadService, TripDataChangedMessage>(this, (recipient, _) =>
                recipient.Invalidate());
            WeakReferenceMessenger.Default.Register<TripListPreloadService, PackingProgressChangedMessage>(this, (recipient, message) =>
                recipient.UpdatePackingProgress(message));
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
                lock (_gate)
                {
                    if (forceRefresh || _cachedRequest is null || _cachedRequest.IsFaulted || _cachedRequest.IsCanceled)
                    {
                        _cachedVersion = _dataVersion;
                        _cachedRequest = _tripRepository.GetActiveTripsWithStatsAsync();
                    }
                    request = _cachedRequest;
                    requestVersion = _cachedVersion;
                }

                try
                {
                    var result = await request;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cachedRequest, request) && requestVersion == _dataVersion)
                            return result;
                    }

                    // A trip change or a packing update superseded this result.
                    forceRefresh = false;
                }
                catch
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_cachedRequest, request) && requestVersion == _dataVersion)
                            _cachedRequest = null;
                    }
                    throw;
                }
            }
        }

        public void Invalidate()
        {
            lock (_gate)
            {
                _dataVersion++;
                _cachedRequest = null;
            }
        }

        private void UpdatePackingProgress(PackingProgressChangedMessage message)
        {
            lock (_gate)
            {
                if (_cachedRequest is { IsFaulted: false, IsCanceled: false } request)
                    _cachedRequest = WithPackingProgressAsync(request, message);
            }
        }

        private static async Task<IReadOnlyList<TripWithStats>> WithPackingProgressAsync(
            Task<IReadOnlyList<TripWithStats>> request, PackingProgressChangedMessage message)
        {
            var trips = await request.ConfigureAwait(false);
            return trips.Select(entry => message.MatchesTrip(entry.Trip) && entry.PackingSummary != message.PackingSummary
                ? new TripWithStats(entry.Trip, message.PackingSummary)
                : entry).ToList();
        }
    }
}
