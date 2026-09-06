using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.Photos;

namespace FamilyDashboard.Web.Workers;

public class PhotoScanWorker(
    IServiceScopeFactory scopeFactory,
    DashboardStateService state,
    IConfiguration configuration,
    ILogger<PhotoScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("Photos:RescanMinutes", 60);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var photoService = scope.ServiceProvider.GetRequiredService<IPhotoService>();

                var photos = await photoService.ScanAsync(stoppingToken);
                state.UpdatePhotos(photos);

                logger.LogInformation("Found {Count} photos", photos.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Photo scan failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
