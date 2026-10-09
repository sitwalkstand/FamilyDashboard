using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace FamilyDashboard.Web.Services.Photos;

/// <summary>
/// Reads an iCloud Shared Album that has "Public Website" turned on. These are the unofficial
/// endpoints behind icloud.com/sharedalbum, so callers should expect them to change and fail softly.
/// </summary>
public interface IAppleSharedAlbumClient
{
    /// <summary>Returns the album's full photo list, or throws <see cref="SharedAlbumException"/>.</summary>
    Task<SharedAlbumStream> GetStreamAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Resolves download URLs for the given photos, keyed by derivative checksum.</summary>
    Task<IReadOnlyDictionary<string, Uri>> GetAssetUrlsAsync(string token, IReadOnlyCollection<string> photoGuids, CancellationToken cancellationToken = default);

    /// <summary>Downloads an asset URL returned by <see cref="GetAssetUrlsAsync"/> into <paramref name="destination"/>.</summary>
    Task DownloadAsync(Uri url, Stream destination, CancellationToken cancellationToken = default);
}

public record SharedAlbumStream(string? StreamName, string? Ctag, IReadOnlyList<SharedAlbumPhoto> Photos);

public record SharedAlbumPhoto(
    string PhotoGuid,
    string? Caption,
    string? Contributor,
    DateTimeOffset? DateCreated,
    IReadOnlyList<SharedAlbumDerivative> Derivatives);

public record SharedAlbumDerivative(string Checksum, int Width, int Height, long? FileSize)
{
    public int LongEdge => Math.Max(Width, Height);
}

public class SharedAlbumException(string message, Exception? innerException = null) : Exception(message, innerException);

public static partial class SharedAlbumUrl
{
    /// <summary>
    /// Pulls the album token out of a public website link such as
    /// https://www.icloud.com/sharedalbum/#B0aBcDeFgHiJkL (a locale segment before the # is fine).
    /// </summary>
    public static bool TryGetToken(string? url, [NotNullWhen(true)] out string? token)
    {
        token = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("www.icloud.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/sharedalbum", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fragment = uri.Fragment.TrimStart('#');
        // Some share links carry extra fragment parts after a ';' (e.g. a photo id).
        var candidate = fragment.Split(';', 2)[0];
        if (!TokenPattern().IsMatch(candidate))
        {
            return false;
        }

        token = candidate;
        return true;
    }

    /// <summary>A short, log-safe form of a token: the token is the only key to the album.</summary>
    public static string Mask(string token) => token.Length <= 4 ? "****" : $"{token[..4]}...";

    [GeneratedRegex("^[A-Za-z0-9]{10,40}$")]
    private static partial Regex TokenPattern();
}
