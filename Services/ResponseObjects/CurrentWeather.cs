using System.Text.Json.Serialization;

namespace Packo.Services.ResponseObjects
{
    public class CurrentWeather
    {
        [JsonPropertyName("temperature_2m")]
        public decimal Temperature { get; set; }

        [JsonPropertyName("relative_humidity_2m")]
        public int Humidity { get; set; }

        [JsonPropertyName("weather_code")]
        public int WeatherCode { get; set; }

        [JsonPropertyName("wind_speed_10m")]
        public decimal WindSpeed { get; set; }

        [JsonPropertyName("is_day")]
        public int IsDay { get; set; } = 1;
    }
}
