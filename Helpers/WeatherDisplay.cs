namespace Packo.Helpers;

public static class WeatherDisplay
{
    // UI emphasis thresholds, in km/h and percent respectively.
    public const decimal StrongWindThreshold = 40m;
    public const int HighHumidityThreshold = 80;

    // Open-Meteo WMO codes: https://open-meteo.com/en/docs#weathervariables
    public static string GetConditionIcon(int code, bool isDay = true) => code switch
    {
        0 or 1 => isDay ? "weather_sun.png" : "weather_moon.png",
        2 => "weather_partly_cloudy.png",
        3 => "weather_cloud.png",
        45 or 48 => "weather_fog.png",
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "weather_rain.png",
        71 or 73 or 75 or 77 or 85 or 86 => "weather_snow.png",
        95 or 96 or 97 or 99 => "weather_storm.png",
        _ => "weather_unknown.png"
    };

    public static string GetConditionKey(int code, bool isDay = true) => code switch
    {
        0 or 1 => isDay ? "WeatherClear" : "WeatherClearNight",
        2 => "WeatherPartlyCloudy",
        3 => "WeatherCloudy",
        45 or 48 => "WeatherFog",
        51 or 53 or 55 => "WeatherDrizzle",
        56 or 57 or 66 or 67 => "WeatherFreezingRain",
        61 or 63 or 65 or 80 or 81 or 82 => "WeatherRain",
        71 or 73 or 75 or 77 or 85 or 86 => "WeatherSnow",
        95 or 96 or 97 or 99 => "WeatherStorm",
        _ => "WeatherUnknown"
    };

    public static string GetWindLevelKey(decimal speed) => speed switch
    {
        >= StrongWindThreshold => "WeatherWindStrong",
        >= 20m => "WeatherWindModerate",
        _ => "WeatherWindLight"
    };

    public static string GetHumidityLevelKey(int humidity) => humidity switch
    {
        >= HighHumidityThreshold => "WeatherHumidityHigh",
        < 40 => "WeatherHumidityLow",
        _ => "WeatherHumidityModerate"
    };
}
