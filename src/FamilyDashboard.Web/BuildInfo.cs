using System.Globalization;
using System.Reflection;

namespace FamilyDashboard.Web;

/// <summary>
/// Version and build timestamp of the running app, read from assembly attributes.
/// The build date is stamped at compile time by the AssemblyMetadata item in the .csproj.
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    public static string Version { get; } = ReadVersion();

    public static DateTimeOffset? BuildDate { get; } = ReadBuildDate();

    private static string ReadVersion()
    {
        var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        // The SDK appends "+<commit sha>" when source control info is available; keep it short.
        var plus = informational.IndexOf('+');
        if (plus >= 0 && informational.Length > plus + 8)
        {
            informational = informational[..(plus + 8)];
        }

        return informational;
    }

    private static DateTimeOffset? ReadBuildDate()
    {
        var value = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
    }
}
