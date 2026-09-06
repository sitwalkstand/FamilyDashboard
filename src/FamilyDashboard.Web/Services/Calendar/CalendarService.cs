using FamilyDashboard.Web.Data.Entities;
using Ical.Net;
using Ical.Net.CalendarComponents;

namespace FamilyDashboard.Web.Services.Calendar;

public class CalendarService(HttpClient httpClient, ILogger<CalendarService> logger) : ICalendarService
{
    public async Task<List<CalendarEventDto>> GetUpcomingEventsAsync(
        IEnumerable<CalendarFeed> feeds,
        int lookAheadDays,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CalendarEventDto>();
        var rangeStart = DateTime.Today;
        var rangeEnd = rangeStart.AddDays(lookAheadDays);

        foreach (var feed in feeds.Where(f => f.Enabled))
        {
            try
            {
                var ics = await httpClient.GetStringAsync(feed.IcsUrl, cancellationToken);
                var calendar = global::Ical.Net.Calendar.Load(ics);

                // Expands recurring events (RRULE) into concrete occurrences in range.
                var occurrences = calendar.GetOccurrences(rangeStart, rangeEnd);

                foreach (var occurrence in occurrences)
                {
                    if (occurrence.Source is not CalendarEvent calEvent)
                    {
                        continue;
                    }

                    var start = occurrence.Period.StartTime.AsDateTimeOffset;
                    var end = occurrence.Period.EndTime?.AsDateTimeOffset ?? start;

                    results.Add(new CalendarEventDto(
                        Title: calEvent.Summary ?? "(untitled event)",
                        Start: start,
                        End: end,
                        IsAllDay: calEvent.IsAllDay,
                        CalendarName: feed.DisplayName,
                        Color: feed.Color));
                }
            }
            catch (Exception ex)
            {
                // Don't let one broken/unreachable feed take the whole widget down -
                // log it and keep whatever the other feeds returned.
                logger.LogWarning(ex, "Failed to fetch/parse calendar feed {FeedName}", feed.DisplayName);
            }
        }

        return results.OrderBy(e => e.Start).ToList();
    }
}
