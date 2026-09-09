using System.Globalization;
using System.Security.Cryptography;
using FamilyDashboard.Web.Data;
using FamilyDashboard.Web.Data.Entities;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using GoogleCalendarApi = Google.Apis.Calendar.v3.CalendarService;

namespace FamilyDashboard.Web.Services.Calendar;

public sealed class GoogleCalendarService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration,
    ILogger<GoogleCalendarService> logger) : IGoogleCalendarService
{
    private static readonly string CalendarScope = Google.Apis.Calendar.v3.CalendarService.Scope.CalendarReadonly;
    private const string StatePurpose = "FamilyDashboard.GoogleCalendar.OAuthState";
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(StatePurpose);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["Google:ClientId"]) &&
        !string.IsNullOrWhiteSpace(configuration["Google:ClientSecret"]);

    public string CreateAuthorizationUrl(string redirectUri)
    {
        EnsureConfigured();
        var state = protector.Protect($"{Guid.NewGuid():N}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        var flow = CreateFlow();
        var request = flow.CreateAuthorizationCodeRequest(redirectUri);
        // The Google client library already adds access_type=offline.
        return $"{request.Build()}&prompt=consent&state={Uri.EscapeDataString(state)}";
    }

    public async Task CompleteAuthorizationAsync(string code, string redirectUri, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var flow = CreateFlow();
        var token = await flow.ExchangeCodeForTokenAsync("family-dashboard", code, redirectUri, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidOperationException("Google did not return a refresh token. Revoke the app's access in Google, then connect again.");
        }

        var credential = new UserCredential(flow, "family-dashboard", token);
        var calendarApi = CreateCalendarApi(credential);
        var profile = await calendarApi.CalendarList.List().ExecuteAsync(cancellationToken);
        var email = profile.Items?.FirstOrDefault(item => string.Equals(item.Id, "primary", StringComparison.OrdinalIgnoreCase))?.Summary
            ?? "Google Calendar";

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.Set<GoogleCalendarConnection>().SingleOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            connection = new GoogleCalendarConnection();
            db.Add(connection);
        }

        connection.Email = email;
        connection.ProtectedRefreshToken = protector.Protect(token.RefreshToken);
        connection.ConnectedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GoogleCalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var api = await GetCalendarApiAsync(cancellationToken);
        var result = await api.CalendarList.List().ExecuteAsync(cancellationToken);
        return result.Items?.Select(item => new GoogleCalendarInfo(
            item.Id,
            item.SummaryOverride ?? item.Summary ?? item.Id,
            item.Description,
            item.BackgroundColor)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<CalendarEventDto>> GetEventsAsync(
        CalendarFeed feed,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feed.ExternalId))
        {
            return [];
        }

        var api = await GetCalendarApiAsync(cancellationToken);
        var request = api.Events.List(feed.ExternalId);
        request.TimeMinDateTimeOffset = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Local)).ToUniversalTime();
        request.TimeMaxDateTimeOffset = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Local)).ToUniversalTime();
        request.SingleEvents = true;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
        var events = await request.ExecuteAsync(cancellationToken);

        return events.Items?.Where(item => item.Status != "cancelled").Select(item =>
        {
            var googleStart = item.Start ?? throw new InvalidOperationException("Google returned an event without a start time.");
            var isAllDay = googleStart.Date is not null;
            var eventStart = isAllDay
                ? DateTimeOffset.Parse(googleStart.Date!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)
                : googleStart.DateTimeDateTimeOffset ?? new DateTimeOffset(start, TimeSpan.Zero);
            var eventEnd = isAllDay
                ? DateTimeOffset.Parse(item.End?.Date ?? googleStart.Date!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)
                : item.End?.DateTimeDateTimeOffset ?? eventStart;
            return new CalendarEventDto(
                item.Summary ?? "(untitled event)",
                eventStart,
                eventEnd,
                isAllDay,
                feed.DisplayName,
                feed.Color);
        }).ToList() ?? [];
    }

    public string ValidateState(string state)
    {
        var value = protector.Unprotect(state);
        var parts = value.Split('|');
        if (parts.Length != 2 || !long.TryParse(parts[1], out var issuedAt) ||
            DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(issuedAt) > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException("The Google authorization request expired.");
        }

        return parts[0];
    }

    private async Task<GoogleCalendarApi> GetCalendarApiAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.Set<GoogleCalendarConnection>().SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Google Calendar is not connected.");
        var refreshToken = protector.Unprotect(connection.ProtectedRefreshToken);
        var flow = CreateFlow();
        var token = new TokenResponse { RefreshToken = refreshToken };
        return CreateCalendarApi(new UserCredential(flow, "family-dashboard", token));
    }

    private GoogleAuthorizationCodeFlow CreateFlow() => new(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = new ClientSecrets
        {
            ClientId = configuration["Google:ClientId"],
            ClientSecret = configuration["Google:ClientSecret"]
        },
        Scopes = [CalendarScope],
        DataStore = new NullDataStore()
    });

    private static GoogleCalendarApi CreateCalendarApi(UserCredential credential) => new(new BaseClientService.Initializer
    {
        HttpClientInitializer = credential,
        ApplicationName = "FamilyDashboard"
    });

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Google Calendar is not configured. Set Google:ClientId and Google:ClientSecret.");
            throw new InvalidOperationException("Google Calendar is not configured.");
        }
    }

    private sealed class NullDataStore : IDataStore
    {
        public Task ClearAsync() => Task.CompletedTask;
        public Task DeleteAsync<T>(string key) => Task.CompletedTask;
        public Task<T?> GetAsync<T>(string key) => Task.FromResult<T?>(default);
        public Task StoreAsync<T>(string key, T value) => Task.CompletedTask;
    }

    private static readonly Type GoogleCalendarApi = typeof(Google.Apis.Calendar.v3.CalendarService);
}
