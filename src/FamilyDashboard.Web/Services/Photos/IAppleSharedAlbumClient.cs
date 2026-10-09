using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace FamilyDashboard.Web.Services.Photos;

/// <summary>
/// Reads a publicly shared iCloud album, in either of Apple's two link formats. Both use the
/// unofficial endpoints behind the iCloud web apps, so callers should expect them to change and
/// fail softly.
/// </summary>
public interface IAppleSharedAlbumClient
{
    /// <summary>Returns the album's full photo list, or throws <see cref="SharedAlbumException"/>.</summary>
    Task<SharedAlbumStream> GetStreamAsync(SharedAlbumLink album, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves download URLs for photos whose derivatives have no <see cref="SharedAlbumDerivative.DownloadUrl"/>,
    /// keyed by derivative checksum. Only legacy shared streams need this step.
    /// </summary>
    Task<IReadOnlyDictionary<string, Uri>> GetAssetUrlsAsync(SharedAlbumLink album, IReadOnlyCollection<string> photoGuids, CancellationToken cancellationToken = default);

    /// <summary>Downloads an asset URL into <paramref name="destination"/>.</summary>
    Task DownloadAsync(Uri url, Stream destination, CancellationToken cancellationToken = default);
}

public record SharedAlbumStream(string? StreamName, string? Ctag, IReadOnlyList<SharedAlbumPhoto> Photos);

public record SharedAlbumPhoto(
    string PhotoGuid,
    string? Caption,
    string? Contributor,
    DateTimeOffset? DateCreated,
    IReadOnlyList<SharedAlbumDerivative> Derivatives);

/// <param name="Checksum">Hex, so it doubles as a safe cache file name.</param>
/// <param name="DownloadUrl">Set when the album listing already includes it (Shared Collections).</param>
public record SharedAlbumDerivative(string Checksum, int Width, int Height, long? FileSize, Uri? DownloadUrl = null)
{
    public int LongEdge => Math.Max(Width, Height);
}

public class SharedAlbumException(string message, Exception? innerException = null) : Exception(message, innerException);

public enum SharedAlbumKind
{
    /// <summary>Legacy "Public Website" link: https://www.icloud.com/sharedalbum/#B0aBcDeFgHiJkL.</summary>
    SharedStream,
    /// <summary>Current share link: https://photos.icloud.com/shared/album/031AbC_dEf.../ (CloudKit).</summary>
    SharedCollection
}

public record SharedAlbumLink(SharedAlbumKind Kind, string Token)
{
    /// <summary>The one form a link is stored in, whatever variant was pasted.</summary>
    public string CanonicalUrl => Kind == SharedAlbumKind.SharedStream
        ? $"https://www.icloud.com/sharedalbum/#{Token}"
        : $"https://photos.icloud.com/shared/album/{Token}/";
}

public static partial class SharedAlbumUrl
{
    /// <summary>
    /// Recognises both share link formats: the legacy https://www.icloud.com/sharedalbum/#TOKEN
    /// (a locale segment before the # is fine) and https://photos.icloud.com/shared/album/ID/.
    /// </summary>
    public static bool TryParse(string? url, [NotNullWhen(true)] out SharedAlbumLink? link)
    {
        link = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (uri.Host.Equals("www.icloud.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/sharedalbum", StringComparison.OrdinalIgnoreCase))
        {
            // Some legacy links carry extra fragment parts after a ';' (e.g. a photo id).
            var token = uri.Fragment.TrimStart('#').Split(';', 2)[0];
            if (StreamTokenPattern().IsMatch(token))
            {
                link = new SharedAlbumLink(SharedAlbumKind.SharedStream, token);
            }
        }
        else if (uri.Host.Equals("photos.icloud.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is ["shared", "album", var id, ..] && CollectionIdPattern().IsMatch(id))
            {
                link = new SharedAlbumLink(SharedAlbumKind.SharedCollection, id);
            }
        }

        return link is not null;
    }

    /// <summary>A short, log-safe form of a token: the token is the only key to the album.</summary>
    public static string Mask(string token) => token.Length <= 4 ? "****" : $"{token[..4]}...";

    [GeneratedRegex("^[A-Za-z0-9]{10,40}$")]
    private static partial Regex StreamTokenPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{10,64}$")]
    private static partial Regex CollectionIdPattern();
}
