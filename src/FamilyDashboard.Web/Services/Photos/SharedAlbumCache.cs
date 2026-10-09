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
/// Mirrors a shared iCloud album into &lt;DataDirectory&gt;/AlbumCache/&lt;widgetId&gt;, so the dashboard
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

    public async Task<AlbumSyncResult> SyncAsync(int widgetId, SharedAlbumLink album, bool hiRes, CancellationToken cancellationToken)
    {
        var token = album.Token;
        var stream = await client.GetStreamAsync(album, cancellationToken);
        var directory = WidgetDirectory(widgetId);
        Directory.CreateDirectory(directory);

        var manifest = ReadManifest(widgetId);
        var previous = manifest?.Token == token
            ? manifest.Photos
                .Where(photo => File.Exists(Path.Combine(directory, photo.FileName)))
                .GroupBy(photo => photo.PhotoGuid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
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

        var photos = new List<CachedAlbumPhoto>();
        var toDownload = new List<(SharedAlbumPhoto Photo, SharedAlbumDerivative Preferred)>();
        foreach (var photo in stream.Photos)
        {
            var preferred = PickDerivative(photo.Derivatives, hiRes);
            // Reuse the cached file while it is the one chosen for this preference (possibly a fallback
            // size, when the preferred one could not be shown) and is still part of the photo.
            if (previous.TryGetValue(photo.PhotoGuid, out var cached)
                && (cached.PreferredChecksum ?? cached.Checksum) == preferred.Checksum
                && photo.Derivatives.Any(derivative => derivative.Checksum == cached.Checksum))
            {
                photos.Add(cached with { Caption = photo.Caption, Contributor = photo.Contributor, DateCreated = photo.DateCreated });
            }
            else
            {
                toDownload.Add((photo, preferred));
            }
        }

        var added = 0;
        var failed = 0;
        if (toDownload.Count > 0)
        {
            // Shared Collections list download URLs with the photos; legacy streams need a lookup.
            var needUrls = toDownload
                .Where(item => item.Photo.Derivatives.Any(derivative => derivative.DownloadUrl is null))
                .Select(item => item.Photo.PhotoGuid)
                .ToList();
            var urls = needUrls.Count > 0
                ? await client.GetAssetUrlsAsync(album, needUrls, cancellationToken)
                : new Dictionary<string, Uri>();

            foreach (var (photo, preferred) in toDownload)
            {
                var downloaded = await DownloadPhotoAsync(directory, photo, preferred, urls, cancellationToken);
                if (downloaded is null)
                {
                    failed++;
                }
                else
                {
                    photos.Add(downloaded);
                    added++;
                }
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

        // Keep the album's order rather than "cached first, downloaded after".
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var photo in stream.Photos)
        {
            order.TryAdd(photo.PhotoGuid, order.Count);
        }
        photos = photos.OrderBy(photo => order.GetValueOrDefault(photo.PhotoGuid)).ToList();

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

        return new AlbumSyncResult(stream.StreamName, ToAlbumPhotos(widgetId, photos), added, removed, failed, Unchanged: false);
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

    // Tries the preferred size first, then the others from largest down, but only moves on when a
    // file turns out to be a format browsers cannot show (e.g. a real HEIC original). A network or
    // size failure stops there, so the next sync retries the preferred size.
    private async Task<CachedAlbumPhoto?> DownloadPhotoAsync(
        string directory,
        SharedAlbumPhoto photo,
        SharedAlbumDerivative preferred,
        IReadOnlyDictionary<string, Uri> urls,
        CancellationToken cancellationToken)
    {
        var candidates = photo.Derivatives
            .Where(derivative => derivative != preferred)
            .OrderByDescending(derivative => derivative.LongEdge)
            .Prepend(preferred);

        foreach (var derivative in candidates)
        {
            var url = derivative.DownloadUrl ?? urls.GetValueOrDefault(derivative.Checksum);
            if (url is null)
            {
                logger.LogWarning("iCloud returned no download URL for photo {PhotoGuid}", photo.PhotoGuid);
                return null;
            }

            var partial = Path.Combine(directory, $"{derivative.Checksum}.part");
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

                // Go by the bytes, not the declared type: a live album listed JPEG files as HEIC.
                var extension = SniffImageExtension(partial);
                if (extension is null)
                {
                    File.Delete(partial);
                    logger.LogInformation("Photo {PhotoGuid}: its {Width}x{Height} version is not a format browsers can show; trying another size",
                        photo.PhotoGuid, derivative.Width, derivative.Height);
                    continue;
                }

                // Checksums are hex, so they make safe, unique file names that change when the image does.
                var fileName = $"{derivative.Checksum}{extension}";
                File.Move(partial, Path.Combine(directory, fileName), overwrite: true);
                return new CachedAlbumPhoto(fileName, photo.PhotoGuid, derivative.Checksum, photo.Caption, photo.Contributor, photo.DateCreated)
                {
                    PreferredChecksum = preferred.Checksum
                };
            }
            catch (Exception exception) when (exception is SharedAlbumException or HttpRequestException or IOException
                                                  || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not download photo {PhotoGuid}: {Message}", photo.PhotoGuid, exception.Message);
                File.Delete(partial);
                return null;
            }
        }

        logger.LogWarning("Skipping photo {PhotoGuid}: none of its sizes is a format browsers can show", photo.PhotoGuid);
        return null;
    }

    private static string? SniffImageExtension(string path)
    {
        Span<byte> buffer = stackalloc byte[12];
        int read;
        using (var file = File.OpenRead(path))
        {
            read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        ReadOnlySpan<byte> header = buffer[..read];
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return ".jpg";
        }
        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
        {
            return ".png";
        }
        if (header.StartsWith("GIF8"u8))
        {
            return ".gif";
        }
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        return null;
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

    private sealed record CachedAlbumPhoto(string FileName, string PhotoGuid, string Checksum, string? Caption, string? Contributor, DateTimeOffset? DateCreated)
    {
        /// <summary>The size that was wanted when this file was chosen; differs from Checksum after a fallback.</summary>
        public string? PreferredChecksum { get; init; }
    }
}
