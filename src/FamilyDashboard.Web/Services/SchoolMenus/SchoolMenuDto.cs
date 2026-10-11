namespace FamilyDashboard.Web.Services.SchoolMenus;

/// <summary>A menu a district publishes, such as "High School Lunch Menu 2026-2027".</summary>
public sealed record SchoolMenuType(string Id, string Name);

/// <summary>
/// One line on a day's menu. Labels are the menu's own connecting text ("or", "Choose 1 or 2");
/// entrées are the lines before the first "Choose" label.
/// </summary>
public sealed record SchoolMenuItem(string Name, bool IsLabel, bool IsEntree);

public sealed record SchoolMenuDay(DateOnly Date, IReadOnlyList<SchoolMenuItem> Items);

/// <summary>The published days of a menu for the current and next month.</summary>
public sealed record SchoolMenuDto(string MenuTypeId, string Name, IReadOnlyList<SchoolMenuDay> Days, DateTime FetchedUtc);
