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

    public double WeatherLatitude { get; set; } = 38.9894;
    public double WeatherLongitude { get; set; } = -77.4794;
    public string WeatherTemperatureUnit { get; set; } = "fahrenheit";
    public int WeatherRefreshMinutes { get; set; } = 30;
}
