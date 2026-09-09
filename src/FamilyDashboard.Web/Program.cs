using FamilyDashboard.Web.Components;
using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Data.Entities;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.Calendar;
using FamilyDashboard.Web.Services.Photos;
using FamilyDashboard.Web.Services.Weather;
using FamilyDashboard.Web.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// ---- Blazor Server (interactive server-side render mode) ----
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---- Config-backed storage ----
var dataDir = builder.Configuration["DataDirectory"];
if (string.IsNullOrWhiteSpace(dataDir))
{
    dataDir = Path.Combine(AppContext.BaseDirectory, "App_Data");
}
else if (!Path.IsPathRooted(dataDir))
{
    dataDir = Path.Combine(AppContext.BaseDirectory, dataDir);
}
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "dashboard.db");
builder.Configuration.AddJsonFile(
    Path.Combine(dataDir, "google-oauth.json"),
    optional: true,
    reloadOnChange: false);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "Keys")));

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ---- Shared live-update notifier ----
// Blazor Server already keeps a persistent circuit open per browser tab, so
// widgets get "live push" simply by subscribing to this singleton's events
// and calling StateHasChanged() - no extra SignalR hub required.
builder.Services.AddSingleton<DashboardStateService>();

// ---- Domain services ----
//builder.Services.AddHttpClient<ICalendarService, CalendarService>();
builder.Services.AddHttpClient<ICalendarService, CalendarService>(client =>
{
    // Some calendar providers (Outlook.com in particular) reject requests that
    // have no User-Agent header at all with a 400/401/403, since HttpClient sends
    // none by default. A plain, honest UA string avoids that without pretending
    // to be a browser.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("FamilyDashboard/1.0 (self-hosted family calendar dashboard)");
    client.DefaultRequestVersion = HttpVersion.Version11;
    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
});
builder.Services.AddScoped<IGoogleCalendarService, GoogleCalendarService>();
builder.Services.AddHttpClient<IWeatherService, OpenMeteoWeatherService>();
builder.Services.AddSingleton<IPhotoService, LocalFolderPhotoService>();

// ---- Background refresh workers ----
builder.Services.AddHostedService<CalendarRefreshWorker>();
builder.Services.AddHostedService<WeatherRefreshWorker>();
builder.Services.AddHostedService<PhotoScanWorker>();

var app = builder.Build();

// Ensure the SQLite schema exists (fine for a small single-instance home app;
// switch to EF migrations if you outgrow this).
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS Screens (
            Id INTEGER NOT NULL CONSTRAINT PK_Screens PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            DisplayOrder INTEGER NOT NULL,
            DurationSeconds INTEGER NOT NULL,
            Enabled INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS Widgets (
            Id INTEGER NOT NULL CONSTRAINT PK_Widgets PRIMARY KEY AUTOINCREMENT,
            DashboardScreenId INTEGER NOT NULL,
            WidgetType TEXT NOT NULL,
            DisplayOrder INTEGER NOT NULL,
            Enabled INTEGER NOT NULL,
            PositionX INTEGER NOT NULL DEFAULT 1,
            PositionY INTEGER NOT NULL DEFAULT 1,
            Width INTEGER NOT NULL DEFAULT 6,
            Height INTEGER NOT NULL DEFAULT 4,
            CONSTRAINT FK_Widgets_Screens_DashboardScreenId FOREIGN KEY (DashboardScreenId) REFERENCES Screens (Id) ON DELETE CASCADE
        );
        """);

    var feedColumns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('CalendarFeeds')").ToList();
    foreach (var column in new[] { "SourceType", "ExternalId" })
    {
        if (!feedColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            var defaultValue = column == "SourceType" ? "Ics" : "";
            db.Database.ExecuteSqlRaw($"ALTER TABLE CalendarFeeds ADD COLUMN {column} TEXT NOT NULL DEFAULT '{defaultValue}'");
        }
    }

    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS GoogleCalendarConnections (
            Id INTEGER NOT NULL CONSTRAINT PK_GoogleCalendarConnections PRIMARY KEY AUTOINCREMENT,
            Email TEXT NOT NULL,
            ProtectedRefreshToken TEXT NOT NULL,
            ConnectedAtUtc TEXT NOT NULL
        );
        """);

    var widgetColumns = db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Widgets')").ToList();
    foreach (var column in new[] { "PositionX", "PositionY", "Width", "Height", "CalendarNames", "PhotoPath" })
    {
        if (!widgetColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            switch (column)
            {
                case "PositionX":
                    db.Database.ExecuteSqlRaw("ALTER TABLE Widgets ADD COLUMN PositionX INTEGER NOT NULL DEFAULT 1");
                    break;
                case "PositionY":
                    db.Database.ExecuteSqlRaw("ALTER TABLE Widgets ADD COLUMN PositionY INTEGER NOT NULL DEFAULT 1");
                    break;
                case "Width":
                    db.Database.ExecuteSqlRaw("ALTER TABLE Widgets ADD COLUMN Width INTEGER NOT NULL DEFAULT 6");
                    break;
                case "Height":
                    db.Database.ExecuteSqlRaw("ALTER TABLE Widgets ADD COLUMN Height INTEGER NOT NULL DEFAULT 4");
                    break;
                case "CalendarNames":
                    db.Database.ExecuteSqlRaw("ALTER TABLE Widgets ADD COLUMN CalendarNames TEXT NOT NULL DEFAULT ''");
                    break;
                case "PhotoPath":
                    db.Database.ExecuteSqlRaw("ALTER TABLE Widgets ADD COLUMN PhotoPath TEXT NOT NULL DEFAULT ''");
                    break;
            }
        }
    }

    var existingScreens = db.Screens.Include(screen => screen.Widgets).ToList();
    foreach (var screen in existingScreens)
    {
        var needsLegacyLayout = screen.Widgets.Count > 0 &&
            (screen.Widgets.All(widget => widget.PositionX == 1 && widget.PositionY == 1 && widget.Width == 6 && widget.Height == 4) ||
             (screen.DisplayOrder < 2 && screen.Widgets.Any(widget => widget.WidgetType == "Clock" && widget.PositionX == 9)));
        if (!needsLegacyLayout)
        {
            continue;
        }

        foreach (var widget in screen.Widgets)
        {
            (widget.PositionX, widget.PositionY, widget.Width, widget.Height) = (screen.DisplayOrder % 3, widget.WidgetType) switch
            {
                (0, "Clock") => (1, 1, 5, 4),
                (0, "Weather") => (1, 9, 12, 3),
                (0, "Calendar") => (8, 1, 5, 8),
                (1, "Clock") => (1, 1, 5, 4),
                (1, "Weather") => (1, 9, 12, 3),
                (1, "Calendar") => (1, 5, 7, 4),
                (2, "Photos") => (1, 1, 7, 12),
                (2, "Clock") => (9, 1, 4, 4),
                (2, "Weather") => (9, 9, 4, 3),
                (2, "Calendar") => (9, 5, 4, 4),
                _ => (1, 1, 6, 4)
            };
        }
    }
    db.SaveChanges();

        if (!db.Screens.Any())
        {
            db.Screens.AddRange(
                new DashboardScreen
                {
                    Name = "Calendar overview",
                    DisplayOrder = 0,
                    Widgets = [
                        new DashboardWidget { WidgetType = "Clock", DisplayOrder = 0, PositionX = 1, PositionY = 1, Width = 5, Height = 4 },
                        new DashboardWidget { WidgetType = "Weather", DisplayOrder = 1, PositionX = 1, PositionY = 9, Width = 12, Height = 3 },
                        new DashboardWidget { WidgetType = "Calendar", DisplayOrder = 2, PositionX = 8, PositionY = 1, Width = 5, Height = 8 }
                    ]
                },
                new DashboardScreen
                {
                    Name = "Daily details",
                    DisplayOrder = 1,
                    Widgets = [
                        new DashboardWidget { WidgetType = "Clock", DisplayOrder = 0, PositionX = 1, PositionY = 1, Width = 5, Height = 4 },
                        new DashboardWidget { WidgetType = "Weather", DisplayOrder = 1, PositionX = 1, PositionY = 9, Width = 12, Height = 3 },
                        new DashboardWidget { WidgetType = "Calendar", DisplayOrder = 2, PositionX = 1, PositionY = 5, Width = 7, Height = 4 }
                    ]
                },
                new DashboardScreen
                {
                    Name = "Family photos",
                    DisplayOrder = 2,
                    Widgets = [
                        new DashboardWidget { WidgetType = "Photos", DisplayOrder = 0, PositionX = 1, PositionY = 1, Width = 7, Height = 12 },
                        new DashboardWidget { WidgetType = "Clock", DisplayOrder = 1, PositionX = 9, PositionY = 1, Width = 4, Height = 4 },
                        new DashboardWidget { WidgetType = "Weather", DisplayOrder = 2, PositionX = 9, PositionY = 9, Width = 4, Height = 3 },
                        new DashboardWidget { WidgetType = "Calendar", DisplayOrder = 3, PositionX = 9, PositionY = 5, Width = 4, Height = 4 }
                    ]
                });
            db.SaveChanges();
        }
}

app.MapGet("/auth/google/start", (HttpContext context, IGoogleCalendarService googleCalendar) =>
{
    var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}/auth/google/callback";
    return Results.Redirect(googleCalendar.CreateAuthorizationUrl(redirectUri));
});

app.MapGet("/auth/google/callback", async (
    HttpContext context,
    IGoogleCalendarService googleCalendar,
    string? code,
    string? state,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.Redirect("/admin?googleError=Authorization+was+cancelled");
    }

    try
    {
        googleCalendar.ValidateState(state);
        var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}/auth/google/callback";
        await googleCalendar.CompleteAuthorizationAsync(code, redirectUri, cancellationToken);
        return Results.Redirect("/admin?google=connected");
    }
    catch (Exception exception) when (exception is InvalidOperationException or Google.GoogleApiException)
    {
        return Results.Redirect($"/admin?googleError={Uri.EscapeDataString(exception.Message)}");
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

// Serve the family photo folder as static files under /photos.
var photoDir = builder.Configuration["PhotoDirectory"];
if (string.IsNullOrWhiteSpace(photoDir))
{
    photoDir = Path.Combine(AppContext.BaseDirectory, "App_Data", "Photos");
}
else if (!Path.IsPathRooted(photoDir))
{
    photoDir = Path.Combine(AppContext.BaseDirectory, photoDir);
}
Directory.CreateDirectory(photoDir);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(photoDir),
    RequestPath = "/photos"
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
