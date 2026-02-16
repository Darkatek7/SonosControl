# SonosControl – Self-Hosted Sonos Automation & Alarm Scheduler
[![Dockerhub](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml/badge.svg)](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml)

> **The all-in-one Blazor Server dashboard for orchestrating your Sonos speakers, radio stations, and Spotify playlists on a fully self-hosted stack.**
>
> Automate wake-up playlists, workday background music, and after-hours silences with a secure .NET 9 control center that you can run anywhere Docker or ASP.NET can live.

[![SonosControl hero screenshot showing the Blazor dashboard](https://github.com/user-attachments/assets/44a9e2a2-dcf2-4bda-bf45-eb289ff34472)](https://github.com/Darkatek7/SonosControl)

---

## Table of Contents
- [Why SonosControl?](#why-sonoscontrol)
- [Feature Highlights](#feature-highlights)
- [Architecture at a Glance](#architecture-at-a-glance)
- [Screenshots](#screenshots)
- [Quick Start](#quick-start)
  - [1. Deploy with Docker Compose](#1-deploy-with-docker-compose)
  - [2. Run Locally with the .NET SDK](#2-run-locally-with-the-net-sdk)
- [Configuration Guide](#configuration-guide)
  - [Environment Variables](#environment-variables)
  - [`config.json` Reference](#configjson-reference)
  - [Daily Scheduling & Automation Rules](#daily-scheduling--automation-rules)
  - [Data Protection Keys & Persistent Storage](#data-protection-keys--persistent-storage)
- [User Management & Security](#user-management--security)
- [Automation Engine](#automation-engine)
- [Integrations](#integrations)
- [Logging & Observability](#logging--observability)
- [Testing & Quality](#testing--quality)
- [Troubleshooting FAQ](#troubleshooting-faq)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)
- [Useful Links](#useful-links)

---

## Why SonosControl?
**SonosControl** was built to solve a recurring problem: keeping a Sonos sound system perfectly aligned with office hours, personal routines, and curated playlists without depending on cloud schedulers. It provides:

- **Self-hosted peace of mind** – own your automation stack end-to-end with SQLite storage and file-based configuration.
- **Enterprise-ready identity** – ASP.NET Core Identity with role-based access, persistent cookies, and registration controls.
- **Musical flexibility** – blend TuneIn stations, Spotify albums/playlists/tracks, or on-demand URLs.
- **Automation that adapts** – per-day overrides, random playback modes, timed sessions, and manual control, all from a modern Blazor interface.

Whether you are outfitting a smart home, synchronizing retail ambience, or crafting the perfect morning routine, SonosControl delivers the orchestration you need.

---

## Feature Highlights
- 🎛️ **Live Sonos Dashboard** – Start, pause, skip tracks, adjust volume, and launch timed playback sessions from a responsive control panel.
- 🗓️ **Granular Scheduling** – Define global start/stop times, select active weekdays, or override every day with custom hours and media preferences.
- 🔁 **Smart Autoplay Options** – Choose exact Spotify URLs or TuneIn streams, or let SonosControl randomize from your curated lists on startup.
- ⏱️ **Timed Playback** – Set play durations with one click; the background service automatically stops the stream when the timer ends.
- 📻 **Station Lookup** – Search the community-driven [Radio Browser](https://www.radio-browser.info/) index, preview stations instantly, and save them to your favorites without leaving the app.
- 🧾 **Audit-Ready Logs** – Capture who changed what and when in a searchable logbook designed for shared environments.
- 👤 **Role-Based Access Control** – Built-in roles (`operator`, `admin`, `superadmin`) let you delegate playback duties while protecting critical settings.
- 🔐 **User Registration Switch** – Toggle self-service onboarding directly from the UI, ideal for organizations that need tight access control.
- 🐳 **Docker-Native Deployment** – Ship as a lightweight container with persistent volumes for configuration, keys, and the SQLite database.
- ✅ **Automated Admin Seeding** – Provide admin credentials via environment variables and SonosControl provisions a secure superuser on launch.
- 🔄 **Reliable Background Service** – The hosted automation service continuously polls your schedule, adapts to changes, and fails gracefully.

---

## Architecture at a Glance
| Layer | Technology | Responsibilities |
|-------|------------|------------------|
| UI | Blazor Server (.NET 9) | Real-time playback controls, configuration forms, station search, admin tooling. |
| API & Hosting | ASP.NET Core | Identity, Razor pages, controllers, background service orchestration, localization (`en-US`, `de-AT`). |
| Data Access | Custom repositories | JSON-backed settings via `SettingsRepo`, Sonos device control via `SonosConnectorRepo`, optional holiday hooks. |
| Persistence | SQLite + JSON | Identity + logs stored in `Data/app.db`; automation settings live in `Data/config.json`. |
| Sonos Integration | [ByteDev.Sonos](https://www.nuget.org/packages/ByteDev.Sonos) | SOAP/UPnP control for playback, volume, queue, and track metadata. |
| Front-end Styling | Bootstrap 5, Font Awesome | Dark theme responsive layouts optimized for desktop & mobile usage. |

---

## Screenshots
| Dashboard | Configuration | Station Lookup |
|-----------|---------------|----------------|
| ![Dashboard view](https://github.com/user-attachments/assets/44a9e2a2-dcf2-4bda-bf45-eb289ff34472) | ![Configuration view](https://github.com/user-attachments/assets/0cc1ec9c-f97f-4c4e-a9ea-1379155af675) | ![Station lookup view](https://github.com/user-attachments/assets/170d5aeb-a887-4e93-be20-f66020cad955) |

---

## Quick Start
### 1. Deploy with Docker Compose
Create a `docker-compose.yml` alongside a persistent `Data/` folder:

```yaml
docker compose up -d
```

<details>
<summary>Full docker-compose example</summary>

```yaml
version: '3.4'
services:
  sonos:
    container_name: sonos
    image: darkatek7/sonoscontrol:latest
    ports:
      - 8080:8080
    restart: unless-stopped
    environment:
      - TZ=Europe/Vienna
      - ADMIN_USERNAME=admin
      - ADMIN_EMAIL=admin@example.com
      - ADMIN_PASSWORD=xIlQuKfFjeJfUsfm
    volumes:
      - ./Data:/app/Data
      # persist data-protection keys
      - ./dpkeys:/root/.aspnet/DataProtection-Keys
```
</details>

> ℹ️ SonosControl seeds an administrator using the three `ADMIN_` variables. Startup fails if any are missing or the password is too weak, ensuring secure deployments from day one.

Once the container is healthy, open [http://localhost:8080](http://localhost:8080), sign in with the seeded credentials, and start curating your schedules.

### 2. Run Locally with the .NET SDK
Prefer to develop or preview without Docker?

```bash
dotnet restore
cd SonosControl.Web
dotnet ef database update   # optional, migrations run automatically at runtime
DOTNET_ENVIRONMENT=Development dotnet run
```

The app listens on `https://localhost:5001` (HTTPS) and `http://localhost:5000` (HTTP) by default. Log in with the admin account configured via environment variables or `appsettings.Development.json`.

---

## Configuration Guide
### Environment Variables
| Name | Purpose | Notes |
|------|---------|-------|
| `ADMIN_USERNAME` | Seeded admin username | Required on first run if no user exists. |
| `ADMIN_EMAIL` | Seeded admin email | Used for password reset workflows and audit logs. |
| `ADMIN_PASSWORD` | Seeded admin password | Must satisfy Identity password validators. |
| `TZ` | Container timezone | Keeps schedules aligned with local time. |
| `ConnectionStrings__DefaultConnection` | Override SQLite path/connection | Optional; defaults to `Data/app.db`. |
| `DataProtection__KeysDirectory` | Persist ASP.NET Core Data Protection keys | Defaults to `./DataProtectionKeys` inside the container/host. |

> Tip: In development you can store admin credentials under the `Admin` section of `appsettings.Development.json`.

### `config.json` Reference
Sonos automation rules live in `Data/config.json`. A full example:

```json
{
  "Volume": 15,
  "StartTime": "06:45:00",
  "StopTime": "17:30:00",
  "IP_Adress": "10.0.0.1",
  "Stations": [
    { "Name": "Antenne Vorarlberg", "Url": "web.radio.antennevorarlberg.at/av-live/stream/mp3" },
    { "Name": "Radio V", "Url": "orf-live.ors-shoutcast.at/vbg-q2a" }
  ],
  "SpotifyTracks": [
    { "Name": "Top 50 Global", "Url": "https://open.spotify.com/playlist/37i9dQZEVXbMDoHDwVN2tF" },
    { "Name": "Astroworld", "Url": "https://open.spotify.com/album/41GuZcammIkupMPKH2OJ6I" }
  ],
  "AutoPlayStationUrl": null,
  "AutoPlaySpotifyUrl": null,
  "AutoPlayRandomStation": false,
  "AutoPlayRandomSpotify": false,
  "DailySchedules": {},
  "ActiveDays": [ "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" ],
  "AllowUserRegistration": true
}
```

The UI keeps this file synchronized; manual edits are optional but handy for infrastructure automation.

### Daily Scheduling & Automation Rules
- **Global defaults:** Set the Sonos IP, volume, start, and stop times under *Configuration → Speaker Settings*.
- **Active days:** Choose which weekdays should respect the default automation. SonosControl pre-selects Monday–Friday on first run.
- **Per-day overrides:** Each day can have a unique start/stop window and playback source (specific station, Spotify link, or random mode).
- **Random playback:** Enable *Random Station* or *Random Spotify* for either the default automation or individual days; SonosControl chooses from the saved lists.
- **Timed playback:** Launch ad-hoc sessions from the dashboard. The hosted service cancels playback after the timer elapses and logs the action.

### Data Protection Keys & Persistent Storage
- **`Data/` volume:** Stores `config.json` and `app.db` (Identity, logs, schedules). Back it up to retain history and user accounts.
- **Data protection keys:** Persist `/root/.aspnet/DataProtection-Keys` (Docker) or the directory specified by `DataProtection:KeysDirectory` to keep login cookies valid across restarts or multi-instance deployments.

---

## User Management & Security
- 🔐 **Identity Integration:** ASP.NET Core Identity with SQLite persistence, password validators, and long-lived cookies.
- 🧑‍🤝‍🧑 **Roles:**
  - `superadmin` – manage roles, toggle registration, delete any user.
  - `admin` – adjust schedules, speaker IP, and review logs.
  - `operator` – control playback without touching critical settings.
- 📝 **Audit Trail:** Every configuration change or playback event is logged with timestamp and username for accountability.
- 🚫 **Registration Toggle:** Restrict sign-ups to invited accounts when running in shared spaces.

---

## Automation Engine
A dedicated `BackgroundService` continually evaluates your configuration:
1. Loads the latest `config.json` via the thread-safe `SettingsRepo` (JSON serialized with atomic writes).
2. Calculates the next start window, respecting per-day overrides and active weekdays.
3. Initiates playback via the Sonos UPnP APIs (direct SOAP calls for TuneIn streams, Spotify playback helpers, queue management).
4. Applies your selected autoplay behavior (specific URL or random selection).
5. Stops playback at the configured end time, even accounting for schedule edits made while running.

The engine polls settings at most once per minute, reacts instantly to near-term changes, and logs key events to the UI.

---

## Integrations
- **Sonos Speakers:** Control playback, pause, stop, volume, and queue operations via `ByteDev.Sonos`.
- **Spotify:** Launch tracks, albums, or playlists by URL using Sonos’s native Spotify integration.
- **TuneIn & Internet Radio:** Store any stream URL, or fetch new stations directly from Radio Browser.
- **Localization:** Built-in support for `en-US` and `de-AT` cultures.

> **Important:** Set up TuneIn and Spotify inside the official Sonos app before using those features in SonosControl. The dashboard relies on Sonos’s native integrations, so the services must already be linked in your Sonos account.

---

## Logging & Observability
- **In-app Logs page:** Filter recent events (50–1000+) and export data manually.
- **Structured entries:** Every log includes action, performer, timestamp, and optional details.
- **Extensible storage:** Logs are stored in SQLite; plug in your favorite exporter or dashboard for long-term retention.

---

## Testing & Quality
The solution ships with comprehensive unit tests covering repositories and the automation service. Run them anytime with:

```bash
dotnet test
```

For the mobile Playwright smoke suite, run a single command from the repository root:

```powershell
.\run-mobile-smoke.ps1
```

If you prefer not to use the wrapper script:

```bash
python verify_mobile_smoke.py
```

Optional parameters:

```powershell
.\run-mobile-smoke.ps1 -BaseUrl "http://localhost:5107" -Username "admin" -Password "Test1234." -NoAutoStart
```

Continuous integration (GitHub Actions) pushes verified images to Docker Hub.

---

## Troubleshooting FAQ
| Symptom | Likely Cause | Fix |
|---------|--------------|-----|
| Admin login fails on fresh install | Missing or invalid `ADMIN_` variables | Recreate container with all three variables; ensure password meets complexity rules. |
| Playback never starts | Wrong Sonos IP or inactive day | Verify the speaker’s IP in Configuration and confirm today is marked active. |
| Timer stops immediately | Timer duration below 1 minute | Enter a positive integer for minutes before launching the session. |
| Custom station won’t play | URL not normalized | Use the Station Lookup search or remove `https://` when adding manually. |
| Cookies invalid after restart | Data protection keys not persisted | Mount `/root/.aspnet/DataProtection-Keys` (Docker) or set `DataProtection:KeysDirectory`. |

---

## Roadmap
- [x] Add user authentication
- [x] Password reset page
- [x] Customizable automation days
- [x] Startup playback presets (specific or random)
- [x] Timed playback durations
- [x] Playback history & audit logs
- [x] Toggleable user registration
- [x] Role-based access (admin/operator/superadmin)
- [ ] Holiday-aware scheduling
- [ ] Multi-room Sonos grouping
- [ ] Native mobile companion app

Have an idea? Open an issue or contribute below!

---

## Contributing
1. Fork the repository and clone your copy.
2. Create a feature branch (e.g., `feature/sonos-grouping`).
3. Install the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
4. Run `dotnet test` to verify changes.
5. Submit a pull request with a detailed description and screenshots when applicable.

Bug reports, feature requests, and documentation improvements are all welcome.

---

## License
SonosControl is released under the [Don't Be a Dick Public License](LICENSE.md). Enjoy it freely and abide by the straightforward terms outlined in the license.

---

## Useful Links
- **Docker Hub:** https://hub.docker.com/r/darkatek7/sonoscontrol
- **Radio Browser API:** https://www.radio-browser.info/
- **ByteDev.Sonos Library:** https://github.com/ByteDev/ByteDev.Sonos
- **ASP.NET Core Docs:** https://learn.microsoft.com/aspnet/

Ready to orchestrate your soundscape? Deploy SonosControl, press play, and let your speakers run themselves.
