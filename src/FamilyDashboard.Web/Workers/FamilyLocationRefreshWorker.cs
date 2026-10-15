using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.FamilyLocations;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Workers;

/// <summary>
/// Polls Home Assistant for device tracker locations while at least one Family Locations or Family Map widget is
/// enabled. A failed poll keeps the last locations and records the failure so the widget can say
/// the data is stale.
/// </summary>
public class FamilyLocationRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IFamilyLocationService locations,
    DashboardStateService state,
    ILogger<FamilyLocationRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Adding or enabling the widget in admin wakes the worker so it doesn't wait out the idle delay.
        state.ScreensChanged += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var polled = await RefreshAsync(stoppingToken);
                await _wake.WaitAsync(polled ? RefreshInterval : IdleInterval, stoppingToken);
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

    /// <summary>Returns true when a widget needed the data, so the caller uses the full poll interval.</summary>
    private async Task<bool> RefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var needed = await db.Widgets.AnyAsync(
                widget => (widget.WidgetType == "FamilyLocations" || widget.WidgetType == "FamilyMap") && widget.Enabled && widget.Screen!.Enabled,
                stoppingToken);
            if (!needed)
            {
                return false;
            }

            if (!locations.IsConfigured)
            {
                state.UpdateFamilyLocations(null, new FamilyLocationStatus(DateTime.UtcNow, false, "Home Assistant isn't configured"));
                return true;
            }

            var members = await locations.GetMembersAsync(stoppingToken);
            state.UpdateFamilyLocations(members, new FamilyLocationStatus(DateTime.UtcNow, true, null));
            logger.LogInformation("Refreshed family locations: {Count} trackers", members.Count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Home Assistant being down or the token being revoked is routine; the widget shows it.
            logger.LogWarning("Family location refresh failed: {Message}", ex.Message);
            state.UpdateFamilyLocations(null, new FamilyLocationStatus(DateTime.UtcNow, false, ex.Message));
            return true;
        }
    }
}
