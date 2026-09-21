namespace FamilyDashboard.Web.Data.Entities;

public class DashboardScreen
{
    public int Id { get; set; }
    public string Name { get; set; } = "Screen";
    public int DisplayOrder { get; set; }
    public int DurationSeconds { get; set; } = 60;
    public bool Enabled { get; set; } = true;

    public List<DashboardWidget> Widgets { get; set; } = [];
}