namespace FamilyDashboard.Web.Services.Weather;

public record WeatherDto(
    double CurrentTempF,
    int WeatherCode,
    double TodayHighF,
    double TodayLowF,
    IReadOnlyList<DailyForecastDto> Forecast);

public record DailyForecastDto(
    DateOnly Date,
    double HighF,
    double LowF,
    int WeatherCode);
