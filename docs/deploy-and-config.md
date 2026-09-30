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
    volumes:
      - ./Data:/app/Data
      - ./DataProtectionKeys:/app/DataProtectionKeys
      - ./artifacts:/app/artifacts
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

## Config File Contract
- Runtime config file: `SonosControl.Web/Data/config.json` (not tracked)
- Template file: `SonosControl.Web/Data/config.template.json` (tracked)

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
