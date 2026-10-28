using System.Net.Http;
using System.Text.Json;

namespace DesktopManager.Services;

public sealed record WeatherSnapshot(string City, double Temperature, double FeelsLike, int RelativeHumidity, double WindSpeed, int WeatherCode, bool IsDay, DateTime UpdatedAt);

public static class WeatherService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<WeatherSnapshot?> GetCurrentAsync(string city, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        var geocodeUrl = "https://geocoding-api.open-meteo.com/v1/search?" +
            $"name={Uri.EscapeDataString(city.Trim())}&count=1&language=zh&format=json";
        using var geoResponse = await Client.GetAsync(geocodeUrl, cancellationToken);
        geoResponse.EnsureSuccessStatusCode();
        using var geoDocument = JsonDocument.Parse(await geoResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!geoDocument.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var location = results[0];
        var latitude = location.GetProperty("latitude").GetDouble();
        var longitude = location.GetProperty("longitude").GetDouble();
        var cityName = location.TryGetProperty("name", out var name) ? name.GetString() ?? city : city;
        var forecastUrl = "https://api.open-meteo.com/v1/forecast?" +
            $"latitude={latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,wind_speed_10m&timezone=auto";
        using var weatherResponse = await Client.GetAsync(forecastUrl, cancellationToken);
        weatherResponse.EnsureSuccessStatusCode();
        using var weatherDocument = JsonDocument.Parse(await weatherResponse.Content.ReadAsStringAsync(cancellationToken));
        var current = weatherDocument.RootElement.GetProperty("current");
        return new WeatherSnapshot(
            cityName,
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            current.GetProperty("relative_humidity_2m").GetInt32(),
            current.GetProperty("wind_speed_10m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("is_day").GetInt32() == 1,
            DateTime.Now);
    }

    public static (string Description, string Symbol) Describe(int code, bool isDay) => code switch
    {
        0 => ("晴朗", isDay ? "☀" : "☾"),
        1 => ("大致晴朗", isDay ? "🌤" : "☾"),
        2 => ("局部多云", "⛅"),
        3 => ("阴天", "☁"),
        45 or 48 => ("有雾", "🌫"),
        51 or 53 or 55 => ("毛毛雨", "🌦"),
        56 or 57 => ("冻毛毛雨", "🌧"),
        61 or 63 or 65 => ("下雨", "🌧"),
        66 or 67 => ("冻雨", "🌧"),
        71 or 73 or 75 or 77 => ("下雪", "❄"),
        80 or 81 or 82 => ("阵雨", "🌦"),
        85 or 86 => ("阵雪", "🌨"),
        95 => ("雷暴", "⛈"),
        96 or 99 => ("雷暴伴冰雹", "⛈"),
        _ => ("天气状况", "☁")
    };
}
