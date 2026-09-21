namespace FamilyDashboard.Web.Data.Entities;

public class DashboardWidget
{
    public int Id { get; set; }
    public int DashboardScreenId { get; set; }
    public string WidgetType { get; set; } = "Clock";
    public int DisplayOrder { get; set; }
    public bool Enabled { get; set; } = true;
    public int PositionX { get; set; } = 1;
    public int PositionY { get; set; } = 1;
    public int Width { get; set; } = 6;
    public int Height { get; set; } = 4;
    public string CalendarNames { get; set; } = "";
    public string PhotoPath { get; set; } = "";

    public DashboardScreen? Screen { get; set; }
}
