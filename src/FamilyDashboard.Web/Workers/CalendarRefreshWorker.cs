using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.Calendar;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Workers;

public class CalendarRefreshWorker(
    IServiceScopeFactory scopeFactory,
    DashboardStateService state,
    IConfiguration configuration,
    ILogger<CalendarRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("Calendar:RefreshMinutes", 15);
        var lookAheadDays = configuration.GetValue("Calendar:LookAheadDays", 14);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                var calendarService = scope.ServiceProvider.GetRequiredService<ICalendarService>();

                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var feeds = await db.CalendarFeeds.Where(f => f.Enabled).ToListAsync(stoppingToken);

                var events = await calendarService.GetUpcomingEventsAsync(feeds, lookAheadDays, stoppingToken);
                state.UpdateEvents(events);

                logger.LogInformation("Refreshed {Count} calendar events from {FeedCount} feeds", events.Count, feeds.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Calendar refresh failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
