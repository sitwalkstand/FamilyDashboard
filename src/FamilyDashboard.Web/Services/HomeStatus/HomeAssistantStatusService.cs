using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FamilyDashboard.Web.Services.HomeStatus;

/// <summary>
/// Reads entity states from Home Assistant's REST API (<c>GET /api/states</c>) for the Home Status
/// widget, using the same <c>HomeAssistant:BaseUrl</c> and <c>HomeAssistant:Token</c> settings as
/// Family Locations. Read-only: the dashboard has no login, so it never calls Home Assistant services.
/// </summary>
public class HomeAssistantStatusService(HttpClient http, IConfiguration configuration) : IHomeStatusService
{
    private string BaseUrl => (configuration["HomeAssistant:BaseUrl"] ?? "").Trim().TrimEnd('/');
    private string Token => (configuration["HomeAssistant:Token"] ?? "").Trim();

    public bool IsConfigured => BaseUrl.Length > 0 && Token.Length > 0;

    public async Task<IReadOnlyList<HomeEntityDto>> GetEntitiesAsync(CancellationToken cancellationToken = default)
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

        var entities = new List<HomeEntityDto>();
        foreach (var entity in document.RootElement.EnumerateArray())
        {
            var entityId = GetString(entity, "entity_id");
            if (entityId is null || !entityId.Contains('.'))
            {
                continue;
            }

            var attributes = entity.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                ? attrs
                : default;
            entities.Add(new HomeEntityDto(
                entityId,
                GetString(attributes, "friendly_name") ?? entityId[(entityId.IndexOf('.') + 1)..],
                GetString(entity, "state") ?? "unknown",
                GetString(attributes, "unit_of_measurement"),
                GetString(attributes, "device_class"),
                GetDouble(attributes, "current_temperature"),
                GetAttentionItems(attributes)));
        }

        return entities.OrderBy(entity => entity.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The needs-attention template sensor keeps its list in an "items" attribute:
    // [{"message": "...", "severity": "alert", "icon": "mdi:..."}]. Other entities don't have one.
    private static List<AttentionItemDto>? GetAttentionItems(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<AttentionItemDto>();
        foreach (var item in items.EnumerateArray())
        {
            if (GetString(item, "message") is { Length: > 0 } message)
            {
                result.Add(new AttentionItemDto(message, GetString(item, "severity") ?? "info", GetString(item, "icon")));
            }
        }
        return result;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
