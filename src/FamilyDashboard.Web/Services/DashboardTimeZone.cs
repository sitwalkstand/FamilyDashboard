namespace FamilyDashboard.Web.Services;

/// <summary>
/// The time zone the dashboard shows times in. ICS feeds often carry UTC times, and the server's own
/// zone depends on the container's TZ setting, so event times are converted to Eastern explicitly.
/// </summary>
public static class DashboardTimeZone
{
    public static TimeZoneInfo Zone { get; } = Find();

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
