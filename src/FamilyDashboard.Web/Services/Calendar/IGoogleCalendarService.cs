using FamilyDashboard.Web.Data.Entities;

namespace FamilyDashboard.Web.Services.Calendar;

public interface IGoogleCalendarService
{
    bool IsConfigured { get; }
    string CreateAuthorizationUrl(string redirectUri);
    string ValidateState(string state);
    Task CompleteAuthorizationAsync(string code, string redirectUri, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoogleCalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalendarEventDto>> GetEventsAsync(
        CalendarFeed feed,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default);
}

public sealed record GoogleCalendarInfo(string Id, string Summary, string? Description, string? BackgroundColor);
