namespace FamilyDashboard.Web.Data.Entities;

public class GoogleCalendarConnection
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string ProtectedRefreshToken { get; set; } = string.Empty;
    public DateTime ConnectedAtUtc { get; set; } = DateTime.UtcNow;
}
