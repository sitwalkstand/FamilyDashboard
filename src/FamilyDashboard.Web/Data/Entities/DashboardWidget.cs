namespace FamilyDashboard.Web.Data.Entities;

public class DashboardWidget
{
    public int Id { get; set; }
    public int DashboardScreenId { get; set; }
    public string WidgetType { get; set; } = "Clock";
    public int DisplayOrder { get; set; }
    public bool Enabled { get; set; } = true;
    public int PositionX { get; set; } = 1;
    public int PositionY { get; set; } = 1;
    public int Width { get; set; } = 6;
    public int Height { get; set; } = 4;
    public string CalendarNames { get; set; } = "";
    public int CalendarWeeks { get; set; } = 5;
    public string PhotoPath { get; set; } = "";
    public double WeatherLatitude { get; set; } = 38.9894;
    public double WeatherLongitude { get; set; } = -77.4794;
    public string WeatherTemperatureUnit { get; set; } = "fahrenheit";
    public int WeatherRefreshMinutes { get; set; } = 30;
    public string WeatherLocationName { get; set; } = "";
    public bool WeatherShowCurrent { get; set; } = true;
    public bool WeatherShowFeelsLike { get; set; }
    public bool WeatherShowSummary { get; set; }
    public bool WeatherShowSunriseSunset { get; set; }
    public bool WeatherShowMoonPhase { get; set; }
    public bool WeatherShowWind { get; set; }
    public bool WeatherShowUvIndex { get; set; }
    public bool WeatherShowHumidity { get; set; }
    public bool WeatherShowPressure { get; set; }
    public bool WeatherShowVisibility { get; set; }
    /// <summary>None, Daily, or Hourly.</summary>
    public string WeatherForecastMode { get; set; } = "Daily";
    /// <summary>Days for a daily forecast, hours for an hourly one.</summary>
    public int WeatherForecastLength { get; set; } = 5;
    public bool WeatherShowPrecipitationAmount { get; set; }
    public bool WeatherShowPrecipitationChance { get; set; }
    public bool WeatherCondensed { get; set; }

    public DashboardScreen? Screen { get; set; }
}
