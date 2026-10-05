using System.Reflection;
using System.Text.Json;

namespace FamilyDashboard.Web.Components.Widgets;

public static class FontAwesomeFreeIconCatalog
{
    private const string ResourceName = "FamilyDashboard.Web.Components.Widgets.fontawesome-free-icons.json";
    private static readonly Lazy<IReadOnlyList<FontAwesomeIconOption>> CachedIcons = new(LoadIcons);

    public static IReadOnlyList<FontAwesomeIconOption> Icons => CachedIcons.Value;

    private static IReadOnlyList<FontAwesomeIconOption> LoadIcons()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded icon catalog '{ResourceName}' was not found.");
        var definitions = JsonSerializer.Deserialize<IconDefinition[]>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

        return definitions
            .SelectMany(icon => icon.Styles.Select(style => new FontAwesomeIconOption(
                icon.Name,
                icon.Label,
                style switch
                {
                    "solid" => "Solid",
                    "regular" => "Regular",
                    "brands" => "Brands",
                    _ => throw new InvalidOperationException($"Unknown Font Awesome style '{style}'.")
                },
                string.Join(" ", icon.SearchTerms))))
            .OrderBy(icon => icon.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(icon => icon.Style, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record IconDefinition(string Name, string Label, string[] Styles, string[] SearchTerms);
}

public sealed record FontAwesomeIconOption(string Name, string Label, string Style, string SearchTerms)
{
    public string Value => CalendarIconValue.Create(Style, Name);
}