using CommunityToolkit.Mvvm.Messaging;
using Packo.Messages;
using Packo.Repositories.Interfaces;
using Packo.Services;
using Packo.Services.Interfaces;
using Packo.ViewModels;
using System.Net;
using Packo.Helpers;
using Packo.Models.DTO;
using Packo.Services.ResponseObjects;

if (args.Contains("--live"))
{
    var service = new WeatherService(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
    var location = await service.GetLocationAsync("Warszawa") ?? throw new Exception("Geocoding failed");
    var forecast = await service.GetForecastAsync(location.Latitude, location.Longitude);
    Check(forecast?.Current != null && forecast.Daily?.Dates.Count == 15,
        "Live Open-Meteo geocoding and combined current/15-day forecast deserialize correctly");
    return;
}

var trips = new Trips();
var http = new WeatherHandler();
var preload = new WeatherPreloadService(trips, new WeatherService(new HttpClient(http)));
await preload.PrefetchAsync();
trips.Source = () => Task.FromResult<IReadOnlyList<string>>(["Warszawa"]);
WeakReferenceMessenger.Default.Send(new LoginCompletedMessage());
var afterLogin = await preload.GetWeatherAsync();
Check(afterLogin.Forecasts.Count == 1 && !afterLogin.HasFailures,
    "Login replaces a guest's cached empty forecast without visiting Trips");
Check(http.Requests.Count == 2 && http.Requests[1].Contains("&current=") && http.Requests[1].Contains("&daily="),
    "Each destination uses one geocoding request and one combined forecast request");
var queryKeys = new Uri(http.Requests[1]).Query.TrimStart('?').Split('&').Select(part => part.Split('=')[0]).ToList();
Check(queryKeys.Count == queryKeys.Distinct().Count(), "The combined forecast request has no duplicate query parameters");
Check(http.Requests[1].Contains("wind_speed_unit=kmh") && http.Requests[1].Contains("temperature_unit=celsius")
    && http.Requests[1].Contains("wind_speed_10m,is_day"),
    "Weather requests explicitly use Celsius and km/h and include day/night conditions");
Check(afterLogin.Forecasts.Single().CurrentWeather is { WindSpeed: 11.9m, Humidity: 80, IsDay: 0 },
    "Wind, humidity and day/night data survive API deserialization and forecast loading");

var cold = new DestinationWeatherDTO
{
    CurrentWeather = new CurrentWeather { Temperature = -0.1m, WeatherCode = 0, IsDay = 0, WindSpeed = 39.9m, Humidity = 79 }
};
Check(cold.IsFreezing && cold.WeatherIcon == "weather_moon.png" && cold.ConditionDescription == "WeatherClearNight",
    "A clear freezing night keeps its moon icon and marks the temperature as freezing");
Check(!cold.IsWindy && !cold.IsHighHumidity, "Values just below emphasis thresholds stay unhighlighted");
cold.CurrentWeather.Temperature = 0;
cold.CurrentWeather.WindSpeed = 40;
cold.CurrentWeather.Humidity = 80;
Check(!cold.IsFreezing && cold.IsWindy && cold.IsHighHumidity,
    "Freezing starts below zero; wind and humidity emphasis starts at 40 km/h and 80 percent");
Check(cold.WindDescription == "WeatherWindStrong" && cold.HumidityDescription == "WeatherHumidityHigh",
    "Highlighted measurements have corresponding readable descriptions");
Check(WeatherDisplay.GetWindLevelKey(19.9m) == "WeatherWindLight"
    && WeatherDisplay.GetWindLevelKey(20) == "WeatherWindModerate"
    && WeatherDisplay.GetHumidityLevelKey(39) == "WeatherHumidityLow"
    && WeatherDisplay.GetHumidityLevelKey(40) == "WeatherHumidityModerate",
    "Wind and humidity level descriptions change at their intended boundaries");
Check(new[] { 71, 73, 75, 77, 85, 86 }.All(code => WeatherDisplay.GetConditionIcon(code) == "weather_snow.png")
    && new[] { 56, 57, 66, 67 }.All(code => WeatherDisplay.GetConditionIcon(code) == "weather_rain.png"
        && WeatherDisplay.GetConditionKey(code) == "WeatherFreezingRain"),
    "Snow codes show snow, while freezing rain remains rain");
Check(WeatherDisplay.GetConditionIcon(95) == "weather_storm.png"
    && WeatherDisplay.GetConditionIcon(45) == "weather_fog.png"
    && WeatherDisplay.GetConditionIcon(999) == "weather_unknown.png",
    "Storm, fog and unknown conditions have distinct icons");
var frostyDay = new DailyForecastDTO { WeatherCode = 0, MinTemp = -2, MaxTemp = 4 };
Check(frostyDay.WeatherIcon == "weather_sun.png" && frostyDay.IsFreezing,
    "A sunny forecast can independently mark an overnight freezing minimum");

trips.Source = () => Task.FromResult<IReadOnlyList<string>>(["Kraków"]);
WeakReferenceMessenger.Default.Send(new TripDataChangedMessage());
Check((await preload.GetWeatherAsync()).Forecasts.Single().Destination == "Kraków",
    "Trip changes invalidate weather independently of the Trips view model");
trips.Source = () => Task.FromResult<IReadOnlyList<string>>([]);
WeakReferenceMessenger.Default.Send(new LogoutCompletedMessage());
Check((await preload.GetWeatherAsync()).Forecasts.Count == 0,
    "Logout clears the previously cached account's forecast");

var blocked = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
trips.Source = () => blocked.Task;
preload.Invalidate();
var stale = preload.GetWeatherAsync();
trips.Source = () => Task.FromResult<IReadOnlyList<string>>(["Warszawa"]);
WeakReferenceMessenger.Default.Send(new LoginCompletedMessage());
blocked.SetResult([]);
Check((await stale.WaitAsync(TimeSpan.FromSeconds(3))).Forecasts.Count == 1,
    "An in-flight guest request cannot restore an empty result after login");

trips.Source = () => Task.FromResult<IReadOnlyList<string>>(["unknown"]);
preload.Invalidate();
var unknown = await preload.GetWeatherAsync();
Check(unknown.HasFailures && unknown.UnavailableDestinations.Single() == "unknown",
    "An unrecognized destination is reported as unavailable, not as no trips");
http.ResolveUnknown = true;
Check((await preload.GetWeatherAsync()).Forecasts.Count == 1,
    "Unavailable forecasts are retried instead of being cached as permanently empty");

var session = new Session(); // UserId is null: local trips also need forecasts.
var vm = new WeatherViewModel(null!, null!, session, null!, trips, null!, preload);
await vm.OnAppearingAsync();
Check(vm.HasForecasts && !vm.ShowEmptyState && !vm.HasLoadError,
    "The weather view loads forecasts for local users without a cloud login");

var cloudTrips = new Trips();
var emptyCache = new WeatherPreloadService(cloudTrips, new WeatherService(new HttpClient(new WeatherHandler())));
await emptyCache.PrefetchAsync();
cloudTrips.Source = () => Task.FromResult<IReadOnlyList<string>>(["Warszawa"]);
Check((await emptyCache.GetWeatherAsync()).Forecasts.Count == 1,
    "A previously empty startup result is rechecked on entry even without a local trip-change notification");

trips.Source = () => throw new HttpRequestException("Simulated trips API failure");
preload.Invalidate();
await vm.OnAppearingAsync();
Check(vm.HasLoadError && !vm.ShowEmptyState && !vm.IsLoading,
    "A failed request shows an error and exits loading instead of displaying no trips");
trips.Source = () => Task.FromResult<IReadOnlyList<string>>(["Warszawa"]);
await vm.RetryLoadCommand.ExecuteAsync(null);
Check(vm.HasForecasts && !vm.HasLoadError && !vm.IsLoading,
    "Retry recovers from a request failure");

trips.Source = () => Task.FromResult<IReadOnlyList<string>>(["Warszawa", "failing"]);
preload.Invalidate();
await vm.OnAppearingAsync();
Check(vm.Destinations.Count == 1 && vm.HasLoadError && vm.LoadErrorMessage.Contains("failing") && !vm.ShowEmptyState,
    "One failed destination keeps healthy forecasts visible and identifies the unavailable location");

blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
trips.Source = () => blocked.Task;
preload.Invalidate();
var loading = vm.OnAppearingAsync();
Check(vm.IsLoading && !vm.ShowEmptyState, "Loading is visible while a weather request is pending");
blocked.SetResult([]);
await loading;
Check(!vm.IsLoading && vm.ShowEmptyState && !vm.HasLoadError,
    "No trips is shown only after a successful request actually returns no destinations");

http.Stall = true;
var timed = new WeatherPreloadService(new Trips
{
    Source = () => Task.FromResult<IReadOnlyList<string>>(["Warszawa"])
}, new WeatherService(new HttpClient(http) { Timeout = TimeSpan.FromMilliseconds(30) }));
Check((await timed.GetWeatherAsync().WaitAsync(TimeSpan.FromSeconds(3))).HasFailures,
    "A weather timeout finishes with a retryable unavailable result");

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS: {description}");
}

sealed class Trips : ITripRepository
{
    public Func<Task<IReadOnlyList<string>>> Source { get; set; } = () => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<string>> GetActiveDestinationsAsync() => Source();
}

sealed class Session : ISessionService
{
    public string? UserId => null;
    public string? LocalUserId => "local";
}

sealed class WeatherHandler : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public bool ResolveUnknown { get; set; }
    public bool Stall { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri.AbsoluteUri);
        if (Stall) await Task.Delay(Timeout.Infinite, token);
        string json;
        if (uri.Host.StartsWith("geocoding"))
        {
            var name = Uri.UnescapeDataString(uri.Query.Split("name=")[1].Split('&')[0]);
            json = name == "unknown" && !ResolveUnknown ? "{}"
                : name == "failing" ? "{\"results\":[{\"latitude\":1,\"longitude\":1}]}"
                : "{\"results\":[{\"latitude\":52.23,\"longitude\":21.01}]}";
        }
        else
        {
            if (uri.Query.StartsWith("?latitude=1&")) throw new HttpRequestException("Destination forecast unavailable");
            json = """
                {"current":{"temperature_2m":10.1,"relative_humidity_2m":80,"weather_code":3,"wind_speed_10m":11.9,"is_day":0},
                 "daily":{"time":["2026-10-26"],"temperature_2m_max":[12.5],"temperature_2m_min":[4.5],"weather_code":[3]}}
                """;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
