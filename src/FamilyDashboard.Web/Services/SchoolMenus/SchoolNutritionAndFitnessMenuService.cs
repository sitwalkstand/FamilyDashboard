using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FamilyDashboard.Web.Services.SchoolMenus;

/// <summary>
/// Reads school menus from School Nutrition and Fitness (schoolnutritionandfitness.com), the menu
/// host used by Frederick County Public Schools (VA) among others. Their menu pages refuse to be
/// framed, so this calls the GraphQL API those pages use. It is unofficial and undocumented, so
/// every lookup tolerates missing fields and failures are logged rather than thrown.
/// </summary>
public partial class SchoolNutritionAndFitnessMenuService(HttpClient httpClient, ILogger<SchoolNutritionAndFitnessMenuService> logger)
    : ISchoolMenuService
{
    private const string GraphQlUrl = "https://api.schoolnutritionandfitness.com/graphql";

    private const string SitesQuery = "query($id: String!) { organization(id: $id) { sites { id } } }";

    private const string MenuTypesQuery =
        """query($site: String!) { menuTypes(site: { depth_0_id: $site }, publish_location: "website") { id name } }""";

    // Months are zero-based in this API (9 is October).
    private const string MenuQuery =
        """
        query($id: String!, $month: Int!, $year: Int!, $nextMonth: Int!, $nextYear: Int!) {
          menuType(id: $id) {
            id
            name
            current: menu(month: $month, year: $year) { month year items { day hidden product { name category hide_on_calendars } } }
            next: menu(month: $nextMonth, year: $nextYear) { month year items { day hidden product { name category hide_on_calendars } } }
          }
        }
        """;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public async Task<IReadOnlyList<SchoolMenuType>> GetMenuTypesAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var sites = await QueryAsync(SitesQuery, new { id = organizationId }, cancellationToken);
        if (sites is not { } sitesData
            || !sitesData.TryGetProperty("organization", out var organization)
            || organization.ValueKind != JsonValueKind.Object
            || !organization.TryGetProperty("sites", out var siteList)
            || siteList.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // Each school level (elementary, middle, high) is a site; menus shared between levels appear under each.
        var menuTypes = new List<SchoolMenuType>();
        foreach (var site in siteList.EnumerateArray())
        {
            var siteId = GetString(site, "id");
            if (string.IsNullOrEmpty(siteId))
            {
                continue;
            }

            var result = await QueryAsync(MenuTypesQuery, new { site = siteId }, cancellationToken);
            if (result is not { } data || !data.TryGetProperty("menuTypes", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var menuType in list.EnumerateArray())
            {
                var id = GetString(menuType, "id");
                if (!string.IsNullOrEmpty(id) && menuTypes.All(existing => existing.Id != id))
                {
                    menuTypes.Add(new SchoolMenuType(id, CleanName(GetString(menuType, "name"))));
                }
            }
        }

        return menuTypes.OrderBy(menuType => menuType.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<SchoolMenuDto?> GetMenuAsync(string menuTypeId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var next = today.AddMonths(1);
        var result = await QueryAsync(MenuQuery, new
        {
            id = menuTypeId,
            month = today.Month - 1,
            year = today.Year,
            nextMonth = next.Month - 1,
            nextYear = next.Year
        }, cancellationToken);
        if (result is not { } data || !data.TryGetProperty("menuType", out var menuType) || menuType.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var days = new List<SchoolMenuDay>();
        foreach (var alias in new[] { "current", "next" })
        {
            if (menuType.TryGetProperty(alias, out var menu) && menu.ValueKind == JsonValueKind.Object)
            {
                days.AddRange(ReadDays(menu));
            }
        }

        return new SchoolMenuDto(menuTypeId, CleanName(GetString(menuType, "name")), days.OrderBy(day => day.Date).ToList(), DateTime.UtcNow);
    }

    private static IEnumerable<SchoolMenuDay> ReadDays(JsonElement menu)
    {
        if (!menu.TryGetProperty("month", out var monthValue) || !monthValue.TryGetInt32(out var month)
            || !menu.TryGetProperty("year", out var yearValue) || !yearValue.TryGetInt32(out var year)
            || !menu.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var lines = new List<(int Day, string Name, bool IsLabel)>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("day", out var dayValue) || !dayValue.TryGetInt32(out var day)
                || IsSet(item, "hidden")
                || !item.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object
                || IsSet(product, "hide_on_calendars"))
            {
                continue;
            }

            var name = CleanName(GetString(product, "name"));
            if (name.Length == 0)
            {
                continue;
            }

            var isLabel = string.Equals(GetString(product, "category"), "Ancillary", StringComparison.OrdinalIgnoreCase)
                || name.Equals("or", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Choose", StringComparison.OrdinalIgnoreCase);
            lines.Add((day, name, isLabel));
        }

        foreach (var group in lines.GroupBy(line => line.Day))
        {
            DateOnly date;
            try
            {
                date = new DateOnly(year, month + 1, group.Key);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            // Entrée choices come first; the first "Choose ..." line starts the sides.
            var inEntrees = true;
            var dayItems = new List<SchoolMenuItem>();
            foreach (var (_, name, isLabel) in group)
            {
                if (isLabel && name.StartsWith("Choose", StringComparison.OrdinalIgnoreCase))
                {
                    inEntrees = false;
                }
                dayItems.Add(new SchoolMenuItem(name, isLabel, inEntrees));
            }

            yield return new SchoolMenuDay(date, dayItems);
        }
    }

    private async Task<JsonElement?> QueryAsync(string query, object variables, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(GraphQlUrl, new { query, variables }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                logger.LogWarning("School menu query returned errors: {Errors}", errors.ToString());
            }
            return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data.Clone() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "School menu request failed");
            return null;
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // Flags come back as null, booleans or "1".
    private static bool IsSet(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString() is { Length: > 0 } text && text != "0" && !text.Equals("false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    private static string CleanName(string name) => Whitespace().Replace(name, " ").Trim();
}
