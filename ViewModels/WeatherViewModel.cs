using Packo.Extensions;
using Packo.Interfaces;
using Packo.Models.DTO;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using System.Collections.Concurrent;

namespace Packo.ViewModels
{
    public class WeatherViewModel : BaseViewModel
    {
        public ObservableRangeCollection<DestinationWeatherDTO> Destinations { get; } = new ObservableRangeCollection<DestinationWeatherDTO>();

        private readonly WeatherService _weatherService;
        private bool _isLoading;

        public WeatherViewModel(ILocalUserService localUserService, ISupabaseService supabase, ISessionService sessionService, IPackingItemRepository packingItemRepository, ITripRepository tripRepository, IGoogleAuthService googleAuthService, WeatherService weatherService) : base(localUserService, supabase, sessionService, packingItemRepository, tripRepository, googleAuthService)
        {
            _weatherService = weatherService;
        }

        private async Task LoadWeatherForDestinationsAsync(IReadOnlyList<string> destinations)
        {
            destinations ??= Array.Empty<string>();
            var orderedDestinations = destinations
                .Where(destination => !string.IsNullOrWhiteSpace(destination))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var existingWeather = Destinations
                .GroupBy(weather => weather.Destination, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var newlyLoadedWeather = new ConcurrentDictionary<string, DestinationWeatherDTO>(StringComparer.OrdinalIgnoreCase);

            // Fetch missing forecasts concurrently, then restore the trip-date order
            // supplied by the repository when replacing the visible collection.
            await Parallel.ForEachAsync(orderedDestinations, async (destination, ct) =>
            {
                if (string.IsNullOrWhiteSpace(destination)
                    || existingWeather.ContainsKey(destination)
                    || newlyLoadedWeather.ContainsKey(destination))
                    return;

                var weather = await GetWeatherForDestination(destination);
                if (weather != null)
                    newlyLoadedWeather.TryAdd(destination, weather);
            });

            var orderedWeather = orderedDestinations
                .Select(destination =>
                    existingWeather.TryGetValue(destination, out var existing)
                        ? existing
                        : newlyLoadedWeather.TryGetValue(destination, out var loaded) ? loaded : null)
                .Where(weather => weather is not null)
                .Cast<DestinationWeatherDTO>()
                .ToList();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Destinations.ReplaceRange(orderedWeather);
            });
        }


        private async Task<DestinationWeatherDTO?> GetWeatherForDestination(string destination)
        {
            var location = await _weatherService.GetLocationAsync(destination);

            if (location == null)
                return null;

            var current = await _weatherService.GetWeatherAsync(
                location.Latitude,
                location.Longitude);

            var daily = await _weatherService.GetDailyWeatherAsync(
                location.Latitude,
                location.Longitude);

            if (current == null || daily == null)
                return null;

            var forecastDays = daily.Dates
                .Select((date, index) => new { date, index })
                .Where(x =>
                    daily.MaxTemps[x.index].HasValue &&
                    daily.MinTemps[x.index].HasValue &&
                    daily.WeatherCodes[x.index].HasValue)
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

        public async Task OnAppearingAsync()
        {
            if (_isLoading)
                return;

            _isLoading = true;
            try
            {
                var destinations = await _tripRepository.GetActiveDestinationsAsync();
                await LoadWeatherForDestinationsAsync(destinations);
            }
            finally
            {
                _isLoading = false;
            }
        }

        protected override async Task OnNavigatedToAsync(IDictionary<string, object> query)
        {
            //await OnAppearingAsync();
        }
    }
}
