using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;
using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using System.Globalization;

namespace Packo.Services
{
    public class WeatherPreloadService : IWeatherPreloadService
    {
        private readonly ITripRepository _tripRepository;
        private readonly WeatherService _weatherService;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<WeatherLoadResult>? _cachedRequest;
        private long _dataVersion;
        private long _cachedVersion;

        public WeatherPreloadService(ITripRepository tripRepository, WeatherService weatherService)
        {
            _tripRepository = tripRepository;
            _weatherService = weatherService;
            // Startup can prefetch before interactive login or before a trip is added.
            // Cache invalidation must not depend on the Trips page being instantiated.
            WeakReferenceMessenger.Default.Register<LoginCompletedMessage>(this, (recipient, _) =>
                ((WeatherPreloadService)recipient).Invalidate());
            WeakReferenceMessenger.Default.Register<LogoutCompletedMessage>(this, (recipient, _) =>
                ((WeatherPreloadService)recipient).Invalidate());
            WeakReferenceMessenger.Default.Register<TripDataChangedMessage>(this, (recipient, _) =>
                ((WeatherPreloadService)recipient).Invalidate());
        }

        public async Task PrefetchAsync()
        {
            try
            {
                await GetWeatherAsync();
            }
            catch
            {
                // Forecasts are optional at startup; retry when the tab is opened.
            }
        }

        public async Task<WeatherLoadResult> GetWeatherAsync(bool forceRefresh = false)
        {
            while (true)
            {
                Task<WeatherLoadResult> request;
                long requestVersion;
                await _gate.WaitAsync();
                try
                {
                    var version = Volatile.Read(ref _dataVersion);
                    if (forceRefresh || _cachedRequest is null || _cachedVersion != version
                        || (_cachedRequest.IsCompletedSuccessfully
                            && (_cachedRequest.Result.HasFailures || _cachedRequest.Result.Forecasts.Count == 0)))
                    {
                        _cachedVersion = version;
                        _cachedRequest = LoadWeatherAsync();
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

                    // Never restore a guest/previous account's empty or stale result.
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

        public void Invalidate() => Interlocked.Increment(ref _dataVersion);

        private async Task<WeatherLoadResult> LoadWeatherAsync()
        {
            var destinations = (await _tripRepository.GetActiveDestinationsAsync())
                .Where(destination => !string.IsNullOrWhiteSpace(destination))
                .Select(destination => destination.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var forecasts = await Task.WhenAll(destinations.Select(GetWeatherForDestinationAsync));
            return new WeatherLoadResult(
                forecasts.OfType<DestinationWeatherDTO>().ToList(),
                destinations.Where((_, index) => forecasts[index] is null).ToList());
        }

        private async Task<DestinationWeatherDTO?> GetWeatherForDestinationAsync(string destination)
        {
            try
            {
                return await LoadDestinationAsync(destination);
            }
            catch (Exception ex)
            {
                // One unavailable destination must not hide the other forecasts.
                System.Diagnostics.Debug.WriteLine($"Weather unavailable for {destination}: {ex}");
                return null;
            }
        }

        private async Task<DestinationWeatherDTO?> LoadDestinationAsync(string destination)
        {
            var location = await _weatherService.GetLocationAsync(destination);
            if (location is null)
                return null;

            var forecast = await _weatherService.GetForecastAsync(location.Latitude, location.Longitude);
            var current = forecast?.Current;
            var daily = forecast?.Daily;
            if (current is null || daily is null)
                return null;

            var forecastDays = daily.Dates
                .Select((date, index) => new { date, index })
                .Where(x => daily.MaxTemps[x.index].HasValue && daily.MinTemps[x.index].HasValue && daily.WeatherCodes[x.index].HasValue)
                .Select(x => new DailyForecastDTO
                {
                    Date = DateTime.Parse(x.date, CultureInfo.InvariantCulture),
                    MaxTemp = daily.MaxTemps[x.index]!.Value,
                    MinTemp = daily.MinTemps[x.index]!.Value,
                    WeatherCode = daily.WeatherCodes[x.index]!.Value
                })
                .ToList();

            return new DestinationWeatherDTO
            {
                Destination = destination,
                CurrentWeather = current,
                ForecastDays = forecastDays
            };
        }
    }
}
