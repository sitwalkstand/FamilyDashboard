namespace FamilyDashboard.Web.Services.FamilyLocations;

public interface IFamilyLocationService
{
    /// <summary>True when a Home Assistant URL and access token are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Lists every device tracker Home Assistant knows about. Throws when Home Assistant can't be
    /// reached or rejects the token, so callers can tell "no data" from "no trackers".
    /// </summary>
    Task<IReadOnlyList<FamilyMemberDto>> GetMembersAsync(CancellationToken cancellationToken = default);
}
