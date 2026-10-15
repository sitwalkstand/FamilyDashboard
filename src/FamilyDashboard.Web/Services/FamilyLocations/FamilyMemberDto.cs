namespace FamilyDashboard.Web.Services.FamilyLocations;

/// <summary>
/// One family member as reported by a Home Assistant device tracker. Coordinates are only used by
/// the Family Map widget, which is an explicit opt-in because the dashboard is unauthenticated on the LAN.
/// </summary>
public sealed record FamilyMemberDto(
    string EntityId,
    string Name,
    string Place,
    bool IsHome,
    int? BatteryPercent,
    bool? IsCharging,
    bool? IsDriving,
    DateTime? LastSeenUtc,
    double? Latitude = null,
    double? Longitude = null);

public sealed record FamilyLocationStatus(DateTime CheckedUtc, bool Succeeded, string? Message);
