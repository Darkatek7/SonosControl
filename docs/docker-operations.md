# Docker Operations

This page covers the day-to-day commands for the all-in-one SonosControl container setup.

## Prerequisites
- Docker Engine with Compose support
- A copied `.env` file based on `.env.example`
- `PLAYBACK_PUBLIC_BASE_URL` set to a LAN URL that Sonos devices can reach

## First Start
1. Copy the environment template.
2. Adjust credentials and the public playback URL.
3. Build and start the stack.

```bash
cp .env.example .env
docker compose up -d --build
```

## Routine Commands

### Check status
```bash
docker compose ps
docker compose logs --tail=200
```

### Pull code changes and rebuild
```bash
git pull
docker compose up -d --build
```

### Restart without rebuild
```bash
docker compose restart
```

### Stop the stack
```bash
docker compose down
```

## Backup
Back up all persisted runtime state:
- `Data/`
- `DataProtectionKeys/`
- `artifacts/`
- `.env`

Example:

```bash
tar -czf sonoscontrol-backup-$(date +%Y%m%d-%H%M%S).tar.gz \
  Data DataProtectionKeys artifacts .env
```

## Restore
1. Stop the stack.
2. Restore the backup archive into the deployment folder.
3. Start the stack again.

```bash
docker compose down
tar -xzf sonoscontrol-backup-YYYYMMDD-HHMMSS.tar.gz
docker compose up -d
```

## YouTube Runtime Checks
If YouTube playback fails, verify these first:
- `PLAYBACK_PUBLIC_BASE_URL` points to the correct host and port
- Sonos devices can reach that URL on the LAN
- the container is healthy and serving `/healthz`

Useful checks:

```bash
docker compose exec sonoscontrol which yt-dlp
docker compose exec sonoscontrol which ffmpeg
docker compose exec sonoscontrol printenv Playback__PublicBaseUrl
```
