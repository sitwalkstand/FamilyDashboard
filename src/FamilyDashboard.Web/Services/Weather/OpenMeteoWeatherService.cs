using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FamilyDashboard.Web.Services.Weather;

public partial class OpenMeteoWeatherService(HttpClient httpClient, ILogger<OpenMeteoWeatherService> logger) : IWeatherService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> UsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California",
        ["CO"] = "Colorado", ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "District of Columbia",
        ["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois",
        ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas", ["KY"] = "Kentucky", ["LA"] = "Louisiana",
        ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota",
        ["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada",
        ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York",
        ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma", ["OR"] = "Oregon",
        ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota",
        ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont", ["VA"] = "Virginia",
        ["WA"] = "Washington", ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming",
    };

    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex UsZipCodePattern();

    public async Task<WeatherLocation?> FindLocationAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        // The geocoder only matches a bare place name or postal code, so "Syracuse, NY" is searched as
        // "Syracuse" and the part after the comma picks among the matches by state/region or country.
        var parts = query.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0];
        var qualifier = parts.Length > 1 ? parts[1] : null;
        var zipCode = UsZipCodePattern().IsMatch(name) ? name[..5] : null;

        var url = "https://geocoding-api.open-meteo.com/v1/search" +
                  $"?name={Uri.EscapeDataString(zipCode ?? name)}&count=10&language=en&format=json" +
                  (zipCode is null ? "" : "&countryCode=US");
        var response = await httpClient.GetFromJsonAsync<GeocodingResponse>(url, JsonOptions, cancellationToken);
        var results = response?.Results ?? [];
        var result = qualifier is null
            ? results.FirstOrDefault()
            : results.FirstOrDefault(candidate => MatchesQualifier(candidate, qualifier)) ?? results.FirstOrDefault();
        if (result is null)
        {
            return null;
        }

        var region = zipCode is null ? result.Admin1 : $"{result.Admin1} {zipCode}".Trim();
        var displayName = string.Join(", ", new[] { result.Name, region, result.Country }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new WeatherLocation(displayName, result.Latitude, result.Longitude);
    }

    private static bool MatchesQualifier(GeocodingResult result, string qualifier)
    {
        var region = UsStates.GetValueOrDefault(qualifier, qualifier);
        return string.Equals(result.Admin1, region, StringComparison.OrdinalIgnoreCase)
               || string.Equals(result.Country, qualifier, StringComparison.OrdinalIgnoreCase)
               || string.Equals(result.CountryCode, qualifier, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<WeatherDto?> GetForecastAsync(WeatherLocationKey location, CancellationToken cancellationToken = default)
    {
        var imperial = location.IsImperial;
        var url = "https://api.open-meteo.com/v1/forecast" +
                   $"?latitude={location.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={location.Longitude.ToString(CultureInfo.InvariantCulture)}" +
                   "&current=temperature_2m,apparent_temperature,weather_code,is_day,relative_humidity_2m,pressure_msl,wind_speed_10m,wind_direction_10m,uv_index,visibility" +
                   "&hourly=temperature_2m,weather_code,is_day,precipitation,precipitation_probability" +
                   "&daily=temperature_2m_max,temperature_2m_min,weather_code,sunrise,sunset,precipitation_sum,precipitation_probability_max" +
                   $"&temperature_unit={location.TemperatureUnit}" +
                   $"&wind_speed_unit={(imperial ? "mph" : "kmh")}&precipitation_unit={(imperial ? "inch" : "mm")}" +
                   // Extra hours so an hourly forecast still reaches 12 hours ahead when the data is a refresh interval old.
                   "&forecast_days=7&forecast_hours=48&timezone=auto";

        try
        {
            var response = await httpClient.GetFromJsonAsync<OpenMeteoResponse>(url, JsonOptions, cancellationToken);
            if (response is null)
            {
                return null;
            }

            var forecast = new List<DailyForecastDto>();
            var daily = response.Daily;
            for (var i = 0; i < daily.Time.Count; i++)
            {
                forecast.Add(new DailyForecastDto(
                    Date: DateOnly.Parse(daily.Time[i], CultureInfo.InvariantCulture),
                    High: daily.TempMax[i],
                    Low: daily.TempMin[i],
                    WeatherCode: daily.WeatherCode[i])
                {
                    PrecipitationAmount = daily.PrecipitationSum.ElementAtOrDefault(i),
                    PrecipitationChance = daily.PrecipitationProbabilityMax.ElementAtOrDefault(i),
                    Sunrise = ParseLocalTime(daily.Sunrise.ElementAtOrDefault(i)),
                    Sunset = ParseLocalTime(daily.Sunset.ElementAtOrDefault(i)),
                });
            }

            var offset = TimeSpan.FromSeconds(response.UtcOffsetSeconds);
            var hourly = new List<HourlyForecastDto>();
            for (var i = 0; i < response.Hourly.Time.Count; i++)
            {
                if (ParseLocalTime(response.Hourly.Time[i]) is not { } time
                    || response.Hourly.Temperature2m.ElementAtOrDefault(i) is not { } temperature)
                {
                    continue;
                }

                hourly.Add(new HourlyForecastDto(
                    Time: new DateTimeOffset(time, offset),
                    Temperature: temperature,
                    WeatherCode: response.Hourly.WeatherCode.ElementAtOrDefault(i) ?? 0,
                    IsDay: response.Hourly.IsDay.ElementAtOrDefault(i) != 0,
                    PrecipitationAmount: response.Hourly.Precipitation.ElementAtOrDefault(i),
                    PrecipitationChance: response.Hourly.PrecipitationProbability.ElementAtOrDefault(i)));
            }

            var current = response.Current;
            return new WeatherDto(
                CurrentTemp: current.Temperature2m,
                WeatherCode: current.WeatherCode,
                TodayHigh: forecast.Count > 0 ? forecast[0].High : current.Temperature2m,
                TodayLow: forecast.Count > 0 ? forecast[0].Low : current.Temperature2m,
                Forecast: forecast)
            {
                FeelsLike = current.ApparentTemperature,
                IsDay = current.IsDay != 0,
                Humidity = current.RelativeHumidity2m,
                // Pressure is always hPa; visibility comes back in feet or meters depending on the other units requested.
                Pressure = current.PressureMsl * (imperial ? 0.02953 : 1),
                WindSpeed = current.WindSpeed10m,
                WindDirection = current.WindDirection10m,
                UvIndex = current.UvIndex,
                Visibility = current.Visibility * (response.CurrentUnits.Visibility == "ft" ? 0.3048 : 1) / (imperial ? 1609.344 : 1000),
                Hourly = hourly,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch weather forecast");
            return null;
        }
    }

    private static DateTime? ParseLocalTime(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private class OpenMeteoResponse
    {
        [JsonPropertyName("utc_offset_seconds")]
        public int UtcOffsetSeconds { get; set; }

        [JsonPropertyName("current")]
        public CurrentBlock Current { get; set; } = new();

        [JsonPropertyName("current_units")]
        public CurrentUnitsBlock CurrentUnits { get; set; } = new();

        [JsonPropertyName("hourly")]
        public HourlyBlock Hourly { get; set; } = new();

        [JsonPropertyName("daily")]
        public DailyBlock Daily { get; set; } = new();
    }

    private class GeocodingResponse
    {
        [JsonPropertyName("results")]
        public List<GeocodingResult>? Results { get; set; }
    }

    private class GeocodingResult
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("admin1")]
        public string? Admin1 { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }
    }

    private class CurrentBlock
    {
        [JsonPropertyName("temperature_2m")]
        public double Temperature2m { get; set; }

        [JsonPropertyName("apparent_temperature")]
        public double? ApparentTemperature { get; set; }

        [JsonPropertyName("weather_code")]
        public int WeatherCode { get; set; }

        [JsonPropertyName("is_day")]
        public int IsDay { get; set; } = 1;

        [JsonPropertyName("relative_humidity_2m")]
        public double? RelativeHumidity2m { get; set; }

        [JsonPropertyName("pressure_msl")]
        public double? PressureMsl { get; set; }

        [JsonPropertyName("wind_speed_10m")]
        public double? WindSpeed10m { get; set; }

        [JsonPropertyName("wind_direction_10m")]
        public double? WindDirection10m { get; set; }

        [JsonPropertyName("uv_index")]
        public double? UvIndex { get; set; }

        [JsonPropertyName("visibility")]
        public double? Visibility { get; set; }
    }

    private class CurrentUnitsBlock
    {
        [JsonPropertyName("visibility")]
        public string? Visibility { get; set; }
    }

    private class HourlyBlock
    {
        [JsonPropertyName("time")]
        public List<string> Time { get; set; } = [];

        [JsonPropertyName("temperature_2m")]
        public List<double?> Temperature2m { get; set; } = [];

        [JsonPropertyName("weather_code")]
        public List<int?> WeatherCode { get; set; } = [];

        [JsonPropertyName("is_day")]
        public List<int?> IsDay { get; set; } = [];

        [JsonPropertyName("precipitation")]
        public List<double?> Precipitation { get; set; } = [];

        [JsonPropertyName("precipitation_probability")]
        public List<double?> PrecipitationProbability { get; set; } = [];
    }

    private class DailyBlock
    {
        [JsonPropertyName("time")]
        public List<string> Time { get; set; } = [];

        [JsonPropertyName("temperature_2m_max")]
        public List<double> TempMax { get; set; } = [];

        [JsonPropertyName("temperature_2m_min")]
        public List<double> TempMin { get; set; } = [];

        [JsonPropertyName("weather_code")]
        public List<int> WeatherCode { get; set; } = [];

        [JsonPropertyName("sunrise")]
        public List<string?> Sunrise { get; set; } = [];

        [JsonPropertyName("sunset")]
        public List<string?> Sunset { get; set; } = [];

        [JsonPropertyName("precipitation_sum")]
        public List<double?> PrecipitationSum { get; set; } = [];

        [JsonPropertyName("precipitation_probability_max")]
        public List<double?> PrecipitationProbabilityMax { get; set; } = [];
    }
}
