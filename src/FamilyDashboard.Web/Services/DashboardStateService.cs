using FamilyDashboard.Web.Services.Calendar;
using FamilyDashboard.Web.Services.Photos;
using FamilyDashboard.Web.Services.SchoolMenus;
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
    private readonly Dictionary<int, IReadOnlyList<AlbumPhoto>> _albumPhotos = [];
    private readonly Dictionary<int, AlbumSyncStatus> _albumStatus = [];
    private readonly Dictionary<string, SchoolMenuDto> _schoolMenus = [];

    public event Action? CalendarChanged;
    public event Action? WeatherChanged;
    public event Action? PhotosChanged;
    public event Action? SchoolMenusChanged;

    /// <summary>Raised with the widget id when a shared album widget's photos or sync status change.</summary>
    public event Action<int>? AlbumChanged;

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

    public SchoolMenuDto? GetSchoolMenu(string menuTypeId)
    {
        lock (_lock) { return _schoolMenus.GetValueOrDefault(menuTypeId); }
    }

    public void UpdateSchoolMenu(SchoolMenuDto menu)
    {
        lock (_lock) { _schoolMenus[menu.MenuTypeId] = menu; }
        SchoolMenusChanged?.Invoke();
    }

    public void UpdatePhotos(List<string> paths)
    {
        lock (_lock) { PhotoPaths = paths; }
        PhotosChanged?.Invoke();
    }

    public IReadOnlyList<AlbumPhoto> GetAlbumPhotos(int widgetId)
    {
        lock (_lock) { return _albumPhotos.GetValueOrDefault(widgetId) ?? []; }
    }

    public AlbumSyncStatus? GetAlbumStatus(int widgetId)
    {
        lock (_lock) { return _albumStatus.GetValueOrDefault(widgetId); }
    }

    /// <summary>Records a sync outcome; <paramref name="photos"/> null keeps the photos already shown.</summary>
    public void UpdateAlbum(int widgetId, IReadOnlyList<AlbumPhoto>? photos, AlbumSyncStatus status)
    {
        lock (_lock)
        {
            if (photos is not null)
            {
                _albumPhotos[widgetId] = photos;
            }
            _albumStatus[widgetId] = status;
        }
        AlbumChanged?.Invoke(widgetId);
    }

    public void RemoveAlbum(int widgetId)
    {
        bool removed;
        lock (_lock)
        {
            removed = _albumPhotos.Remove(widgetId) | _albumStatus.Remove(widgetId);
        }
        if (removed)
        {
            AlbumChanged?.Invoke(widgetId);
        }
    }
}
