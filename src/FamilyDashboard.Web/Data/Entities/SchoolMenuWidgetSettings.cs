namespace FamilyDashboard.Web.Data.Entities;

/// <summary>
/// Per-widget options for a School Menu widget, stored as one JSON column on Widgets. EF reads a
/// property missing from the stored JSON as zero/false, not the default below, so a new option
/// either defaults to that or needs an upgrade in Program.cs that writes it into existing rows.
/// </summary>
public class SchoolMenuWidgetSettings
{
    /// <summary>Frederick County Public Schools (VA) on School Nutrition and Fitness.</summary>
    public const string DefaultOrganizationId = "1678132657639";

    public static readonly int[] DaysToShowOptions = [1, 2, 3, 4, 5];

    /// <summary>The district's id on School Nutrition and Fitness (the sid in its menu site link).</summary>
    public string OrganizationId { get; set; } = DefaultOrganizationId;
    /// <summary>The chosen menu, e.g. "Elementary/Middle Lunch Menu 2026-2027". Empty until one is picked.</summary>
    public string MenuTypeId { get; set; } = "";
    public string MenuName { get; set; } = "";
    public int DaysToShow { get; set; } = 3;
    /// <summary>False shows only the entrée choices, not sides, fruit and milk.</summary>
    public bool ShowSides { get; set; } = true;
}
