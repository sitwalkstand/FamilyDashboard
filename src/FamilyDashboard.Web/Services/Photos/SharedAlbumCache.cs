using System.Globalization;
using System.Text.Json;

namespace FamilyDashboard.Web.Services.Photos;

/// <summary>A cached album photo as the dashboard shows it.</summary>
public record AlbumPhoto(string Url, string? Caption, string? Contributor, DateTimeOffset? DateCreated);

/// <summary>Where shared album photos are cached: one folder per widget under this root.</summary>
public record SharedAlbumCacheLocation(string RootPath)
{
    public const string RequestPath = "/album-cache";
}

/// <summary>The latest sync outcome for a shared album widget, shown in the admin editor.</summary>
public record AlbumSyncStatus(string? AlbumName, int PhotoCount, DateTimeOffset? LastSyncedUtc, string? LastError);

public record AlbumSyncResult(string? AlbumName, IReadOnlyList<AlbumPhoto> Photos, int Added, int Removed, int Failed, bool Unchanged);

/// <summary>
/// Mirrors an iCloud Shared Album into &lt;DataDirectory&gt;/AlbumCache/&lt;widgetId&gt;, so the dashboard
/// only ever shows files from disk and keeps working while iCloud is unreachable. A failed sync
/// throws before anything is deleted, so the existing cache is never lost to an outage.
/// </summary>
public class SharedAlbumCache(
    SharedAlbumCacheLocation location,
    IAppleSharedAlbumClient client,
    ILogger<SharedAlbumCache> logger)
{
    private const string ManifestFileName = "album.json";
    // The "medium" pick: the smallest derivative that is still sharp on a kiosk screen. Shared
    // albums usually hold only a ~350px thumbnail and a ~2000px+ image, which then means the latter.
    private const int MediumMinLongEdge = 1280;
    private static readonly string[] DisplayableExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif"];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    /// <summary>Cached photos for a widget, if the cache holds this album. Reads disk only.</summary>
    public IReadOnlyList<AlbumPhoto>? LoadCached(int widgetId, string token, out string? albumName)
    {
        albumName = null;
        var manifest = ReadManifest(widgetId);
        if (manifest is null || manifest.Token != token)
        {
            return null;
        }

        albumName = manifest.AlbumName;
        return ToAlbumPhotos(widgetId, manifest.Photos);
    }

    public async Task<AlbumSyncResult> SyncAsync(int widgetId, string token, bool hiRes, CancellationToken cancellationToken)
    {
        var stream = await client.GetStreamAsync(token, cancellationToken);
        var directory = WidgetDirectory(widgetId);
        Directory.CreateDirectory(directory);

        var manifest = ReadManifest(widgetId);
        var previous = manifest?.Token == token
            ? manifest.Photos.Where(photo => File.Exists(Path.Combine(directory, photo.FileName))).ToDictionary(photo => photo.Checksum, StringComparer.OrdinalIgnoreCase)
            : [];

        var ctagMatches = manifest is not null && manifest.Token == token && stream.Ctag is not null && manifest.Ctag == stream.Ctag;
        if (ctagMatches && manifest!.HiRes == hiRes && previous.Count == manifest.Photos.Count)
        {
            return new AlbumSyncResult(stream.StreamName, ToAlbumPhotos(widgetId, manifest.Photos), 0, 0, 0, Unchanged: true);
        }

        // iCloud sometimes answers with an empty photo list for an album that still has photos (seen
        // on a live album after a few requests, with its ctag unchanged). Never let that empty the cache.
        if (stream.Photos.Count == 0)
        {
            throw new SharedAlbumException(ctagMatches || previous.Count > 0
                ? "iCloud returned an empty photo list; keeping the cached photos and trying again later."
                : "iCloud returned no photos for this album yet. It will be tried again later.");
        }

        var wanted = stream.Photos
            .Select(photo => (Photo: photo, Derivative: PickDerivative(photo.Derivatives, hiRes)))
            .ToList();
        var missing = wanted.Where(item => !previous.ContainsKey(item.Derivative.Checksum)).ToList();

        var downloaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failed = 0;
        if (missing.Count > 0)
        {
            var urls = await client.GetAssetUrlsAsync(token, missing.Select(item => item.Photo.PhotoGuid).ToList(), cancellationToken);
            foreach (var (photo, derivative) in missing)
            {
                var fileName = await TryDownloadAsync(directory, photo, derivative, urls, cancellationToken);
                if (fileName is null)
                {
                    failed++;
                }
                else
                {
                    downloaded[derivative.Checksum] = fileName;
                }
            }
        }

        var photos = new List<CachedAlbumPhoto>();
        foreach (var (photo, derivative) in wanted)
        {
            var fileName = previous.TryGetValue(derivative.Checksum, out var cached) ? cached.FileName : downloaded.GetValueOrDefault(derivative.Checksum);
            if (fileName is not null)
            {
                photos.Add(new CachedAlbumPhoto(fileName, photo.PhotoGuid, derivative.Checksum, photo.Caption, photo.Contributor, photo.DateCreated));
            }
        }

        // The album was read successfully, so anything no longer in it can go.
        var keep = photos.Select(photo => photo.FileName).Append(ManifestFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!keep.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
                removed++;
            }
        }

        WriteManifest(widgetId, new AlbumCacheManifest
        {
            Token = token,
            // Leave the ctag out after a partial sync so the next pass retries the failed photos.
            Ctag = failed == 0 ? stream.Ctag : null,
            HiRes = hiRes,
            AlbumName = stream.StreamName,
            SyncedUtc = DateTimeOffset.UtcNow,
            Photos = photos
        });

        return new AlbumSyncResult(stream.StreamName, ToAlbumPhotos(widgetId, photos), downloaded.Count, removed, failed, Unchanged: false);
    }

    /// <summary>Deletes cache folders of widgets that no longer use a shared album.</summary>
    public void RemoveUnused(IReadOnlySet<int> albumWidgetIds)
    {
        if (!Directory.Exists(location.RootPath))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(location.RootPath))
        {
            if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var widgetId)
                && !albumWidgetIds.Contains(widgetId))
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("Removed the shared album cache for widget {WidgetId}", widgetId);
            }
        }
    }

    internal static SharedAlbumDerivative PickDerivative(IReadOnlyList<SharedAlbumDerivative> derivatives, bool hiRes)
    {
        var bySize = derivatives.OrderBy(derivative => derivative.LongEdge).ToList();
        return hiRes
            ? bySize[^1]
            : bySize.FirstOrDefault(derivative => derivative.LongEdge >= MediumMinLongEdge) ?? bySize[^1];
    }

    private async Task<string?> TryDownloadAsync(
        string directory,
        SharedAlbumPhoto photo,
        SharedAlbumDerivative derivative,
        IReadOnlyDictionary<string, Uri> urls,
        CancellationToken cancellationToken)
    {
        if (!urls.TryGetValue(derivative.Checksum, out var url))
        {
            logger.LogWarning("iCloud returned no download URL for photo {PhotoGuid}", photo.PhotoGuid);
            return null;
        }

        var extension = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        if (!DisplayableExtensions.Contains(extension))
        {
            logger.LogWarning("Skipping photo {PhotoGuid}: browsers cannot show {Extension} files", photo.PhotoGuid, extension);
            return null;
        }

        // Checksums are hex, so they make safe, unique file names that change when the image does.
        var fileName = $"{derivative.Checksum}{extension}";
        var target = Path.Combine(directory, fileName);
        var partial = target + ".part";
        try
        {
            await using (var output = File.Create(partial))
            {
                await client.DownloadAsync(url, output, cancellationToken);
            }

            var size = new FileInfo(partial).Length;
            if (derivative.FileSize is { } expected && expected != size)
            {
                throw new SharedAlbumException($"expected {expected} bytes but received {size}");
            }

            File.Move(partial, target, overwrite: true);
            return fileName;
        }
        catch (Exception exception) when (exception is SharedAlbumException or HttpRequestException or IOException
                                              || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not download photo {PhotoGuid}: {Message}", photo.PhotoGuid, exception.Message);
            File.Delete(partial);
            return null;
        }
    }

    private static IReadOnlyList<AlbumPhoto> ToAlbumPhotos(int widgetId, IEnumerable<CachedAlbumPhoto> photos) =>
        photos
            .Select(photo => new AlbumPhoto(
                $"{SharedAlbumCacheLocation.RequestPath}/{widgetId.ToString(CultureInfo.InvariantCulture)}/{photo.FileName}",
                photo.Caption,
                photo.Contributor,
                photo.DateCreated))
            .ToList();

    private string WidgetDirectory(int widgetId) =>
        Path.Combine(location.RootPath, widgetId.ToString(CultureInfo.InvariantCulture));

    private AlbumCacheManifest? ReadManifest(int widgetId)
    {
        var path = Path.Combine(WidgetDirectory(widgetId), ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AlbumCacheManifest>(File.ReadAllText(path), ManifestJsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            logger.LogWarning("Ignoring an unreadable shared album cache manifest for widget {WidgetId}: {Message}", widgetId, exception.Message);
            return null;
        }
    }

    private void WriteManifest(int widgetId, AlbumCacheManifest manifest)
    {
        var path = Path.Combine(WidgetDirectory(widgetId), ManifestFileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, ManifestJsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class AlbumCacheManifest
    {
        public string Token { get; set; } = "";
        public string? Ctag { get; set; }
        public bool HiRes { get; set; }
        public string? AlbumName { get; set; }
        public DateTimeOffset SyncedUtc { get; set; }
        public List<CachedAlbumPhoto> Photos { get; set; } = [];
    }

    private sealed record CachedAlbumPhoto(string FileName, string PhotoGuid, string Checksum, string? Caption, string? Contributor, DateTimeOffset? DateCreated);
}
