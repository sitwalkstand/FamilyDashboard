namespace FamilyDashboard.Web.Services.Weather;

public interface IWeatherService
{
    Task<WeatherDto?> GetForecastAsync(WeatherLocationKey location, CancellationToken cancellationToken = default);
    Task<WeatherLocation?> FindLocationByZipAsync(string zipCode, CancellationToken cancellationToken = default);
}

public sealed record WeatherLocation(string Name, string? Region, string? Country, double Latitude, double Longitude);
