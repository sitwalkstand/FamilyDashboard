using FamilyDashboard.Web.Data.Entities;
using Ical.Net;
using Ical.Net.CalendarComponents;

namespace FamilyDashboard.Web.Services.Calendar;

public class CalendarService(
    HttpClient httpClient,
    IGoogleCalendarService googleCalendarService,
    ILogger<CalendarService> logger) : ICalendarService
{
    private const int MaxFetchAttempts = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

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
                if (string.Equals(feed.SourceType, "Google", StringComparison.OrdinalIgnoreCase))
                {
                    results.AddRange(await googleCalendarService.GetEventsAsync(feed, rangeStart, rangeEnd, cancellationToken));
                }
                else
                {
                    var ics = await FetchFeedAsync(feed.IcsUrl, feed.DisplayName, cancellationToken);
                    var calendar = global::Ical.Net.Calendar.Load(ics);
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
            }
            catch (HttpRequestException ex)
            {
                // Keep provider throttling from producing a full exception trace for every refresh.
                logger.LogWarning("Could not fetch calendar feed {FeedName}: {Message}", feed.DisplayName, ex.Message);
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

    private async Task<string> FetchFeedAsync(string url, string feedName, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxFetchAttempts; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxFetchAttempts)
            {
                var delay = GetTransportRetryDelay(attempt);
                logger.LogDebug(ex, "Retrying calendar feed {FeedName} after a transport error in {DelaySeconds}s", feedName, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }

                var isRetryable = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                                  (int)response.StatusCode >= 500;
                if (!isRetryable || attempt == MaxFetchAttempts)
                {
                    response.EnsureSuccessStatusCode();
                }

                var delay = GetRetryDelay(response, attempt);
                logger.LogDebug("Retrying calendar feed {FeedName} after HTTP {StatusCode} in {DelaySeconds}s", feedName, (int)response.StatusCode, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Calendar feed request did not complete.");
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfter)
        {
            return retryAfter > MaxRetryDelay ? MaxRetryDelay : retryAfter;
        }

        return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), MaxRetryDelay.TotalSeconds));
    }

    private static TimeSpan GetTransportRetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), MaxRetryDelay.TotalSeconds));
}
