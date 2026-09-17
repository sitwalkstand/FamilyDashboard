namespace FamilyDashboard.Web.Services.Weather;

public interface IWeatherService
{
    Task<WeatherDto?> GetForecastAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
    Task<WeatherLocation?> FindLocationByZipAsync(string zipCode, CancellationToken cancellationToken = default);
}

public sealed record WeatherLocation(string Name, string? Region, string? Country, double Latitude, double Longitude);
