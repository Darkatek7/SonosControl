# Deploy and Config Guide

This guide covers production and local deployment for SonosControl, plus the settings you should persist and back up.

## Prerequisites
- Docker (for container deployment) or .NET 9 SDK (for local run)
- A reachable Sonos speaker IP
- Admin seed credentials (`ADMIN_USERNAME`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`)

## Docker Deployment
1. Create a folder with a `docker-compose.yml`.
2. Create persistent folders for app data and data protection keys.
3. Start the container.

```yaml
version: "3.4"
services:
  sonos:
    image: darkatek7/sonoscontrol:latest
    container_name: sonos
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      - TZ=Europe/Vienna
      - ADMIN_USERNAME=admin
      - ADMIN_EMAIL=admin@example.com
      - ADMIN_PASSWORD=ChangeMe123!
    volumes:
      - ./Data:/app/Data
      - ./DataProtectionKeys:/root/.aspnet/DataProtection-Keys
```

```bash
docker compose up -d
```

After startup, open `http://localhost:8080`.

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

For production, back up all three regularly.
