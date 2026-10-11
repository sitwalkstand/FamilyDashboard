namespace FamilyDashboard.Web.Services.SchoolMenus;

public interface ISchoolMenuService
{
    /// <summary>Lists the menus a district publishes on its website, across all its school levels.</summary>
    Task<IReadOnlyList<SchoolMenuType>> GetMenuTypesAsync(string organizationId, CancellationToken cancellationToken = default);

    /// <summary>Fetches the menu's days for the month containing <paramref name="today"/> and the month after.</summary>
    Task<SchoolMenuDto?> GetMenuAsync(string menuTypeId, DateOnly today, CancellationToken cancellationToken = default);
}
