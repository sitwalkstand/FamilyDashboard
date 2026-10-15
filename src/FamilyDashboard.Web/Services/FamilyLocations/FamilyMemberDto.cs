namespace FamilyDashboard.Web.Services.FamilyLocations;

/// <summary>
/// One family member as reported by a Home Assistant device tracker. Coordinates are deliberately
/// left out: the dashboard is unauthenticated on the LAN, so it only shows the place name.
/// </summary>
public sealed record FamilyMemberDto(
    string EntityId,
    string Name,
    string Place,
    bool IsHome,
    int? BatteryPercent,
    bool? IsCharging,
    bool? IsDriving,
    DateTime? LastSeenUtc);

public sealed record FamilyLocationStatus(DateTime CheckedUtc, bool Succeeded, string? Message);
