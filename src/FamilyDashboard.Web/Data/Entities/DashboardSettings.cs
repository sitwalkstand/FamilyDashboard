namespace FamilyDashboard.Web.Data.Entities;

/// <summary>
/// Single-row table of user-editable dashboard preferences, configured via /admin.
/// </summary>
public class DashboardSettings
{
    public int Id { get; set; } = 1;

    public string DashboardTitle { get; set; } = "Family Dashboard";

    public bool ShowWeather { get; set; } = true;
    public bool ShowCalendar { get; set; } = true;
    public bool ShowPhotos { get; set; } = true;
    public bool ShowClock { get; set; } = true;

    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    // Degrees clockwise to rotate the dashboard (0, 90, 180 or 270), for a monitor mounted on its
    // side while the kiosk's OS still outputs a landscape picture.
    public int DisplayRotation { get; set; }
}
