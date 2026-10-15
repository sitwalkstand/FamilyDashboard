namespace FamilyDashboard.Web.Data.Entities;

/// <summary>
/// Per-widget options for a Family Locations widget, stored as one JSON column on Widgets. EF reads
/// a property missing from the stored JSON as zero/false, not the default below, so a new option
/// either defaults to that or needs an upgrade in Program.cs that writes it into existing rows.
/// </summary>
public class FamilyLocationsWidgetSettings
{
    /// <summary>Home Assistant device tracker entity ids to show, separated by commas. Empty shows none.</summary>
    public string EntityIds { get; set; } = "";
    public string Title { get; set; } = "Family";
    public bool ShowBattery { get; set; } = true;

    public IReadOnlyList<string> EntityIdList =>
        EntityIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
