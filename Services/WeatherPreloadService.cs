using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Services.Interfaces;

namespace Packo.Services
{
    public class WeatherPreloadService : IWeatherPreloadService
    {
        private readonly ITripRepository _tripRepository;
        private readonly WeatherService _weatherService;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Task<IReadOnlyList<DestinationWeatherDTO>>? _cachedRequest;

        public WeatherPreloadService(ITripRepository tripRepository, WeatherService weatherService)
        {
            _tripRepository = tripRepository;
            _weatherService = weatherService;
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

        public async Task<IReadOnlyList<DestinationWeatherDTO>> GetWeatherAsync(bool forceRefresh = false)
        {
            Task<IReadOnlyList<DestinationWeatherDTO>> request;

            await _gate.WaitAsync();
            try
            {
                if (forceRefresh || _cachedRequest is null)
                    _cachedRequest = LoadWeatherAsync();

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

        public void Invalidate() => _cachedRequest = null;

        private async Task<IReadOnlyList<DestinationWeatherDTO>> LoadWeatherAsync()
        {
            var destinations = (await _tripRepository.GetActiveDestinationsAsync())
                .Where(destination => !string.IsNullOrWhiteSpace(destination))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var forecasts = await Task.WhenAll(destinations.Select(GetWeatherForDestinationAsync));
            return forecasts
                .Where(forecast => forecast is not null)
                .Cast<DestinationWeatherDTO>()
                .ToList();
        }

        private async Task<DestinationWeatherDTO?> GetWeatherForDestinationAsync(string destination)
        {
            var location = await _weatherService.GetLocationAsync(destination);
            if (location is null)
                return null;

            var current = await _weatherService.GetWeatherAsync(location.Latitude, location.Longitude);
            var daily = await _weatherService.GetDailyWeatherAsync(location.Latitude, location.Longitude);
            if (current is null || daily is null)
                return null;

            var forecastDays = daily.Dates
                .Select((date, index) => new { date, index })
                .Where(x => daily.MaxTemps[x.index].HasValue && daily.MinTemps[x.index].HasValue && daily.WeatherCodes[x.index].HasValue)
                .Select(x => new DailyForecastDTO
                {
                    Date = DateTime.Parse(x.date),
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
