using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.Weather;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Workers;

/// <summary>
/// Fetches one forecast per distinct location/unit used by enabled Weather widgets, each on the
/// shortest refresh interval configured among the widgets that share it.
/// </summary>
public class WeatherRefreshWorker(
    IServiceScopeFactory scopeFactory,
    DashboardStateService state,
    ILogger<WeatherRefreshWorker> logger) : BackgroundService
{
    private const int MinRefreshMinutes = 5;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<WeatherLocationKey, DateTime> _lastFetchedUtc = [];
    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Widget edits in admin wake the worker so a new location is fetched right away.
        state.ScreensChanged += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshDueLocationsAsync(stoppingToken);
                await _wake.WaitAsync(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            state.ScreensChanged -= Wake;
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private async Task RefreshDueLocationsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var weatherService = scope.ServiceProvider.GetRequiredService<IWeatherService>();

            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var widgets = await db.Widgets
                .Where(widget => widget.WidgetType == "Weather" && widget.Enabled && widget.Screen!.Enabled)
                .ToListAsync(stoppingToken);

            var locations = widgets
                .GroupBy(WeatherLocationKey.For)
                .ToDictionary(
                    group => group.Key,
                    group => TimeSpan.FromMinutes(group.Min(widget => Math.Max(MinRefreshMinutes, widget.WeatherRefreshMinutes))));

            foreach (var stale in _lastFetchedUtc.Keys.Where(key => !locations.ContainsKey(key)).ToList())
            {
                _lastFetchedUtc.Remove(stale);
            }

            foreach (var (location, interval) in locations)
            {
                if (_lastFetchedUtc.TryGetValue(location, out var lastFetched) && DateTime.UtcNow - lastFetched < interval)
                {
                    continue;
                }

                // Record the attempt even on failure so an unreachable API is retried on the
                // widget's interval rather than every minute.
                _lastFetchedUtc[location] = DateTime.UtcNow;
                var forecast = await weatherService.GetForecastAsync(location, stoppingToken);
                if (forecast is not null)
                {
                    state.UpdateWeather(location, forecast);
                    logger.LogInformation("Refreshed weather for {Latitude},{Longitude}: {Temp} {Unit}",
                        location.Latitude, location.Longitude, forecast.CurrentTemp, location.TemperatureUnit);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Weather refresh failed");
        }
    }
}
