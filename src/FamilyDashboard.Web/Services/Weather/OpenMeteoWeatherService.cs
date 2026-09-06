using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyDashboard.Web.Services.Weather;

public class OpenMeteoWeatherService(HttpClient httpClient, ILogger<OpenMeteoWeatherService> logger) : IWeatherService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WeatherDto?> GetForecastAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        var url = "https://api.open-meteo.com/v1/forecast" +
                   $"?latitude={latitude}&longitude={longitude}" +
                   "&current=temperature_2m,weather_code" +
                   "&daily=temperature_2m_max,temperature_2m_min,weather_code" +
                   "&temperature_unit=fahrenheit&forecast_days=7&timezone=auto";

        try
        {
            var response = await httpClient.GetFromJsonAsync<OpenMeteoResponse>(url, JsonOptions, cancellationToken);
            if (response is null)
            {
                return null;
            }

            var forecast = new List<DailyForecastDto>();
            for (var i = 0; i < response.Daily.Time.Count; i++)
            {
                forecast.Add(new DailyForecastDto(
                    Date: DateOnly.Parse(response.Daily.Time[i]),
                    HighF: response.Daily.TempMax[i],
                    LowF: response.Daily.TempMin[i],
                    WeatherCode: response.Daily.WeatherCode[i]));
            }

            return new WeatherDto(
                CurrentTempF: response.Current.Temperature2m,
                WeatherCode: response.Current.WeatherCode,
                TodayHighF: forecast.Count > 0 ? forecast[0].HighF : response.Current.Temperature2m,
                TodayLowF: forecast.Count > 0 ? forecast[0].LowF : response.Current.Temperature2m,
                Forecast: forecast);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch weather forecast");
            return null;
        }
    }

    private class OpenMeteoResponse
    {
        [JsonPropertyName("current")]
        public CurrentBlock Current { get; set; } = new();

        [JsonPropertyName("daily")]
        public DailyBlock Daily { get; set; } = new();
    }

    private class CurrentBlock
    {
        [JsonPropertyName("temperature_2m")]
        public double Temperature2m { get; set; }

        [JsonPropertyName("weather_code")]
        public int WeatherCode { get; set; }
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
    }
}
