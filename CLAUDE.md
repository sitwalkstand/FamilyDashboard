# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Self-hosted, DakBoard-style kitchen dashboard (calendar, weather, photo slideshow) built on ASP.NET Core Blazor Server, .NET 10. Single project: `src/FamilyDashboard.Web`. There is no test project.

## Commands

```bash
# Build / run (from src/FamilyDashboard.Web)
dotnet build
dotnet run                     # dashboard at /, admin/editor at /admin

# Local run with custom storage paths (relative paths resolve against the build output dir, not the cwd)
DataDirectory=./data PhotoDirectory=./photos dotnet run

# Docker (dashboard + Caddy reverse proxy)
PHOTO_FOLDER=/path/to/photos docker compose up -d --build
```

## Deployment

Pushing to `main` triggers `.github/workflows/deploy.yml` on a self-hosted runner: builds the root `Dockerfile`, pushes `ghcr.io/sitwalkstand/familydashboard:latest`, then SSHes to the home LXC host to `git pull` and `docker compose up -d`. `docker-compose.yml` pulls the GHCR image (it does not build the dashboard locally); Caddy (`caddy/Dockerfile`, Cloudflare DNS plugin) terminates TLS using the `Caddyfile` and proxies to `dashboard:80`. Forwarded headers are trusted from any proxy (`KnownProxies`/`KnownIPNetworks` cleared in `Program.cs`) so OAuth redirect URIs get the right scheme/host.

## Architecture

**Live updates without a custom hub.** Background workers (`Workers/`) poll external sources on a `PeriodicTimer` and push results into the singleton `DashboardStateService`, which raises `CalendarChanged` / `WeatherChanged` / `PhotosChanged`. Widgets subscribe in `OnInitialized` and call `StateHasChanged()`; the Blazor Server circuit pushes the re-render. Widgets don't fetch data themselves — add new data sources as worker → state service → widget.

**Screens and widgets are data-driven.** `Dashboard.razor` loads enabled `DashboardScreen`s (with `DashboardWidget`s) from SQLite, rotates through them using each screen's `DurationSeconds`, and renders widgets by switching on the `WidgetType` string (`Clock`, `Weather`, `Calendar`, `Photos`). Widgets are placed on a 12-column CSS grid via `PositionX/PositionY/Width/Height`. `Admin.razor` is the editor (feeds, Google connection, screens, drag/resize layout using the JS helpers in `wwwroot/js/dashboard-editor.js`, photo management). Adding a widget type means touching the switch in `Dashboard.razor`, the admin editor, and possibly new per-widget columns.

**Schema management is hand-rolled — no EF migrations.** On startup `Program.cs` calls `EnsureCreated()` and then runs idempotent raw SQL: `CREATE TABLE IF NOT EXISTS` and `pragma_table_info` checks followed by `ALTER TABLE ... ADD COLUMN ... DEFAULT ...`. When adding a property to an entity, you must also add the matching column-upgrade case in `Program.cs`, or existing deployed databases will break. The same block also migrates legacy data (emoji calendar icons → Font Awesome names, legacy widget layouts) and seeds three default screens when none exist.

**Calendar sources.** `CalendarFeed.SourceType` is `Ics` (fetched via `IcsUrl` with Ical.Net) or `Google` (fetched via the Google Calendar API using `ExternalId` as the calendar id). `CalendarService` handles both; Google feeds are skipped if no `GoogleCalendarConnection` exists, and expired/undecryptable tokens are logged rather than thrown. The Google OAuth flow lives in minimal-API endpoints `/auth/google/start` and `/auth/google/callback` in `Program.cs`; refresh tokens are stored encrypted with ASP.NET Data Protection (keys persisted in `<DataDirectory>/Keys`). The calendar `HttpClient` sets an explicit User-Agent and HTTP/1.1 because some providers (Outlook) reject requests otherwise.

**Configuration and storage.**
- `DataDirectory` holds `dashboard.db` (SQLite), `Keys/`, and the optional `google-oauth.json` (git-ignored; loaded as an extra config source with `Google:ClientId`/`Google:ClientSecret`). See `App_Data/google-oauth.example.json`. In Docker this is the `/data` volume.
- `PhotoDirectory` is served as static files under `/photos` and scanned by `PhotoScanWorker`.
- A Photos widget can instead use a public iCloud shared album, configured in its `PhotoSettings` JSON column. `SharedAlbumUrl.TryParse` accepts both link formats: current `photos.icloud.com/shared/album/<id>/` links (Shared Collections, read through CloudKit: `records/resolve` gives an anonymous token and zone, then `records/query`) and legacy `icloud.com/sharedalbum/#<token>` links (`sharedstreams` endpoints; Apple retires these when an album is migrated). `PhotoScanWorker` syncs it through `AppleSharedAlbumClient` (unofficial endpoints) into `<DataDirectory>/AlbumCache/<widgetId>/`, served at `/album-cache` (images only, so the `album.json` manifest stays private). The widget only shows cached files; a failed or empty sync never deletes the cache. EF reads properties missing from the JSON as zero/false, not the class defaults, so new `PhotoWidgetSettings` options need a matching upgrade for existing rows.
- A School Menu widget (`WidgetType` `SchoolMenu`) shows upcoming school meals from School Nutrition and Fitness, the menu host used by Frederick County Public Schools (VA). Its pages refuse to be framed, so `SchoolNutritionAndFitnessMenuService` calls the unofficial GraphQL API those pages use (`api.schoolnutritionandfitness.com/graphql`; months are zero-based). Options live in the `SchoolMenuSettings` JSON column (district `OrganizationId`, chosen `MenuTypeId`, days to show, sides on/off). `SchoolMenuRefreshWorker` fetches the current and next month for each chosen menu every 3 hours. Menu types are per school year, so a new year's menu has to be chosen again in admin.
- A Family Locations widget (`WidgetType` `FamilyLocations`) shows each family member's place and battery from Life360. Life360 has no public API and blocks direct access via Cloudflare, so it is read through a Home Assistant instance running the Life360 integration: `HomeAssistantFamilyLocationService` calls `GET /api/states` and keeps `device_tracker.*` entities. `HomeAssistant:BaseUrl` and `HomeAssistant:Token` (a long-lived access token) come from the git-ignored `<DataDirectory>/home-assistant.json` (see `App_Data/home-assistant.example.json`). `FamilyLocationRefreshWorker` polls every 2 minutes, but only while an enabled widget of this type exists; a failed poll keeps the last locations and the widget shows "Not updating". Options (chosen `EntityIds`, title, battery on/off) live in the `FamilyLocationSettings` JSON column. Family Locations exposes only place names, never coordinates (see Family Map below), because `/admin` and `/` are unauthenticated. The attribute names read from the tracker (`battery_level`, `battery_charging`, `driving`, `place`, `address`, `last_seen`) are from the integration's docs and should be checked against a live instance.
- A Family Map widget (`WidgetType` `FamilyMap`) plots the same Home Assistant trackers on a Leaflet map (vendored in `wwwroot/lib/leaflet`, standard OpenStreetMap tiles from the internet with no API key, inverted to dark in CSS; CARTO's free tiles were dropped once they started requiring a key). It is the only place coordinates are exposed, so adding it is the opt-in: anyone who can open `/` sees exact positions. `wwwroot/js/family-map.js` owns the map: `FamilyMapWidget` creates it once, then pushes members on each `FamilyLocationsChanged` rather than rebuilding it. Its members/title live in the `FamilyMapSettings` JSON column, which reuses `FamilyLocationsWidgetSettings`; `FamilyLocationRefreshWorker` polls while either widget type is enabled.
- A Countdown widget (`WidgetType` `Countdown`) counts down to a date and time with a title, both kept in the `CountdownSettings` JSON column. The end time is entered and stored as Eastern wall-clock time and converted with `DashboardTimeZone`, so it doesn't depend on the container's TZ. It ticks every second on its own timer, and shows "It's here!" once the time has passed.
- Weather location/unit/refresh are configured per Weather widget (columns on `Widgets`, edited in the widget's admin edit window). `WeatherRefreshWorker` fetches one forecast per distinct location/unit (`WeatherLocationKey`) from Open-Meteo (no API key). The old `Settings` weather columns are unmapped and only read once to seed existing widgets.
- Calendar icons use Font Awesome Free, vendored in `wwwroot/lib/fontawesome`; the icon picker list comes from `Components/Widgets/fontawesome-free-icons.json`, compiled as an embedded resource.

`/admin` is unauthenticated by design (trusted home LAN).
