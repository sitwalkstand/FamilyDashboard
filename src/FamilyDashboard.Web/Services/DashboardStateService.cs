using FamilyDashboard.Web.Services.Calendar;
using FamilyDashboard.Web.Services.Weather;
using FamilyDashboard.Web.Data.Entities;

namespace FamilyDashboard.Web.Services;

/// <summary>
/// Holds the latest fetched data in memory and raises events when it changes.
/// Background workers write to this; widget components subscribe to be notified
/// and call StateHasChanged() - Blazor Server's existing circuit does the rest,
/// so no extra SignalR hub is needed for "live" updates.
/// </summary>
public class DashboardStateService
{
    private readonly object _lock = new();

    public IReadOnlyList<CalendarEventDto> Events { get; private set; } = [];
    public IReadOnlyList<CalendarLegendDto> Calendars { get; private set; } = [];
    private readonly Dictionary<WeatherLocationKey, WeatherDto> _weather = [];
    public IReadOnlyList<string> PhotoPaths { get; private set; } = [];

    public event Action? CalendarChanged;
    public event Action? WeatherChanged;
    public event Action? PhotosChanged;

    /// <summary>Raised when screens or widgets are changed from the admin page.</summary>
    public event Action? ScreensChanged;

    public void NotifyScreensChanged() => ScreensChanged?.Invoke();

    public void UpdateEvents(List<CalendarEventDto> events, IEnumerable<CalendarFeed>? feeds = null)
    {
        lock (_lock)
        {
            Events = events;
            if (feeds is not null)
            {
                Calendars = feeds
                    .Where(feed => feed.Enabled)
                    .Select(feed => new CalendarLegendDto(feed.DisplayName, feed.Color, feed.Icon))
                    .ToList();
            }
        }
        CalendarChanged?.Invoke();
    }

    public WeatherDto? GetWeather(WeatherLocationKey location)
    {
        lock (_lock) { return _weather.GetValueOrDefault(location); }
    }

    public void UpdateWeather(WeatherLocationKey location, WeatherDto weather)
    {
        lock (_lock) { _weather[location] = weather; }
        WeatherChanged?.Invoke();
    }

    public void UpdatePhotos(List<string> paths)
    {
        lock (_lock) { PhotoPaths = paths; }
        PhotosChanged?.Invoke();
    }
}
