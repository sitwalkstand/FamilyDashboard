using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FamilyDashboard.Web.Services.FamilyLocations;

/// <summary>
/// Reads Life360 locations through a Home Assistant instance running the Life360 integration.
/// Life360 has no public API and blocks direct access, so Home Assistant absorbs that breakage and
/// this app only needs Home Assistant's documented REST API (<c>GET /api/states</c>).
/// Configure with <c>HomeAssistant:BaseUrl</c> and <c>HomeAssistant:Token</c> (a long-lived access token).
/// </summary>
public class HomeAssistantFamilyLocationService(HttpClient http, IConfiguration configuration) : IFamilyLocationService
{
    private string BaseUrl => (configuration["HomeAssistant:BaseUrl"] ?? "").Trim().TrimEnd('/');
    private string Token => (configuration["HomeAssistant:Token"] ?? "").Trim();

    public bool IsConfigured => BaseUrl.Length > 0 && Token.Length > 0;

    public async Task<IReadOnlyList<FamilyMemberDto>> GetMembersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Home Assistant isn't configured. Set HomeAssistant:BaseUrl and HomeAssistant:Token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/states");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Home Assistant rejected the access token.");
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var members = new List<FamilyMemberDto>();
        foreach (var entity in document.RootElement.EnumerateArray())
        {
            var entityId = GetString(entity, "entity_id");
            if (entityId is null || !entityId.StartsWith("device_tracker.", StringComparison.Ordinal))
            {
                continue;
            }

            var state = GetString(entity, "state") ?? "unknown";
            var attributes = entity.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                ? attrs
                : default;

            var name = GetString(attributes, "friendly_name") ?? entityId["device_tracker.".Length..];
            var isHome = state.Equals("home", StringComparison.OrdinalIgnoreCase);
            members.Add(new FamilyMemberDto(
                entityId,
                name,
                DescribePlace(state, attributes),
                isHome,
                GetInt(attributes, "battery_level") ?? GetInt(attributes, "battery"),
                GetBool(attributes, "battery_charging"),
                GetBool(attributes, "driving"),
                GetDate(attributes, "last_seen") ?? GetDate(entity, "last_updated"),
                GetDouble(attributes, "latitude"),
                GetDouble(attributes, "longitude")));
        }

        return members.OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // "home" and named zones come through as the state; "not_home" means no zone matched, so the
    // street address (when the integration provides one) says more than "Away".
    private static string DescribePlace(string state, JsonElement attributes)
    {
        if (state.Equals("home", StringComparison.OrdinalIgnoreCase))
        {
            return "Home";
        }
        if (state.Equals("not_home", StringComparison.OrdinalIgnoreCase))
        {
            var place = GetString(attributes, "place");
            return !string.IsNullOrWhiteSpace(place) ? place
                : GetString(attributes, "address") is { Length: > 0 } address ? address
                : "Away";
        }
        if (state.Equals("unknown", StringComparison.OrdinalIgnoreCase) || state.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }
        return state;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => (int)Math.Round(number),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => (int)Math.Round(parsed),
            _ => null
        };
    }

    private static double? GetDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static DateTime? GetDate(JsonElement element, string property) =>
        GetString(element, property) is { } text
        && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
