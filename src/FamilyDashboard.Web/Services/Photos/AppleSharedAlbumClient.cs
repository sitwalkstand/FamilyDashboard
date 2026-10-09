using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FamilyDashboard.Web.Services.Photos;

/// <summary>
/// Talks to the sharedstreams endpoints used by icloud.com/sharedalbum. Response shapes were taken
/// from a live album: numbers arrive as strings, derivatives are keyed by size, and asset URLs are
/// keyed by derivative checksum.
/// </summary>
public partial class AppleSharedAlbumClient(HttpClient http, ILogger<AppleSharedAlbumClient> logger) : IAppleSharedAlbumClient
{
    // Any partition answers with HTTP 330 and the album's real host; that host is remembered so
    // later calls go straight there. (Deriving it from the token did not match a live album.)
    private const string DefaultHost = "p01-sharedstreams.icloud.com";
    private const int MaxRedirects = 3;
    private const int AssetUrlBatchSize = 25;
    private static readonly ConcurrentDictionary<string, string> AlbumHosts = new();

    public async Task<SharedAlbumStream> GetStreamAsync(string token, CancellationToken cancellationToken = default)
    {
        // Always ask for the full list (no ctag): with a ctag the service answers an unchanged album
        // with an empty list, and its behaviour for a changed one is undocumented. The caller compares
        // the returned ctag itself to skip unchanged albums.
        using var document = await PostAsync(token, "webstream", new { streamCtag = (string?)null }, cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("photos", out var photosElement)
            || photosElement.ValueKind != JsonValueKind.Array)
        {
            throw new SharedAlbumException("iCloud's album response has no photo list. Apple may have changed the shared album service.");
        }

        var photos = new List<SharedAlbumPhoto>();
        var skippedVideos = 0;
        foreach (var item in photosElement.EnumerateArray())
        {
            var guid = ReadString(item, "photoGuid");
            if (string.IsNullOrEmpty(guid))
            {
                continue;
            }

            if (string.Equals(ReadString(item, "mediaAssetType"), "video", StringComparison.OrdinalIgnoreCase))
            {
                skippedVideos++;
                continue;
            }

            var derivatives = new List<SharedAlbumDerivative>();
            if (item.TryGetProperty("derivatives", out var derivativesElement) && derivativesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var derivative in derivativesElement.EnumerateObject())
                {
                    var checksum = ReadString(derivative.Value, "checksum");
                    var width = (int)(ReadLong(derivative.Value, "width") ?? 0);
                    var height = (int)(ReadLong(derivative.Value, "height") ?? 0);
                    if (!string.IsNullOrEmpty(checksum) && width > 0 && height > 0)
                    {
                        derivatives.Add(new SharedAlbumDerivative(checksum, width, height, ReadLong(derivative.Value, "fileSize")));
                    }
                }
            }

            if (derivatives.Count == 0)
            {
                continue;
            }

            var contributor = ReadString(item, "contributorFullName");
            if (string.IsNullOrWhiteSpace(contributor))
            {
                contributor = $"{ReadString(item, "contributorFirstName")} {ReadString(item, "contributorLastName")}".Trim();
            }

            photos.Add(new SharedAlbumPhoto(
                guid,
                NullIfBlank(ReadString(item, "caption")),
                NullIfBlank(contributor),
                DateTimeOffset.TryParse(ReadString(item, "dateCreated"), out var created) ? created : null,
                derivatives));
        }

        if (skippedVideos > 0)
        {
            logger.LogInformation("Skipped {Count} video(s) in shared album {Token}; only photos are shown", skippedVideos, SharedAlbumUrl.Mask(token));
        }

        return new SharedAlbumStream(NullIfBlank(ReadString(root, "streamName")), NullIfBlank(ReadString(root, "streamCtag")), photos);
    }

    public async Task<IReadOnlyDictionary<string, Uri>> GetAssetUrlsAsync(string token, IReadOnlyCollection<string> photoGuids, CancellationToken cancellationToken = default)
    {
        var urls = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in photoGuids.Chunk(AssetUrlBatchSize))
        {
            using var document = await PostAsync(token, "webasseturls", new { photoGuids = batch }, cancellationToken);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            {
                throw new SharedAlbumException("iCloud's asset URL response has no items. Apple may have changed the shared album service.");
            }

            foreach (var item in items.EnumerateObject())
            {
                var location = ReadString(item.Value, "url_location");
                var path = ReadString(item.Value, "url_path");
                if (location is not null && path is not null
                    && Uri.TryCreate($"https://{location}{path}", UriKind.Absolute, out var url)
                    && IsAppleContentHost(url))
                {
                    urls[item.Name] = url;
                }
            }
        }

        return urls;
    }

    public async Task DownloadAsync(Uri url, Stream destination, CancellationToken cancellationToken = default)
    {
        if (!IsAppleContentHost(url))
        {
            throw new SharedAlbumException($"Refusing to download a photo from unexpected host {url.Host}.");
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SharedAlbumException($"iCloud returned HTTP {(int)response.StatusCode} downloading a photo.");
        }

        await response.Content.CopyToAsync(destination, cancellationToken);
    }

    private async Task<JsonDocument> PostAsync(string token, string endpoint, object body, CancellationToken cancellationToken)
    {
        var host = AlbumHosts.GetValueOrDefault(token, DefaultHost);
        for (var attempt = 0; attempt <= MaxRedirects; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/{token}/sharedstreams/{endpoint}")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain")
            };
            using var response = await http.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if ((int)response.StatusCode == 330)
            {
                var redirectHost = response.Headers.TryGetValues("X-Apple-MMe-Host", out var values) ? values.FirstOrDefault() : null;
                redirectHost ??= TryReadRedirectHost(content);
                if (redirectHost is null || !PartitionHostPattern().IsMatch(redirectHost))
                {
                    throw new SharedAlbumException($"iCloud redirected the album to an unexpected host '{redirectHost}'.");
                }

                logger.LogInformation("Shared album {Token} is served from {Host}", SharedAlbumUrl.Mask(token), redirectHost);
                AlbumHosts[token] = redirectHost;
                host = redirectHost;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SharedAlbumException("iCloud could not find the shared album. Check the link, and that Public Website is still turned on for the album.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SharedAlbumException($"iCloud returned HTTP {(int)response.StatusCode} for {endpoint}.");
            }

            try
            {
                return JsonDocument.Parse(content);
            }
            catch (JsonException exception)
            {
                throw new SharedAlbumException($"iCloud's {endpoint} response was not JSON. Apple may have changed the shared album service.", exception);
            }
        }

        throw new SharedAlbumException("iCloud redirected the album too many times.");
    }

    private static string? TryReadRedirectHost(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadString(document.RootElement, "X-Apple-MMe-Host");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAppleContentHost(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps
        && (url.Host.EndsWith(".icloud-content.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    // Apple sends sizes and dimensions as strings ("1537"); accept real numbers too.
    private static long? ReadLong(JsonElement element, string name) =>
        long.TryParse(ReadString(element, name), out var value) ? value : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^p\d{1,3}-sharedstreams\.icloud\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex PartitionHostPattern();
}
