using FamilyDashboard.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CalendarFeed> CalendarFeeds => Set<CalendarFeed>();
    public DbSet<DashboardSettings> Settings => Set<DashboardSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardSettings>().HasData(new DashboardSettings { Id = 1 });
    }
}
