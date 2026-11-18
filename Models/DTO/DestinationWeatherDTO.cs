using Packo.Services.ResponseObjects;

namespace Packo.Models.DTO
{
    using Packo.Helpers;
    using Packo.Services;

    public class DestinationWeatherDTO
    {
        public string Destination { get; set; }

        public CurrentWeather CurrentWeather { get; set; }

        public List<DailyForecastDTO> ForecastDays { get; set; } = [];

        public string WeatherIcon => WeatherDisplay.GetConditionIcon(CurrentWeather.WeatherCode, CurrentWeather.IsDay == 1);
        public string ConditionDescription => LocalizationResourceManager.Instance[
            WeatherDisplay.GetConditionKey(CurrentWeather.WeatherCode, CurrentWeather.IsDay == 1)];
        public bool IsFreezing => CurrentWeather.Temperature < 0;
        public bool IsWindy => CurrentWeather.WindSpeed >= WeatherDisplay.StrongWindThreshold;
        public bool IsHighHumidity => CurrentWeather.Humidity >= WeatherDisplay.HighHumidityThreshold;
        public string WindDescription => LocalizationResourceManager.Instance[WeatherDisplay.GetWindLevelKey(CurrentWeather.WindSpeed)];
        public string HumidityDescription => LocalizationResourceManager.Instance[WeatherDisplay.GetHumidityLevelKey(CurrentWeather.Humidity)];
    }
}
