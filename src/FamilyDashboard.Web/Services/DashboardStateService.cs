using FamilyDashboard.Web.Services.Calendar;
using FamilyDashboard.Web.Services.FamilyLocations;
using FamilyDashboard.Web.Services.HomeStatus;
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

    private IReadOnlyList<FamilyMemberDto> _familyMembers = [];
    private FamilyLocationStatus? _familyStatus;

    private IReadOnlyDictionary<string, HomeEntityDto> _homeEntities = new Dictionary<string, HomeEntityDto>();
    private HomeStatusStatus? _homeStatus;

    public event Action? CalendarChanged;
    public event Action? FamilyLocationsChanged;
    public event Action? HomeStatusChanged;
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

    public (IReadOnlyList<FamilyMemberDto> Members, FamilyLocationStatus? Status) GetFamilyLocations()
    {
        lock (_lock) { return (_familyMembers, _familyStatus); }
    }

    /// <summary>Records a poll outcome; <paramref name="members"/> null keeps the locations already shown.</summary>
    public void UpdateFamilyLocations(IReadOnlyList<FamilyMemberDto>? members, FamilyLocationStatus status)
    {
        lock (_lock)
        {
            if (members is not null)
            {
                _familyMembers = members;
            }
            _familyStatus = status;
        }
        FamilyLocationsChanged?.Invoke();
    }

    /// <summary>Home Assistant entities by entity id, from the last successful poll.</summary>
    public (IReadOnlyDictionary<string, HomeEntityDto> Entities, HomeStatusStatus? Status) GetHomeStatus()
    {
        lock (_lock) { return (_homeEntities, _homeStatus); }
    }

    /// <summary>Records a poll outcome; <paramref name="entities"/> null keeps the states already shown.</summary>
    public void UpdateHomeStatus(IReadOnlyList<HomeEntityDto>? entities, HomeStatusStatus status)
    {
        lock (_lock)
        {
            if (entities is not null)
            {
                _homeEntities = entities.ToDictionary(entity => entity.EntityId, StringComparer.OrdinalIgnoreCase);
            }
            _homeStatus = status;
        }
        HomeStatusChanged?.Invoke();
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
