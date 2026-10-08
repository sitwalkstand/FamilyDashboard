namespace FamilyDashboard.Web.Services.Weather;

/// <summary>Temperatures are in the unit of the <see cref="WeatherLocationKey"/> they were fetched for.</summary>
public record WeatherDto(
    double CurrentTemp,
    int WeatherCode,
    double TodayHigh,
    double TodayLow,
    IReadOnlyList<DailyForecastDto> Forecast);

public record DailyForecastDto(
    DateOnly Date,
    double High,
    double Low,
    int WeatherCode);

/// <summary>
/// Identifies one forecast to fetch. Weather widgets with the same location and unit share a forecast.
/// </summary>
public readonly record struct WeatherLocationKey(double Latitude, double Longitude, string TemperatureUnit)
{
    public static WeatherLocationKey For(Data.Entities.DashboardWidget widget) =>
        new(widget.WeatherLatitude, widget.WeatherLongitude, NormalizeUnit(widget.WeatherTemperatureUnit));

    public static string NormalizeUnit(string? unit) =>
        string.Equals(unit, "celsius", StringComparison.OrdinalIgnoreCase) ? "celsius" : "fahrenheit";
}
