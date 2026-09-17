using FamilyDashboard.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CalendarFeed> CalendarFeeds => Set<CalendarFeed>();
    public DbSet<GoogleCalendarConnection> GoogleCalendarConnections => Set<GoogleCalendarConnection>();
    public DbSet<DashboardSettings> Settings => Set<DashboardSettings>();
    public DbSet<DashboardScreen> Screens => Set<DashboardScreen>();
    public DbSet<DashboardWidget> Widgets => Set<DashboardWidget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardSettings>().HasData(new DashboardSettings { Id = 1 });
        modelBuilder.Entity<DashboardScreen>()
            .HasMany(screen => screen.Widgets)
            .WithOne(widget => widget.Screen)
            .HasForeignKey(widget => widget.DashboardScreenId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
