namespace Packo.Models.DTO;

public sealed record WeatherLoadResult(
    IReadOnlyList<DestinationWeatherDTO> Forecasts,
    IReadOnlyList<string> UnavailableDestinations)
{
    public bool HasFailures => UnavailableDestinations.Count > 0;
}
