using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Data.Entities;
using FamilyDashboard.Web.Services;
using FamilyDashboard.Web.Services.Photos;
using Microsoft.EntityFrameworkCore;

namespace FamilyDashboard.Web.Workers;

/// <summary>
/// Rescans the local photo folder, and keeps each Photos widget that points at an iCloud Shared
/// Album in sync with its disk cache. Widgets show cached photos straight away at startup; a widget
/// whose album link changes is synced immediately rather than on the next interval.
/// </summary>
public class PhotoScanWorker(
    IServiceScopeFactory scopeFactory,
    DashboardStateService state,
    IConfiguration configuration,
    ILogger<PhotoScanWorker> logger) : BackgroundService
{
    private const int MinAlbumSyncMinutes = 5;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<int, AlbumSyncState> _albums = [];
    private readonly SemaphoreSlim _wake = new(0);
    private DateTime _lastFolderScanUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var folderInterval = TimeSpan.FromMinutes(configuration.GetValue("Photos:RescanMinutes", 60));
        var albumInterval = TimeSpan.FromMinutes(Math.Max(MinAlbumSyncMinutes, configuration.GetValue("Photos:AlbumSyncMinutes", 30)));

        // Widget edits in admin wake the worker, so a new album link is synced right away.
        state.ScreensChanged += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow - _lastFolderScanUtc >= folderInterval)
                {
                    await ScanFolderAsync(stoppingToken);
                }

                await SyncAlbumsAsync(albumInterval, stoppingToken);
                await _wake.WaitAsync(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            state.ScreensChanged -= Wake;
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private async Task ScanFolderAsync(CancellationToken stoppingToken)
    {
        _lastFolderScanUtc = DateTime.UtcNow;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var photoService = scope.ServiceProvider.GetRequiredService<IPhotoService>();

            var photos = await photoService.ScanAsync(stoppingToken);
            state.UpdatePhotos(photos);

            logger.LogInformation("Found {Count} photos", photos.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Photo scan failed");
        }
    }

    private async Task SyncAlbumsAsync(TimeSpan albumInterval, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var cache = scope.ServiceProvider.GetRequiredService<SharedAlbumCache>();

            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            var photoWidgets = await db.Widgets
                .AsNoTracking()
                .Include(widget => widget.Screen)
                .Where(widget => widget.WidgetType == "Photos")
                .ToListAsync(stoppingToken);

            var albumWidgets = new List<(DashboardWidget Widget, AlbumKey Key)>();
            foreach (var widget in photoWidgets)
            {
                if (SharedAlbumUrl.TryParse(widget.PhotoSettings.AlbumUrl, out var link))
                {
                    albumWidgets.Add((widget, new AlbumKey(link, widget.PhotoSettings.HiRes)));
                }
            }
            var albumWidgetIds = albumWidgets.Select(item => item.Widget.Id).ToHashSet();

            // Widgets that were deleted or switched back to the local folder.
            foreach (var widgetId in _albums.Keys.Where(id => !albumWidgetIds.Contains(id)).ToList())
            {
                _albums.Remove(widgetId);
                state.RemoveAlbum(widgetId);
            }
            cache.RemoveUnused(albumWidgetIds);

            foreach (var (widget, key) in albumWidgets)
            {
                await SyncAlbumAsync(cache, widget, key, albumInterval, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Shared album sync failed");
        }
    }

    private async Task SyncAlbumAsync(
        SharedAlbumCache cache,
        DashboardWidget widget,
        AlbumKey key,
        TimeSpan albumInterval,
        CancellationToken stoppingToken)
    {
        var known = _albums.GetValueOrDefault(widget.Id);
        if (known?.Key.Token != key.Token)
        {
            // New to this run, or a different album: show whatever is cached for it before going online.
            var cached = cache.LoadCached(widget.Id, key.Token, out var cachedName);
            state.UpdateAlbum(widget.Id, cached ?? [], new AlbumSyncStatus(cachedName, cached?.Count ?? 0, null, null));
            known = new AlbumSyncState(key, DateTime.MinValue);
            _albums[widget.Id] = known;
        }

        var due = known.Key != key || DateTime.UtcNow - known.LastAttemptUtc >= albumInterval;
        if (!due || !widget.Enabled || widget.Screen?.Enabled != true)
        {
            return;
        }

        // Record the attempt even on failure, so an unreachable iCloud is retried on the interval
        // rather than every minute.
        _albums[widget.Id] = new AlbumSyncState(key, DateTime.UtcNow);
        var token = SharedAlbumUrl.Mask(key.Token);
        try
        {
            var result = await cache.SyncAsync(widget.Id, key.Link, key.HiRes, stoppingToken);
            state.UpdateAlbum(widget.Id, result.Photos, new AlbumSyncStatus(result.AlbumName, result.Photos.Count, DateTimeOffset.UtcNow,
                result.Failed > 0 ? $"{result.Failed} photo(s) could not be downloaded; they will be retried." : null));

            if (result.Unchanged)
            {
                logger.LogInformation("Shared album '{Album}' ({Token}) for widget {WidgetId} is unchanged: {Count} photos",
                    result.AlbumName, token, widget.Id, result.Photos.Count);
            }
            else
            {
                logger.LogInformation("Synced shared album '{Album}' ({Token}) for widget {WidgetId}: {Count} photos, {Added} added, {Removed} removed, {Failed} failed",
                    result.AlbumName, token, widget.Id, result.Photos.Count, result.Added, result.Removed, result.Failed);
            }
        }
        catch (Exception ex) when (ex is SharedAlbumException or HttpRequestException or IOException
                                       || ex is TaskCanceledException && !stoppingToken.IsCancellationRequested)
        {
            // The cache is untouched on failure, so the widget keeps showing the photos it has.
            var shown = state.GetAlbumPhotos(widget.Id).Count;
            var previous = state.GetAlbumStatus(widget.Id);
            state.UpdateAlbum(widget.Id, null, new AlbumSyncStatus(previous?.AlbumName, shown, previous?.LastSyncedUtc, ex.Message));
            logger.LogWarning("Could not sync shared album {Token} for widget {WidgetId}: {Message}. Keeping {Count} cached photos.",
                token, widget.Id, ex.Message, shown);
        }
    }

    private sealed record AlbumKey(SharedAlbumLink Link, bool HiRes)
    {
        public string Token => Link.Token;
    }

    private sealed record AlbumSyncState(AlbumKey Key, DateTime LastAttemptUtc);
}
