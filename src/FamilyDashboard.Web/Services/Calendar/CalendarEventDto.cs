using FamilyDashboard.Web.Services;

namespace FamilyDashboard.Web.Services.Calendar;

public record CalendarEventDto(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string CalendarName,
    string Color,
    string Icon)
{
    /// <summary>Start in Eastern time for display. All-day events are dates, so they keep the server's local date.</summary>
    public DateTimeOffset StartLocal => IsAllDay
        ? new DateTimeOffset(Start.LocalDateTime)
        : TimeZoneInfo.ConvertTime(Start, DashboardTimeZone.Zone);
}

public sealed record CalendarLegendDto(string CalendarName, string Color, string Icon);
