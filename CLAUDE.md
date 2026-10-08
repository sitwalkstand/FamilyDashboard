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
- Weather location/unit/refresh are configured per Weather widget (columns on `Widgets`, edited in the widget's admin edit window). `WeatherRefreshWorker` fetches one forecast per distinct location/unit (`WeatherLocationKey`) from Open-Meteo (no API key). The old `Settings` weather columns are unmapped and only read once to seed existing widgets.
- Calendar icons use Font Awesome Free, vendored in `wwwroot/lib/fontawesome`; the icon picker list comes from `Components/Widgets/fontawesome-free-icons.json`, compiled as an embedded resource.

`/admin` is unauthenticated by design (trusted home LAN).
