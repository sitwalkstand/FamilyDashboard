namespace FamilyDashboard.Web.Services.HomeStatus;

/// <summary>One Home Assistant entity, reduced to what the Home Status widget shows.</summary>
public sealed record HomeEntityDto(
    string EntityId,
    string Name,
    string State,
    string? Unit,
    string? DeviceClass,
    double? CurrentTemperature,
    IReadOnlyList<AttentionItemDto>? AttentionItems)
{
    public string Domain => EntityId[..EntityId.IndexOf('.')];
}

/// <summary>
/// One entry from the <c>items</c> attribute of the needs-attention template sensor.
/// Severity is "alert", "warning" or "info"; Icon is a Material Design Icons name such as "mdi:water-alert".
/// </summary>
public sealed record AttentionItemDto(string Message, string Severity, string? Icon);

public sealed record HomeStatusStatus(DateTime CheckedUtc, bool Succeeded, string? Message);
