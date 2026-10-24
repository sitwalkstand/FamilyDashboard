using System.Globalization;

namespace FamilyDashboard.Web.Services.HomeStatus;

/// <summary>How a tile shows an entity: readable text, a Font Awesome icon class, and a tone
/// ("", "warn", "alert" or "off") the widget uses for color.</summary>
public sealed record HomeTileView(string Text, string Icon, string Tone);

/// <summary>
/// Turns raw Home Assistant states into the words a family reads ("Wet", "Armed home", "74°")
/// and picks icons from the vendored Font Awesome Free set.
/// </summary>
public static class HomeStateFormatter
{
    private static readonly HashSet<string> SafetyClasses = ["moisture", "smoke", "carbon_monoxide", "gas"];
    private static readonly HashSet<string> OpeningClasses = ["door", "window", "garage_door", "opening"];

    public static HomeTileView Describe(HomeEntityDto entity)
    {
        var icon = Icon(entity);
        if (entity.State is "unavailable")
        {
            return new HomeTileView("Offline", icon, "off");
        }
        if (entity.State is "unknown")
        {
            return new HomeTileView("Unknown", icon, "off");
        }

        var on = entity.State == "on";
        return entity.Domain switch
        {
            "person" or "device_tracker" => new(entity.State switch { "home" => "Home", "not_home" => "Away", _ => entity.State }, icon, ""),
            "alarm_control_panel" => new(Humanize(entity.State.Replace("armed_", "armed ")), icon, entity.State == "triggered" ? "alert" : ""),
            "lock" => new(Humanize(entity.State), icon, entity.State switch { "unlocked" or "open" => "warn", "jammed" => "alert", _ => "" }),
            "cover" => new(Humanize(entity.State), icon, entity.State == "open" && entity.DeviceClass == "garage" ? "warn" : ""),
            "climate" => new(Climate(entity), icon, ""),
            "binary_sensor" => entity.DeviceClass switch
            {
                "moisture" => new(on ? "Wet" : "Dry", icon, on ? "alert" : ""),
                { } safety when SafetyClasses.Contains(safety) => new(on ? "Detected" : "Clear", icon, on ? "alert" : ""),
                { } opening when OpeningClasses.Contains(opening) => new(on ? "Open" : "Closed", icon, on ? "warn" : ""),
                "motion" or "occupancy" or "presence" => new(on ? "Detected" : "Clear", icon, ""),
                "battery" => new(on ? "Low" : "Normal", icon, on ? "warn" : ""),
                "connectivity" => new(on ? "Connected" : "Disconnected", icon, on ? "" : "warn"),
                _ => new(on ? "On" : "Off", icon, "")
            },
            "sensor" => new(Sensor(entity), icon, entity.DeviceClass == "battery" && Number(entity.State) < 20 ? "warn" : ""),
            _ => new(Humanize(entity.State), icon, "")
        };
    }

    /// <summary>A Font Awesome icon for an attention item's Material Design icon, falling back to one per severity.</summary>
    public static string AttentionIcon(AttentionItemDto item)
    {
        var name = item.Icon?.Replace("mdi:", "") ?? "";
        return name switch
        {
            _ when name.Contains("water") || name.Contains("leak") => "fa-solid fa-droplet",
            _ when name.Contains("smoke") || name.Contains("fire") => "fa-solid fa-fire",
            _ when name.Contains("shield") => "fa-solid fa-shield-halved",
            _ when name.Contains("thermometer") => "fa-solid fa-temperature-half",
            _ when name.Contains("snowflake") => "fa-solid fa-snowflake",
            _ when name.Contains("battery") => "fa-solid fa-battery-quarter",
            _ when name.Contains("lock-open") => "fa-solid fa-lock-open",
            _ when name.Contains("garage") => "fa-solid fa-warehouse",
            _ when name.Contains("door") => "fa-solid fa-door-open",
            _ when name.Contains("disconnect") || name.Contains("offline") => "fa-solid fa-plug-circle-xmark",
            _ when name.Contains("check") => "fa-solid fa-circle-check",
            _ => item.Severity switch
            {
                "alert" => "fa-solid fa-triangle-exclamation",
                "warning" => "fa-solid fa-circle-exclamation",
                _ => "fa-solid fa-circle-info"
            }
        };
    }

    private static string Icon(HomeEntityDto entity) => (entity.Domain, entity.DeviceClass) switch
    {
        ("person" or "device_tracker", _) => "fa-solid fa-user",
        ("alarm_control_panel", _) => "fa-solid fa-shield-halved",
        ("lock", _) => entity.State == "locked" ? "fa-solid fa-lock" : "fa-solid fa-lock-open",
        ("cover", "garage") => "fa-solid fa-warehouse",
        ("climate", _) => "fa-solid fa-temperature-half",
        ("light", _) => "fa-solid fa-lightbulb",
        ("fan", _) => "fa-solid fa-fan",
        ("switch" or "input_boolean", _) => "fa-solid fa-toggle-on",
        (_, "moisture") => "fa-solid fa-house-flood-water",
        (_, "smoke" or "gas" or "carbon_monoxide") => "fa-solid fa-fire",
        (_, "door" or "garage_door" or "opening") => entity.State == "on" ? "fa-solid fa-door-open" : "fa-solid fa-door-closed",
        (_, "window") => "fa-solid fa-window-maximize",
        (_, "motion" or "occupancy" or "presence") => "fa-solid fa-person-walking",
        (_, "temperature") => "fa-solid fa-temperature-half",
        (_, "humidity") => "fa-solid fa-droplet",
        (_, "battery") => Number(entity.State) < 20 ? "fa-solid fa-battery-quarter" : "fa-solid fa-battery-half",
        (_, "power" or "energy") => "fa-solid fa-bolt",
        (_, "connectivity") => "fa-solid fa-wifi",
        _ => "fa-solid fa-house"
    };

    private static string Climate(HomeEntityDto entity)
    {
        var mode = Humanize(entity.State);
        return entity.CurrentTemperature is { } current
            ? $"{mode} · {Math.Round(current):0}°"
            : mode;
    }

    private static string Sensor(HomeEntityDto entity)
    {
        if (!double.TryParse(entity.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return Humanize(entity.State);
        }

        return entity.DeviceClass switch
        {
            "temperature" => $"{Math.Round(value):0}°",
            "humidity" or "battery" => $"{Math.Round(value):0}%",
            _ when entity.Unit == "%" => $"{Math.Round(value):0}%",
            _ => $"{Math.Round(value, 1):0.#} {entity.Unit}".TrimEnd()
        };
    }

    private static double Number(string state) =>
        double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    // "armed home" -> "Armed home", "not_running" -> "Not running".
    private static string Humanize(string state)
    {
        var text = state.Replace('_', ' ').Trim();
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
