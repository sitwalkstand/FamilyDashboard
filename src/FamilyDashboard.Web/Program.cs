using FamilyDashboard.Web.Components;
using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.Calendar;
using FamilyDashboard.Web.Services.Photos;
using FamilyDashboard.Web.Services.Weather;
using FamilyDashboard.Web.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Blazor Server (interactive server-side render mode) ----
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---- Config-backed storage ----
var dataDir = builder.Configuration["DataDirectory"] ?? "/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "dashboard.db");

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
});
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
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

// Serve the family photo folder as static files under /photos.
var photoDir = builder.Configuration["PhotoDirectory"] ?? "/photos";
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
