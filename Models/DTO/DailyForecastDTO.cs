namespace Packo.Models.DTO
{
    using Packo.Helpers;
    using Packo.Services;

    public class DailyForecastDTO
    {
        public DateTime Date { get; set; }
        public decimal MaxTemp { get; set; }
        public decimal MinTemp { get; set; }
        public int WeatherCode { get; set; }
        public string WeatherIcon => WeatherDisplay.GetConditionIcon(WeatherCode);
        public string ConditionDescription => LocalizationResourceManager.Instance[WeatherDisplay.GetConditionKey(WeatherCode)];
        public bool IsFreezing => MinTemp < 0;
    }
}
