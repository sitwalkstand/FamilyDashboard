using FamilyDashboard.Web.Data.Entities;

namespace FamilyDashboard.Web.Services.Calendar;

public interface ICalendarService
{
    /// <summary>
    /// Fetches and merges upcoming events from every enabled <see cref="CalendarFeed"/>.
    /// </summary>
    Task<List<CalendarEventDto>> GetUpcomingEventsAsync(
        IEnumerable<CalendarFeed> feeds,
        int lookAheadDays,
        CancellationToken cancellationToken = default);
}
