using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.HomeStatus;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Workers;

/// <summary>
/// Polls Home Assistant for entity states while at least one Home Status widget is enabled. A failed
/// poll keeps the last states and records the failure so the widget can say the data is stale.
/// </summary>
public class HomeStatusRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IHomeStatusService homeStatus,
    DashboardStateService state,
    ILogger<HomeStatusRefreshWorker> logger) : BackgroundService
{
    // Short, because the widget is about things like a leak or the alarm that shouldn't wait minutes.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
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
                widget => widget.WidgetType == "HomeStatus" && widget.Enabled && widget.Screen!.Enabled,
                stoppingToken);
            if (!needed)
            {
                return false;
            }

            if (!homeStatus.IsConfigured)
            {
                state.UpdateHomeStatus(null, new HomeStatusStatus(DateTime.UtcNow, false, "Home Assistant isn't configured"));
                return true;
            }

            var entities = await homeStatus.GetEntitiesAsync(stoppingToken);
            state.UpdateHomeStatus(entities, new HomeStatusStatus(DateTime.UtcNow, true, null));
            logger.LogDebug("Refreshed home status: {Count} entities", entities.Count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Home Assistant being down or the token being revoked is routine; the widget shows it.
            logger.LogWarning("Home status refresh failed: {Message}", ex.Message);
            state.UpdateHomeStatus(null, new HomeStatusStatus(DateTime.UtcNow, false, ex.Message));
            return true;
        }
    }
}
