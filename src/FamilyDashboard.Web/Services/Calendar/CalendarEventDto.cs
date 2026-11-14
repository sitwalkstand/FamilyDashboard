using FamilyDashboard.Web.Services;

namespace FamilyDashboard.Web.Services.Calendar;

public record CalendarEventDto(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string CalendarName,
    string Color,
    string Icon,
    int FeedId)
{
    /// <summary>Start in Eastern time for display. All-day events are dates, so they keep the date they were given.</summary>
    public DateTimeOffset StartLocal => IsAllDay ? Start : DashboardTimeZone.ToDashboardTime(Start);
}

public sealed record CalendarLegendDto(string CalendarName, string Color, string Icon);
