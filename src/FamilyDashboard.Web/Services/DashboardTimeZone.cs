namespace FamilyDashboard.Web.Services;

/// <summary>
/// The time zone the dashboard shows times in. ICS feeds often carry UTC times, and the server's own
/// zone depends on the container's TZ setting, so event times are converted to Eastern explicitly.
/// </summary>
public static class DashboardTimeZone
{
    public static TimeZoneInfo Zone { get; } = Find();

    /// <summary>The current wall-clock time in the dashboard's zone. Use this instead of <see cref="DateTime.Now"/>.</summary>
    public static DateTime Now => TimeZoneInfo.ConvertTime(DateTime.UtcNow, Zone);

    /// <summary>Today's date in the dashboard's zone. Use this instead of <see cref="DateTime.Today"/>.</summary>
    public static DateTime Today => Now.Date;

    public static DateTimeOffset ToDashboardTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);

    // IANA ids work on Linux and, with ICU, on Windows; the Windows id covers a Windows host without it.
    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }
}
