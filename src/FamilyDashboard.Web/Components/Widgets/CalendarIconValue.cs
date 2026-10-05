namespace FamilyDashboard.Web.Components.Widgets;

public static class CalendarIconValue
{
    public static string Create(string style, string name) => style switch
    {
        "Regular" => $"regular:{name}",
        "Brands" => $"brands:{name}",
        _ => name
    };

    public static string GetCssClass(string? value)
    {
        var (family, name) = Parse(value);
        return $"{family} fa-{name}";
    }

    private static (string Family, string Name) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ("fa-solid", "calendar-days");
        }

        var separatorIndex = value.IndexOf(':');
        if (separatorIndex > 0)
        {
            var family = value[..separatorIndex] switch
            {
                "regular" => "fa-regular",
                "brands" => "fa-brands",
                "solid" => "fa-solid",
                _ => null
            };
            if (family is not null && separatorIndex < value.Length - 1)
            {
                return (family, value[(separatorIndex + 1)..]);
            }
        }

        return ("fa-solid", value);
    }
}