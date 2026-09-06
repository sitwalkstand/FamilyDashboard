namespace FamilyDashboard.Web.Services.Weather;

public interface IWeatherService
{
    Task<WeatherDto?> GetForecastAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}
