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

## FlyDJ (optional, experimental)
The FlyDJ sidecar (`sonoscontrol-flydj`) is off by default. Its details and
settings are in [Deploy and Config Guide](deploy-and-config.md#flydj-optional-experimental).

### Enable
Set `FLYDJ_ENABLED=true` in `.env`, then build and start the stack:

```bash
docker compose up -d --build
```

### Check
```bash
docker compose ps
docker compose logs flydj
```

With `FLYDJ_ENABLED=false`, `docker compose ps` shows `sonoscontrol-flydj` as
`Exited (0)`. This is expected: the container stays stopped and uses no CPU or
RAM. With FlyDJ enabled, it shows `Up` and reports `healthy` after its start
period. Both connectome artifacts are baked into the image, so the sidecar
needs no internet access at runtime.

The sidecar listens on port 8090 only inside the Compose network. It is not
published to the host.

### Resources
```bash
docker stats --no-stream sonoscontrol-flydj
```

Compare the reported CPU and memory with the `FLYDJ_CPUS` and `FLYDJ_MEM_LIMIT`
values. Use `FLYDJ_CPUS=2.0` and `FLYDJ_MEM_LIMIT=1g` for the `full` scope.

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
