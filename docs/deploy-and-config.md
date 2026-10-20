# Deploy and Config Guide

This guide covers production and local deployment for SonosControl, plus the settings you should persist and back up.

## Prerequisites
- Docker (for container deployment) or .NET 10 SDK (for local run)
- A reachable Sonos speaker IP
- Admin seed credentials (`ADMIN_USERNAME`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`)
- For YouTube and uploaded MP3 playback, a LAN-reachable app URL that Sonos devices can open

## Docker Deployment
1. Create a folder with a `docker-compose.yml`.
2. Create persistent folders for app data and data protection keys.
3. Start the container.

```yaml
services:
  sonos:
    build:
      context: .
      dockerfile: Dockerfile
    image: sonoscontrol:local
    container_name: sonoscontrol
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      TZ: Europe/Vienna
      ADMIN_USERNAME: admin
      ADMIN_EMAIL: admin@example.com
      ADMIN_PASSWORD: ChangeMe123!
      PLAYBACK_PUBLIC_BASE_URL: http://192.168.1.50:8080
      FlyDj__Enabled: ${FLYDJ_ENABLED:-false}
      FlyDj__ServiceUrl: http://flydj:8090
      FlyDj__SharedSecret: ${FLYDJ_SHARED_SECRET:-}
    volumes:
      - ./Data:/app/Data
      - ./DataProtectionKeys:/app/DataProtectionKeys
      - ./artifacts:/app/artifacts

  # Optional experimental FlyDJ sidecar. Stays stopped (exit 0) unless FLYDJ_ENABLED=true.
  flydj:
    build:
      context: ./flydj
      dockerfile: Dockerfile
    image: sonoscontrol-flydj:local
    container_name: sonoscontrol-flydj
    restart: on-failure
    expose:
      - "8090"
    environment:
      TZ: ${TZ:-Europe/Vienna}
      FLYDJ_ENABLED: ${FLYDJ_ENABLED:-false}
      FLYDJ_CONNECTOME_SCOPE: ${FLYDJ_CONNECTOME_SCOPE:-auditory}
      FLYDJ_SIM_SPEED: ${FLYDJ_SIM_SPEED:-1.0}
      FLYDJ_SHARED_SECRET: ${FLYDJ_SHARED_SECRET:-}
    cpus: ${FLYDJ_CPUS:-1.0}
    mem_limit: ${FLYDJ_MEM_LIMIT:-512m}
```

```bash
cp .env.example .env
docker compose up -d --build
```

After startup, open `http://localhost:8080`.

The shipped image already includes `ffmpeg` and `yt-dlp`, so YouTube playback works inside the same container. The only external requirement is that `PLAYBACK_PUBLIC_BASE_URL` points to the host and port your Sonos devices can reach on the LAN.
For update, restart, backup, and restore commands, use [Docker Operations](docker-operations.md).

## Local Development Run

### PowerShell
```powershell
dotnet restore
Copy-Item SonosControl.Web/Data/config.template.json SonosControl.Web/Data/config.json -ErrorAction SilentlyContinue
dotnet run --project SonosControl.Web --urls http://localhost:5107
```

### Bash
```bash
dotnet restore
cp -n SonosControl.Web/Data/config.template.json SonosControl.Web/Data/config.json
dotnet run --project SonosControl.Web --urls http://localhost:5107
```

## Required Environment Variables
| Variable | Purpose | Required |
|---|---|---|
| `ADMIN_USERNAME` | Initial admin username | Yes (first run) |
| `ADMIN_EMAIL` | Initial admin email | Yes (first run) |
| `ADMIN_PASSWORD` | Initial admin password | Yes (first run) |
| `TZ` | Runtime timezone | Recommended |
| `ConnectionStrings__DefaultConnection` | Override database path | Optional |
| `DataProtection__KeysDirectory` | Key persistence directory | Optional |
| `Playback__PublicBaseUrl` or `PLAYBACK_PUBLIC_BASE_URL` | LAN URL Sonos should use for YouTube and uploaded MP3 audio | Required for YouTube and MP3 uploads |

## Temporary MP3 Uploads

In **Library → Upload MP3**, choose a file, speaker, and **Delete after (minutes)**, then select **Upload and play**. Operators and administrators can upload files. Playback replaces the selected speaker's current source.

The default upload limit is 100 MB and the default retention is 60 minutes. Each upload can set retention from 1 minute to 7 days. The deadline starts when the upload completes and survives app restarts. Expired URLs immediately return 404; the background cleanup removes expired files on startup and every minute, even if a speaker is still playing. Choose a retention period long enough for the entire track. Failed uploads and failed playback attempts remove their temporary files; interrupted uploads left by an app crash are removed after 24 hours.

Configure these values in `appsettings.json` or with environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `Mp3Upload__RetentionMinutes` | `60` | Initial value of the per-upload deletion timer (1–10080 minutes) |
| `Mp3Upload__CleanupIntervalMinutes` | `1` | Time between cleanup scans (1–60 minutes) |
| `Mp3Upload__MaxFileSizeBytes` | `104857600` | Maximum streamed file size (up to 1 GiB) |
| `Mp3Upload__ArtifactDirectory` | `artifacts/mp3-uploads` under the app directory | Temporary upload storage, outside `wwwroot` |

`Playback__PublicBaseUrl` must point to the app's LAN address and include any reverse proxy path prefix. Sonos fetches audio without a login using an unguessable temporary URL; byte-range requests are supported. Cleanup requires `BackgroundServices__Enabled=true` (the default). Retention is fixed per file at upload time; changing defaults affects future uploads.

## FlyDJ (optional, experimental)

When FlyDJ is enabled on the server (`FLYDJ_ENABLED=true` or
`FlyDj__Enabled=true`), the schedule editor offers **FlyDJ** under
**Playback → Source type** (with **Specific source** selected). Choose the
schedule's rooms, per-room volumes, synchronization and time window as usual;
no source URL is needed. FlyDJ starts automatically during the winning time
window, even if its standalone autopilot was off. It uses the candidate
stations and decision preferences saved on the FlyDJ page. Multiple concurrent
FlyDJ windows share station selection while retaining their own room settings.

Normal schedule priorities and date exclusions still apply per room. At the
window's end the schedule stops its playback and releases its rooms. A manual
start during the window keeps that room away from FlyDJ for the rest of the
occurrence. Turning FlyDJ off on its page skips the current FlyDJ occurrence;
future occurrences can start again. FlyDJ is available only for timed schedules,
not manual playback templates. Changing a room's volume does not take over its
playback or cancel its stop timer; FlyDJ can keep choosing stations. Successful play/pause,
next-track and volume commands from the app are recorded in Insights → Activity
with the user and affected room. A room taken over by manual playback is shown
as skipped for the rest of its schedule, without a misleading cooldown retry.
Paused simulations hide retained activity measurements instead of presenting
them as live values. If the server feature is disabled later, a saved
FlyDJ schedule reports that FlyDJ must be enabled and sends no playback commands.

FlyDJ is an optional, experimental radio DJ. It runs as a separate `flydj` Python container that is off by default and never published to the host: the web app reaches it at `http://flydj:8090` on the Compose network. The DJ chooses only between radio stations from your library and plays them on configured rooms. Its choices come from a simulation of the fruit-fly auditory connectome, which scores each candidate station by how the simulated brain responds to it. Manual control always wins; see [Operations and Observability](operations-and-observability.md#flydj-optional-experimental) for the playback ownership rules.

### Enable

1. Set `FLYDJ_ENABLED=true` in `.env`.
2. Build and start the stack: `docker compose up -d --build`.

While `FLYDJ_ENABLED` is `false`, the sidecar container exits immediately with code 0. With `restart: on-failure` it stays stopped and uses no CPU or RAM. Its image is still built or pulled. To turn FlyDJ off again, set the value back to `false` and run `docker compose up -d`.

| Variable | Default | Purpose |
|---|---|---|
| `FLYDJ_ENABLED` | `false` | Single on/off switch. Passed to the web app as `FlyDj__Enabled` and to the sidecar. |
| `FLYDJ_CONNECTOME_SCOPE` | `auditory` | `auditory` uses a baked subgraph of about 8k neurons seeded at Johnston's organ. `full` uses the whole brain (about 139k neurons, 15M connections). Both artifacts are built into the image from pinned, checksum-verified FlyWire data, so no download happens at runtime. |
| `FLYDJ_SIM_SPEED` | `1.0` | Target simulation speed relative to real time in the live view. |
| `FLYDJ_SHARED_SECRET` | empty | Optional token shared by the web app (`FlyDj__SharedSecret`) and the sidecar. When set, every sidecar request must send it. |
| `FLYDJ_CPUS` | `1.0` | CPU limit for the sidecar container. |
| `FLYDJ_MEM_LIMIT` | `512m` | Memory limit for the sidecar container. |
| `FlyDj__ServiceUrl` | `http://flydj:8090` | Internal sidecar address, set in `docker-compose.yml`. |

### Resource notes

- The default `auditory` scope is small and suits modest hardware with the default 1 CPU and 512 MB limits.
- The `full` scope uses about 300–500 MB of RAM and runs at roughly 0.6x real time on one fast core. Set `FLYDJ_CPUS=2.0` and `FLYDJ_MEM_LIMIT=1g` for it.
- When stopped, the simulation does no work. When running with no FlyDJ page open, it only simulates short bursts. With the page open, it simulates continuously for the live 3D view.
- The sidecar keeps no persistent data; FlyDJ settings and decision history live in the web app's `config.json` and database.

### Data and attribution

- Connectome data: FlyWire release 783, licensed under CC BY 4.0 (Dorkenwald et al. 2024, *Nature*).
- Leaky integrate-and-fire model: Shiu et al. 2024, licensed under MIT.
- Full attribution and license notices are in [`flydj/NOTICE.md`](../flydj/NOTICE.md). The sidecar API contract is in [`flydj/API.md`](../flydj/API.md).

## Config File Contract
- Runtime config file: `SonosControl.Web/Data/config.json` (not tracked)
- Template file: `SonosControl.Web/Data/config.template.json` (tracked)

Run one app instance per configuration directory. Settings updates read the latest
document, apply only edited fields or identified entries, and normalize and save
under one process-wide lock. Independent edits are preserved; the last saved edit
wins for the same field. An old editor cannot recreate an entry that was deleted.
Reload the page if an entry was removed while you were editing it. Autosave keeps
its debounce and offers a retry after a failed save.

Only a missing `config.json` starts with defaults. Invalid JSON or an unreadable
file produces an explicit error and makes the settings health check unhealthy.
Automation does not overwrite such a file with defaults. Correct file permissions
or restore a valid backup through Administration → Backups.

Changed settings are staged in a temporary file in the configuration directory
and atomically replace the active file. Unchanged updates do not write another
file or create another backup. Backups keep the existing JSON format, with up to
30 versioned files in `Data/backups`. A failed backup or replacement leaves the
active configuration untouched; temporary files are removed.

Import and restore explicitly replace the whole configuration. Incoming settings
are migrated and checked before replacement, including legacy and schema-v2
documents. The safety backup and replacement use the same settings lock. A
restore can replace damaged JSON and preserves it in the safety backup. Navigate
to another page after restoring to refresh the layout's settings error state.

At minimum, verify:
1. Speaker IP is set correctly.
2. Start and stop times match your timezone.
3. Saved stations and Spotify URLs are valid.

## Persistent Storage
- `Data/app.db` stores Identity, logs, and playback statistics.
- `Data/config.json` stores automation and playback settings.
- Data protection keys keep auth cookies valid across restarts.
- `artifacts/` stores short-lived YouTube audio fallback files and temporary MP3 uploads inside the container bind mount.

For production, back up all three regularly.

Playback timers from legacy scenes are optional runtime limits held in memory.
Starting a schedule or template cancels earlier timers on its target speakers,
including when the new playback has no timer. Timers on other speakers continue.
Playback commands and timer expiry are serialized per speaker across service
scopes. Shutdown cancels pending timers and awaits running timer commands;
timers are not restored after an app restart. Speaker auto-stop deadlines are
different: they are stored in `Data/app.db` and reconciled on restart (see
[Operations and Observability](operations-and-observability.md)).
