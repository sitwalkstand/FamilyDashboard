namespace FamilyDashboard.Web.Services.Weather;

/// <summary>Display text and Font Awesome icons for WMO weather codes and other derived values.</summary>
public static class WeatherConditions
{
    private const double SynodicMonthDays = 29.530588853;
    private static readonly DateTime ReferenceNewMoonUtc = new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

    public static string Describe(int code, bool isDay) => code switch
    {
        0 => isDay ? "Sunny" : "Clear",
        1 => isDay ? "Mostly sunny" : "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Foggy",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Rain showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorms",
        96 or 99 => "Thunderstorms with hail",
        _ => "Unknown",
    };

    public static string IconClass(int code, bool isDay) => "fa-solid " + code switch
    {
        0 => isDay ? "fa-sun" : "fa-moon",
        1 or 2 => isDay ? "fa-cloud-sun" : "fa-cloud-moon",
        3 => "fa-cloud",
        45 or 48 => "fa-smog",
        51 or 53 or 55 or 61 or 63 or 80 or 81 => isDay ? "fa-cloud-sun-rain" : "fa-cloud-moon-rain",
        56 or 57 or 65 or 66 or 67 or 82 => "fa-cloud-showers-heavy",
        71 or 73 or 75 or 77 or 85 or 86 => "fa-snowflake",
        95 or 96 or 99 => "fa-cloud-bolt",
        _ => "fa-cloud",
    };

    /// <summary>A short phrase such as "Sunny this afternoon" for the next few hours.</summary>
    public static string Summary(WeatherDto weather, DateTimeOffset now)
    {
        var upcoming = weather.Hourly
            .Where(hour => hour.Time > now.AddHours(-1))
            .Take(6)
            .ToList();
        if (upcoming.Count == 0)
        {
            return Describe(weather.WeatherCode, weather.IsDay);
        }

        // WMO codes grow with severity, so the highest code is the weather worth mentioning.
        var worst = upcoming.MaxBy(hour => hour.WeatherCode)!;
        var localHour = upcoming[0].Time.Hour;
        var period = localHour switch
        {
            >= 5 and < 12 => "this morning",
            >= 12 and < 17 => "this afternoon",
            >= 17 and < 21 => "this evening",
            _ => "tonight",
        };
        return $"{Describe(worst.WeatherCode, period != "tonight" && worst.IsDay)} {period}";
    }

    public static (string Name, string IconClass) MoonPhase(DateTime utcNow)
    {
        var age = ((utcNow - ReferenceNewMoonUtc).TotalDays % SynodicMonthDays + SynodicMonthDays) % SynodicMonthDays;
        var phase = (int)Math.Floor(age / SynodicMonthDays * 8 + 0.5) % 8;
        var name = phase switch
        {
            0 => "New moon",
            1 => "Waxing crescent",
            2 => "First quarter",
            3 => "Waxing gibbous",
            4 => "Full moon",
            5 => "Waning gibbous",
            6 => "Last quarter",
            _ => "Waning crescent",
        };
        return (name, phase == 4 ? "fa-solid fa-circle" : phase == 0 ? "fa-regular fa-circle" : "fa-solid fa-moon");
    }

    public static string CompassDirection(double degrees)
    {
        string[] directions = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return directions[(int)Math.Round(((degrees % 360) + 360) % 360 / 45) % 8];
    }

    public static string UvLevel(double uvIndex) => uvIndex switch
    {
        < 3 => "Low",
        < 6 => "Moderate",
        < 8 => "High",
        < 11 => "Very high",
        _ => "Extreme",
    };
}
