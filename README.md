# SonosControl - Self-Hosted Sonos Automation Dashboard
[![Dockerhub](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml/badge.svg)](https://github.com/Darkatek7/SonosControl/actions/workflows/dockerhubpush.yml)

SonosControl is a deployer-friendly Blazor control center for automating Sonos playback, scheduling start/stop windows, and managing stations and users from one self-hosted app.

![SonosControl dashboard hero](docs/assets/readme/images/desktop-home.png)

## Quick Start

### 1. Run with Docker Compose

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

Open `http://localhost:8080` and sign in with the seeded admin account.
Set `PLAYBACK_PUBLIC_BASE_URL` to the LAN URL that your Sonos devices can reach. `localhost` does not work for YouTube audio streaming to Sonos.

### 2. Run locally with .NET 10

PowerShell:
```powershell
dotnet restore
Copy-Item SonosControl.Web/Data/config.template.json SonosControl.Web/Data/config.json -ErrorAction SilentlyContinue
dotnet run --project SonosControl.Web --urls http://localhost:5107
```

Bash:
```bash
dotnet restore
cp -n SonosControl.Web/Data/config.template.json SonosControl.Web/Data/config.json
dotnet run --project SonosControl.Web --urls http://localhost:5107
```

Then open `http://localhost:5107`.

## Screenshot Gallery

### Desktop
| Home | Config | Statistics | User Management | Logs |
|---|---|---|---|---|
| ![Desktop home](docs/assets/readme/images/desktop-home.png) | ![Desktop config](docs/assets/readme/images/desktop-config.png) | ![Desktop statistics](docs/assets/readme/images/desktop-stats.png) | ![Desktop users](docs/assets/readme/images/desktop-users.png) | ![Desktop logs](docs/assets/readme/images/desktop-logs.png) |

### Mobile
| Home | Config | Statistics | User Management | Logs |
|---|---|---|---|---|
| ![Mobile home](docs/assets/readme/images/mobile-home.png) | ![Mobile config](docs/assets/readme/images/mobile-config.png) | ![Mobile statistics](docs/assets/readme/images/mobile-stats.png) | ![Mobile users](docs/assets/readme/images/mobile-users.png) | ![Mobile logs](docs/assets/readme/images/mobile-logs.png) |

## Feature Highlights
- Real-time Sonos dashboard with playback, queue, group, and volume controls.
- Day-based automation with start/stop windows and optional random media selection.
- TuneIn, Spotify, YouTube, and YouTube Music source management from a single UI.
- Role-based access (`operator`, `admin`, `superadmin`) with registration control.
- Searchable audit logs for operational traceability.
- Health and metrics endpoints (`/healthz`, `/metricsz`) for basic monitoring.
- Docker image includes `ffmpeg` and `yt-dlp` for YouTube audio playback without extra sidecars.

## Docs Index
- [Deploy and Config Guide](docs/deploy-and-config.md)
- [Docker Operations](docs/docker-operations.md)
- [Operations and Observability](docs/operations-and-observability.md)
- [Testing and Troubleshooting](docs/testing-and-troubleshooting.md)
- [Warning triage notes](docs/quality-warning-triage.md)
- [Contributing Guide](CONTRIBUTING.md)

## Contributing
Contribution workflow, README asset maintenance, and screenshot refresh instructions are documented in [CONTRIBUTING.md](CONTRIBUTING.md).

## License
SonosControl is released under the [Don't Be a Dick Public License](LICENSE.md).

## Useful Links
- Docker Hub: https://hub.docker.com/r/darkatek7/sonoscontrol
- ByteDev.Sonos: https://github.com/ByteDev/ByteDev.Sonos
- Radio Browser: https://www.radio-browser.info/
- ASP.NET Core docs: https://learn.microsoft.com/aspnet/core
