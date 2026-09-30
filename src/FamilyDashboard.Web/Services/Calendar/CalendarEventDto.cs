namespace FamilyDashboard.Web.Services.Calendar;

public record CalendarEventDto(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string CalendarName,
    string Color,
    string Icon);

public sealed record CalendarLegendDto(string CalendarName, string Color, string Icon);
