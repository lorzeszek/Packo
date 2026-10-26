using Packo.Services.ResponseObjects;
using System.Globalization;
using System.Text.Json;

namespace Packo.Services
{
    public class WeatherService
    {
        private readonly HttpClient _httpClient;

        public WeatherService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<OpenMeteoResponse?> GetForecastAsync(
            double latitude,
            double longitude)
        {
            var latitudeText = latitude.ToString(CultureInfo.InvariantCulture);
            var longitudeText = longitude.ToString(CultureInfo.InvariantCulture);

            string url =
                $"https://api.open-meteo.com/v1/forecast" +
                $"?latitude={latitudeText}" +
                $"&longitude={longitudeText}" +
                $"&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m" +
                $"&forecast_days=15" +
                $"&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                $"&timezone=auto";

            var json = await _httpClient.GetStringAsync(url);

            var result = JsonSerializer.Deserialize<OpenMeteoResponse>(json);

            return result;
        }

        public async Task<GeocodingResult?> GetLocationAsync(string destination)
        {
            var encodedDestination = Uri.EscapeDataString(destination);

            string url =
                $"https://geocoding-api.open-meteo.com/v1/search?name={encodedDestination}&count=10&language=pl&format=json";

            var json = await _httpClient.GetStringAsync(url);

            var result = JsonSerializer.Deserialize<GeocodingResponse>(json);

            return result?.Results?.FirstOrDefault();
        }
    }
}
