namespace FamilyDashboard.Web.Data.Entities;

/// <summary>
/// A subscribed ICS calendar feed URL (Google/Outlook/iCloud "secret address" link).
/// Added and managed from the /admin page.
/// </summary>
public class CalendarFeed
{
    public int Id { get; set; }

    /// <summary>Friendly name shown in the UI, e.g. "Mom's calendar".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The ICS subscription URL for this calendar.</summary>
    public string IcsUrl { get; set; } = string.Empty;

    /// <summary>Hex color used to tag events from this calendar on the dashboard.</summary>
    public string Color { get; set; } = "#3B8BD4";

    public bool Enabled { get; set; } = true;
}
