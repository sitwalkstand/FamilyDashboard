namespace FamilyDashboard.Web.Data.Entities;

/// <summary>
/// Per-widget options for a Countdown widget, stored as one JSON column on Widgets. EF reads a
/// property missing from the stored JSON as zero/false, not the default below, so a new option
/// either defaults to that or needs an upgrade in Program.cs that writes it into existing rows.
/// </summary>
public class CountdownWidgetSettings
{
    public string Title { get; set; } = "Countdown";
    /// <summary>When the countdown reaches zero, as a wall-clock time in the dashboard's time zone (Eastern).</summary>
    public DateTime EndsAt { get; set; } = DateTime.Today.AddDays(7);
}
