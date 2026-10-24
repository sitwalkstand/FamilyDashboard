namespace FamilyDashboard.Web.Data.Entities;

/// <summary>
/// Per-widget options for a Home Status widget, stored as one JSON column on Widgets. EF reads a
/// property missing from the stored JSON as zero/false, not the default below, so a new option
/// either defaults to that or needs an upgrade in Program.cs that writes it into existing rows.
/// </summary>
public class HomeStatusWidgetSettings
{
    public const string DefaultAttentionEntityId = "sensor.needs_attention";

    public string Title { get; set; } = "Home";
    /// <summary>Home Assistant entity ids shown as tiles, separated by commas, in the order shown.</summary>
    public string EntityIds { get; set; } = "";
    public bool ShowAttention { get; set; } = true;
    /// <summary>The template sensor whose <c>items</c> attribute lists what needs attention.</summary>
    public string AttentionEntityId { get; set; } = DefaultAttentionEntityId;

    public IReadOnlyList<string> EntityIdList =>
        EntityIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
