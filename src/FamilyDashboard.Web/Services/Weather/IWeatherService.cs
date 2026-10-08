namespace FamilyDashboard.Web.Services.Weather;

public interface IWeatherService
{
    Task<WeatherDto?> GetForecastAsync(WeatherLocationKey location, CancellationToken cancellationToken = default);

    /// <summary>Finds a place from a city ("Syracuse, NY"), region, or US ZIP code.</summary>
    Task<WeatherLocation?> FindLocationAsync(string query, CancellationToken cancellationToken = default);
}

public sealed record WeatherLocation(string DisplayName, double Latitude, double Longitude);
