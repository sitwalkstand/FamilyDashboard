using FamilyDashboard.Web.Services.Calendar;
using FamilyDashboard.Web.Services.Weather;

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
    public WeatherDto? Weather { get; private set; }
    public IReadOnlyList<string> PhotoPaths { get; private set; } = [];

    public event Action? CalendarChanged;
    public event Action? WeatherChanged;
    public event Action? PhotosChanged;

    public void UpdateEvents(List<CalendarEventDto> events)
    {
        lock (_lock) { Events = events; }
        CalendarChanged?.Invoke();
    }

    public void UpdateWeather(WeatherDto? weather)
    {
        lock (_lock) { Weather = weather; }
        WeatherChanged?.Invoke();
    }

    public void UpdatePhotos(List<string> paths)
    {
        lock (_lock) { PhotoPaths = paths; }
        PhotosChanged?.Invoke();
    }
}
