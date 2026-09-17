# Family Dashboard

A self-hosted, DakBoard-style kitchen dashboard: calendar, weather, and a photo
slideshow, built on ASP.NET Core Blazor Server (.NET 10).

> **Note:** this was scaffolded in a sandbox without a .NET SDK or NuGet
> access, so it has **not** been compiled or restored. Run `dotnet restore`
> and `dotnet build` locally as your first step and fix up anything that
> drifted (see "Before you build" below) — the shapes/APIs are current as of
> .NET 10 but exact NuGet patch versions may have moved on.

## Before you build

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. `cd src/FamilyDashboard.Web && dotnet restore` — if `Ical.Net`,
   `Microsoft.EntityFrameworkCore.Sqlite`, or
   `Microsoft.Extensions.Http.Resilience` resolve to different latest
   versions than pinned in the `.csproj`, run
   `dotnet add package <name>` to pick up the current version.
3. `dotnet build`.

## Run locally

```bash
cd src/FamilyDashboard.Web
dotnet run
```

Browse to `http://localhost:5000` (or whatever port the console prints) for
the dashboard, and `http://localhost:5000/admin` to add calendar feeds.
Data defaults to `/data` and photos to `/photos` — override with the
`DataDirectory` / `PhotoDirectory` config keys or environment variables for
local runs (e.g. `DataDirectory=./data PhotoDirectory=./photos dotnet run`).

## Run with Docker (recommended for the always-on device)

```bash
PHOTO_FOLDER=/path/to/your/photos docker compose up -d --build
```

Then point the kitchen display's browser at `http://<server-ip>:8080` in
kiosk/fullscreen mode.

## Adding calendars

Go to `/admin` and paste in an ICS subscription URL:

- **Google Calendar**: Settings → Settings for my calendars → *Integrate
  calendar* → "Secret address in iCal format".
- **Outlook.com**: Calendar settings → Shared calendars → *Publish a
  calendar* → copy the ICS link.
- **iCloud**: Calendar app → Share Calendar → *Public Calendar* → copy the
  `webcal://` link and change the scheme to `https://`.

Calendars refresh on the interval set in `appsettings.json`
(`Calendar:RefreshMinutes`, default 15). Newly added feeds are picked up on
the next scheduled refresh — not instantly — since the worker only re-reads
the feed list from SQLite once per cycle.

### Connecting Google Calendar

Create a Google Cloud OAuth client for a web application, enable the Google
Calendar API, and add this authorized redirect URI:

`http://localhost:5000/auth/google/callback`

In **Google Cloud Console → Google Auth Platform → Audience**, set the app
audience to **External** when connecting a personal Gmail account. If the app
is left as **Internal**, Google returns `403 org_internal` and only accounts in
the owning Google Workspace organization can authorize it. For an External
app still in testing, add the Google account under **Test users**. An Internal
app is appropriate only when every account belongs to the same Workspace
organization.

Create `App_Data/google-oauth.json` beside the local SQLite database:

```json
{
  "Google": {
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret"
  }
}
```

For Docker, place the same file at `/data/google-oauth.json` inside the
persistent `dashboard-data` volume before starting the container. The file is
ignored by Git and is loaded separately from `appsettings.json`.

Open `/admin`, choose **Connect Google Calendar**, authorize the account, and
select the calendars to make available to Calendar widgets. OAuth encryption
keys are stored beside the SQLite database, so back up the `App_Data` folder.

## Project layout

```
src/FamilyDashboard.Web/
  Components/         Razor components (pages, layout, widgets)
  Data/                EF Core DbContext + entities (SQLite)
  Services/            Calendar (Ical.Net), Weather (Open-Meteo), Photos, live-state notifier
  Workers/             BackgroundServices that poll external sources on a timer
Dockerfile             Multi-stage build → single container image
docker-compose.yml     One-command deploy with named volume for the DB + a photo mount
```

## How live updates work

Blazor Server already keeps a persistent SignalR circuit open to each
connected browser tab — so a second, custom SignalR hub would be redundant.
Instead, `DashboardStateService` is a singleton that background workers
update; each widget subscribes to its change event in `OnInitialized` and
calls `StateHasChanged()`. The existing circuit pushes the re-render to the
kitchen display automatically.

## Reasonable next steps

- Swap `db.Database.EnsureCreated()` for real EF Core migrations once the
  schema needs to evolve.
- Add `Polly`-based retry/backoff around the calendar and weather HTTP calls
  (the `Microsoft.Extensions.Http.Resilience` package is already referenced
  for this).
- Trigger an immediate calendar refresh from the admin "Add calendar" button
  instead of waiting for the next timer tick.
- Basic auth (or a shared PIN) in front of `/admin` — it's unauthenticated
  right now, which is fine on a trusted home LAN but worth locking down if
  you ever expose it beyond that.
- `[PersistentState]` (new in .NET 10) on widget fields if you want state to
  survive a brief circuit disconnect/reconnect more gracefully.
