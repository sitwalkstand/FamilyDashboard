using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.SchoolMenus;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Workers;

/// <summary>
/// Fetches each menu chosen by an enabled School Menu widget. Menus are published a month at a
/// time and rarely change, so each is refreshed every few hours, sooner after a failed fetch.
/// </summary>
public class SchoolMenuRefreshWorker(
    IServiceScopeFactory scopeFactory,
    DashboardStateService state,
    ILogger<SchoolMenuRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, DateTime> _nextFetchUtc = [];
    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Widget edits in admin wake the worker so a newly chosen menu is fetched right away.
        state.ScreensChanged += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshDueMenusAsync(stoppingToken);
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

    private async Task RefreshDueMenusAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var menuService = scope.ServiceProvider.GetRequiredService<ISchoolMenuService>();

            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var menuTypeIds = (await db.Widgets
                    .Where(widget => widget.WidgetType == "SchoolMenu" && widget.Enabled && widget.Screen!.Enabled)
                    .ToListAsync(stoppingToken))
                .Select(widget => widget.SchoolMenuSettings.MenuTypeId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet();

            foreach (var stale in _nextFetchUtc.Keys.Where(id => !menuTypeIds.Contains(id)).ToList())
            {
                _nextFetchUtc.Remove(stale);
            }

            foreach (var menuTypeId in menuTypeIds)
            {
                if (_nextFetchUtc.TryGetValue(menuTypeId, out var due) && DateTime.UtcNow < due)
                {
                    continue;
                }

                var menu = await menuService.GetMenuAsync(menuTypeId, DateOnly.FromDateTime(DateTime.Now), stoppingToken);
                _nextFetchUtc[menuTypeId] = DateTime.UtcNow + (menu is null ? RetryInterval : RefreshInterval);
                if (menu is not null)
                {
                    state.UpdateSchoolMenu(menu);
                    logger.LogInformation("Refreshed school menu {MenuName}: {DayCount} days", menu.Name, menu.Days.Count);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "School menu refresh failed");
        }
    }
}
