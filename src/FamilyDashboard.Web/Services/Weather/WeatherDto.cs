namespace FamilyDashboard.Web.Services.Weather;

/// <summary>
/// Temperatures are in the unit of the <see cref="WeatherLocationKey"/> they were fetched for, and the other
/// measurements follow it: Fahrenheit forecasts use mph, inches, inHg and miles; Celsius uses km/h, mm, hPa and km.
/// </summary>
public record WeatherDto(
    double CurrentTemp,
    int WeatherCode,
    double TodayHigh,
    double TodayLow,
    IReadOnlyList<DailyForecastDto> Forecast)
{
    public double? FeelsLike { get; init; }
    public bool IsDay { get; init; } = true;
    public double? Humidity { get; init; }
    public double? Pressure { get; init; }
    public double? WindSpeed { get; init; }
    public double? WindDirection { get; init; }
    public double? UvIndex { get; init; }
    public double? Visibility { get; init; }
    public IReadOnlyList<HourlyForecastDto> Hourly { get; init; } = [];
}

/// <summary>Sunrise and sunset are local times at the forecast location.</summary>
public record DailyForecastDto(
    DateOnly Date,
    double High,
    double Low,
    int WeatherCode)
{
    public double? PrecipitationAmount { get; init; }
    public double? PrecipitationChance { get; init; }
    public DateTime? Sunrise { get; init; }
    public DateTime? Sunset { get; init; }
}

/// <summary><see cref="Time"/> carries the forecast location's UTC offset.</summary>
public record HourlyForecastDto(
    DateTimeOffset Time,
    double Temperature,
    int WeatherCode,
    bool IsDay,
    double? PrecipitationAmount,
    double? PrecipitationChance);

/// <summary>
/// Identifies one forecast to fetch. Weather widgets with the same location and unit share a forecast.
/// </summary>
public readonly record struct WeatherLocationKey(double Latitude, double Longitude, string TemperatureUnit)
{
    public static WeatherLocationKey For(Data.Entities.DashboardWidget widget) =>
        new(widget.WeatherLatitude, widget.WeatherLongitude, NormalizeUnit(widget.WeatherTemperatureUnit));

    public static string NormalizeUnit(string? unit) =>
        string.Equals(unit, "celsius", StringComparison.OrdinalIgnoreCase) ? "celsius" : "fahrenheit";

    public bool IsImperial => TemperatureUnit != "celsius";
}
