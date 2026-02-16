# Operations and Observability

This guide summarizes runtime checks, telemetry endpoints, and day-2 operational practices.

## Runtime Health Endpoints

### Health
- Endpoint: `GET /healthz`
- Purpose: validates database connectivity and settings-file readability
- Expected behavior: returns healthy status when dependencies are available

### Metrics
- Endpoint: `GET /metricsz`
- Purpose: returns lightweight app counters and timing snapshots
- Toggle: `Observability:EnableMetrics` (default `true`)

## What to Monitor
1. Dashboard refresh failures and stale states
2. Sonos command failures
3. Playback monitor cycle duration
4. Session update/write rates
5. Database growth (`Data/app.db`)

## Logging
- UI logs are stored in SQLite and exposed through the Logs page.
- Record key admin actions and playback events for auditability.
- In incident review, correlate logs with `/healthz` and `/metricsz` snapshots.

## Backup and Restore
Back up these paths together:
1. `SonosControl.Web/Data/app.db`
2. `SonosControl.Web/Data/config.json`
3. Data protection key directory (`DataProtectionKeys` or configured equivalent)

Restore all three to preserve accounts, settings, and active sessions.

## Security and Secrets Hygiene
1. Keep `config.json` and data protection keys out of source control.
2. Use strong admin passwords in deployment variables.
3. Rotate admin password and data protection keys if they were ever exposed.
4. Restrict access to `/metricsz` and `/healthz` at the reverse proxy when internet-facing.

## Operational Checklist
1. Confirm `/healthz` returns healthy after deploy.
2. Validate speaker playback from the Home page.
3. Verify schedule start/stop behavior for the current day.
4. Confirm logs continue to ingest events.
5. Verify backups include DB, config, and keys.
