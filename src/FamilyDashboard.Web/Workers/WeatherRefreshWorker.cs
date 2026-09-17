using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.Weather;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Workers;

public class WeatherRefreshWorker(
    IServiceScopeFactory scopeFactory,
    DashboardStateService state,
    IConfiguration configuration,
    ILogger<WeatherRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("Weather:RefreshMinutes", 30);
        var latitude = configuration.GetValue("Weather:Latitude", 38.9894);
        var longitude = configuration.GetValue("Weather:Longitude", -77.4794);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                var weatherService = scope.ServiceProvider.GetRequiredService<IWeatherService>();

                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var settings = await db.Settings.SingleAsync(stoppingToken);
                latitude = settings.WeatherLatitude;
                longitude = settings.WeatherLongitude;
                intervalMinutes = settings.WeatherRefreshMinutes;
                var forecast = await weatherService.GetForecastAsync(latitude, longitude, stoppingToken);
                if (forecast is not null)
                {
                    state.UpdateWeather(forecast);
                    logger.LogInformation("Refreshed weather: {TempF}F", forecast.CurrentTempF);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Weather refresh failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
