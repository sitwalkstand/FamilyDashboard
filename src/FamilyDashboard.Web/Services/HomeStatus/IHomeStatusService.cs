namespace FamilyDashboard.Web.Services.HomeStatus;

public interface IHomeStatusService
{
    /// <summary>True when a Home Assistant URL and access token are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Lists every entity Home Assistant knows about. Throws when Home Assistant can't be reached or
    /// rejects the token, so callers can tell "no data" from "no entities".
    /// </summary>
    Task<IReadOnlyList<HomeEntityDto>> GetEntitiesAsync(CancellationToken cancellationToken = default);
}
