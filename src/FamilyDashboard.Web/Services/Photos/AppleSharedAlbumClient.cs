using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FamilyDashboard.Web.Services.Photos;

/// <summary>
/// Reads public iCloud shared albums. Response shapes were taken from a live album in both formats:
/// <list type="bullet">
/// <item>Legacy shared streams (icloud.com/sharedalbum): numbers arrive as strings, derivatives are
/// keyed by size, and download URLs come from a second call keyed by derivative checksum.</item>
/// <item>Shared Collections (photos.icloud.com/shared/album): CloudKit. The share id resolves to an
/// anonymous token and a zone, whose CPLAsset/CPLMaster records carry ready-to-use download URLs.</item>
/// </list>
/// </summary>
public partial class AppleSharedAlbumClient(HttpClient http, ILogger<AppleSharedAlbumClient> logger) : IAppleSharedAlbumClient
{
    // Any legacy partition answers with HTTP 330 and the album's real host; that host is remembered
    // so later calls go straight there. (Deriving it from the token did not match a live album.)
    private const string DefaultStreamHost = "p01-sharedstreams.icloud.com";
    private const int MaxRedirects = 3;
    private const int AssetUrlBatchSize = 25;
    private static readonly ConcurrentDictionary<string, string> StreamHosts = new();

    private const string CloudKitContainer = "com.apple.photos.cloud";
    private const string CloudKitResolveHost = "https://ckdatabasews.icloud.com";
    private const int CollectionPageSize = 200;
    private const int MaxCollectionPages = 100;
    private static readonly JsonSerializerOptions CloudKitJsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public Task<SharedAlbumStream> GetStreamAsync(SharedAlbumLink album, CancellationToken cancellationToken = default) =>
        album.Kind == SharedAlbumKind.SharedCollection
            ? GetCollectionAsync(album.Token, cancellationToken)
            : GetLegacyStreamAsync(album.Token, cancellationToken);

    public async Task<IReadOnlyDictionary<string, Uri>> GetAssetUrlsAsync(SharedAlbumLink album, IReadOnlyCollection<string> photoGuids, CancellationToken cancellationToken = default)
    {
        var urls = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        if (album.Kind != SharedAlbumKind.SharedStream)
        {
            return urls;
        }

        foreach (var batch in photoGuids.Chunk(AssetUrlBatchSize))
        {
            using var document = await PostStreamAsync(album.Token, "webasseturls", new { photoGuids = batch }, cancellationToken);
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

    // ---- Shared Collections (CloudKit) ----

    private async Task<SharedAlbumStream> GetCollectionAsync(string shareId, CancellationToken cancellationToken)
    {
        var key = Uri.EscapeDataString(shareId);
        using var resolved = await PostCloudKitAsync(
            $"{CloudKitResolveHost}/database/1/{CloudKitContainer}/production/public/records/resolve?remapEnums=true&getCurrentSyncToken=true&sharing_url_key={key}",
            new { shortGUIDs = new[] { new { value = shareId } } },
            cancellationToken);

        if (!resolved.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            throw new SharedAlbumException("iCloud's share lookup returned no result. Apple may have changed the shared album service.");
        }

        var share = results[0];
        if (ReadString(share, "serverErrorCode") is { } errorCode)
        {
            throw new SharedAlbumException(errorCode == "NOT_FOUND"
                ? "iCloud could not find the shared album. Check the link, and that the album is still shared publicly."
                : $"iCloud could not open the shared album ({errorCode}: {ReadString(share, "reason")}).");
        }

        if (share.TryGetProperty("requireAppleLogin", out var requireLogin) && requireLogin.ValueKind == JsonValueKind.True)
        {
            throw new SharedAlbumException("This shared album needs an Apple Account sign-in. Turn on public link sharing for it in Photos.");
        }

        if (!share.TryGetProperty("zoneID", out var zoneElement)
            || !share.TryGetProperty("anonymousPublicAccess", out var access)
            || ReadString(access, "token") is not { } accessToken
            || ReadString(access, "databasePartition") is not { } partition
            || !CloudKitPartitionPattern().IsMatch(partition))
        {
            throw new SharedAlbumException("iCloud's share lookup did not include public access details. Apple may have changed the shared album service.");
        }

        var zone = zoneElement.Clone();
        var albumName = share.TryGetProperty("share", out var shareRecord) ? ReadField(shareRecord, "cloudkit.title") : null;
        var ownerRecordName = share.TryGetProperty("ownerIdentity", out var owner) ? ReadString(owner, "userRecordName") : null;
        var ownerName = owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("nameComponents", out var names)
            ? NullIfBlank($"{ReadString(names, "givenName")} {ReadString(names, "familyName")}")
            : null;

        var queryUrl = $"{partition.TrimEnd('/')}/database/1/{CloudKitContainer}/production/shared/records/query"
            + $"?remapEnums=true&getCurrentSyncToken=true&sharing_url_key={key}&publicAccessAuthToken={Uri.EscapeDataString(accessToken)}";

        var assets = new List<JsonElement>();
        var masters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string? syncToken = null;
        string? continuation = null;
        for (var page = 0; page < MaxCollectionPages; page++)
        {
            using var response = await PostCloudKitAsync(queryUrl, new
            {
                query = new
                {
                    recordType = "CPLAssetAndMasterByAssetDateWithoutHiddenOrDeleted",
                    filterBy = new[] { new { fieldName = "direction", comparator = "EQUALS", fieldValue = new { value = "ASCENDING", type = "STRING" } } }
                },
                zoneID = zone,
                resultsLimit = CollectionPageSize,
                continuationMarker = continuation
            }, cancellationToken);

            var root = response.RootElement;
            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            {
                throw new SharedAlbumException("iCloud's album response has no photo list. Apple may have changed the shared album service.");
            }

            syncToken ??= ReadString(root, "syncToken");
            foreach (var record in records.EnumerateArray())
            {
                switch (ReadString(record, "recordType"))
                {
                    case "CPLAsset":
                        assets.Add(record.Clone());
                        break;
                    case "CPLMaster" when ReadString(record, "recordName") is { } masterName:
                        masters[masterName] = record.Clone();
                        break;
                }
            }

            continuation = ReadString(root, "continuationMarker");
            if (continuation is null)
            {
                break;
            }
        }

        var photos = new List<SharedAlbumPhoto>();
        foreach (var asset in assets)
        {
            var guid = ReadString(asset, "recordName");
            if (guid is null || !TryGetFields(asset, out var assetFields)
                || !assetFields.TryGetProperty("masterRef", out var masterRef)
                || !masterRef.TryGetProperty("value", out var masterRefValue)
                || ReadString(masterRefValue, "recordName") is not { } masterName
                || !masters.TryGetValue(masterName, out var master))
            {
                continue;
            }

            var derivatives = ReadCollectionDerivatives(master);
            if (derivatives.Count == 0)
            {
                continue;
            }

            var created = assetFields.TryGetProperty("assetDate", out var assetDate) && ReadLong(assetDate, "value") is { } milliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : (DateTimeOffset?)null;
            // Collections carry no contributor names; the owner's is the only one the share exposes.
            var contributor = asset.TryGetProperty("created", out var createdBy) && ReadString(createdBy, "userRecordName") == ownerRecordName
                ? ownerName
                : null;

            // Shared Collections have no per-photo captions.
            photos.Add(new SharedAlbumPhoto(guid, null, contributor, created, derivatives));
        }

        return new SharedAlbumStream(albumName, syncToken, photos);
    }

    // Image renditions on a CPLMaster are res{Name}Res fields with matching res{Name}Width/Height;
    // video renditions (resVid*, resOriginalVidCompl for Live Photos) are left out.
    private static List<SharedAlbumDerivative> ReadCollectionDerivatives(JsonElement master)
    {
        var derivatives = new List<SharedAlbumDerivative>();
        if (!TryGetFields(master, out var fields))
        {
            return derivatives;
        }

        foreach (var field in fields.EnumerateObject())
        {
            var match = CollectionResourcePattern().Match(field.Name);
            if (!match.Success || field.Name.Contains("Vid", StringComparison.Ordinal)
                || !field.Value.TryGetProperty("value", out var resource))
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            var checksum = ReadString(resource, "fileChecksum");
            var downloadUrl = ReadString(resource, "downloadURL")?.Replace("${f}", "photo", StringComparison.Ordinal);
            var width = (int)(ReadFieldLong(fields, $"res{name}Width") ?? 0);
            var height = (int)(ReadFieldLong(fields, $"res{name}Height") ?? 0);
            if (checksum is null || downloadUrl is null || width <= 0 || height <= 0
                || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var url) || !IsAppleContentHost(url))
            {
                continue;
            }

            string hexChecksum;
            try
            {
                hexChecksum = Convert.ToHexStringLower(Convert.FromBase64String(checksum));
            }
            catch (FormatException)
            {
                continue;
            }

            derivatives.Add(new SharedAlbumDerivative(hexChecksum, width, height, ReadLong(resource, "size"), url));
        }

        return derivatives;
    }

    private async Task<JsonDocument> PostCloudKitAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, CloudKitJsonOptions), Encoding.UTF8, "text/plain")
        };
        using var response = await http.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SharedAlbumException($"iCloud returned HTTP {(int)response.StatusCode} reading the shared album.");
        }

        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new SharedAlbumException("iCloud's shared album response was not JSON. Apple may have changed the shared album service.", exception);
        }
    }

    // ---- Legacy shared streams ----

    private async Task<SharedAlbumStream> GetLegacyStreamAsync(string token, CancellationToken cancellationToken)
    {
        // Always ask for the full list (no ctag): with a ctag the service answers an unchanged album
        // with an empty list, and its behaviour for a changed one is undocumented. The caller compares
        // the returned ctag itself to skip unchanged albums.
        using var document = await PostStreamAsync(token, "webstream", new { streamCtag = (string?)null }, cancellationToken);
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
                    if (!string.IsNullOrEmpty(checksum) && HexPattern().IsMatch(checksum) && width > 0 && height > 0)
                    {
                        derivatives.Add(new SharedAlbumDerivative(checksum.ToLowerInvariant(), width, height, ReadLong(derivative.Value, "fileSize")));
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

    private async Task<JsonDocument> PostStreamAsync(string token, string endpoint, object body, CancellationToken cancellationToken)
    {
        var host = StreamHosts.GetValueOrDefault(token, DefaultStreamHost);
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
                if (redirectHost is null || !StreamPartitionHostPattern().IsMatch(redirectHost))
                {
                    throw new SharedAlbumException($"iCloud redirected the album to an unexpected host '{redirectHost}'.");
                }

                logger.LogInformation("Shared album {Token} is served from {Host}", SharedAlbumUrl.Mask(token), redirectHost);
                StreamHosts[token] = redirectHost;
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

    // ---- JSON helpers ----

    private static bool IsAppleContentHost(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps
        && (url.Host.EndsWith(".icloud-content.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase));

    private static bool TryGetFields(JsonElement record, out JsonElement fields) =>
        record.TryGetProperty("fields", out fields) && fields.ValueKind == JsonValueKind.Object;

    /// <summary>Reads a CloudKit record field's value, e.g. fields["cloudkit.title"].value.</summary>
    private static string? ReadField(JsonElement record, string name) =>
        TryGetFields(record, out var fields) && fields.TryGetProperty(name, out var field) ? ReadString(field, "value") : null;

    private static long? ReadFieldLong(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var field) ? ReadLong(field, "value") : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    // Legacy streams send sizes and dimensions as strings ("1537"); CloudKit sends numbers.
    private static long? ReadLong(JsonElement element, string name) =>
        long.TryParse(ReadString(element, name), out var value) ? value : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^p\d{1,3}-sharedstreams\.icloud\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex StreamPartitionHostPattern();

    [GeneratedRegex(@"^https://p\d{1,3}-ckdatabasews\.icloud\.com(:443)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex CloudKitPartitionPattern();

    [GeneratedRegex("^res(?<name>[A-Za-z]+)Res$")]
    private static partial Regex CollectionResourcePattern();

    [GeneratedRegex("^[0-9A-Fa-f]+$")]
    private static partial Regex HexPattern();
}
