namespace FamilyDashboard.Web.Data.Entities;

/// <summary>How a photo is placed in its widget.</summary>
public enum PhotoDisplayStyle
{
    /// <summary>Fill the widget, cropping the edges (object-fit: cover).</summary>
    Crop,
    /// <summary>Show the whole photo, letterboxed (object-fit: contain).</summary>
    Fit,
    /// <summary>Show the whole photo over a blurred, enlarged copy of itself.</summary>
    FitAndBlur,
    /// <summary>Cropped into a circle.</summary>
    Circle
}

/// <summary>
/// Per-widget options for a Photos widget, stored as one JSON column on Widgets. EF reads a
/// property missing from the stored JSON as zero/false, not the default below, so a new option
/// either defaults to that or needs an upgrade in Program.cs that writes it into existing rows.
/// </summary>
public class PhotoWidgetSettings
{
    public static readonly int[] ChangeIntervalMinuteOptions = [1, 5, 15, 30, 60];

    /// <summary>iCloud Shared Album public website URL. Empty uses the local photo folder.</summary>
    public string AlbumUrl { get; set; } = "";
    public int ChangeIntervalSeconds { get; set; } = 300;
    /// <summary>0-100, applied as a CSS brightness filter.</summary>
    public int Brightness { get; set; } = 100;
    public PhotoDisplayStyle Style { get; set; } = PhotoDisplayStyle.Crop;
    public bool ShowMeta { get; set; }
    public bool Transitions { get; set; } = true;
    public bool Vignette { get; set; }
    public bool HiRes { get; set; }
    public bool ClickToRotate { get; set; }
}
