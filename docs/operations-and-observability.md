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
- Sonos HTTP errors, SOAP faults, invalid sources, and missing required device IDs
  fail the command. Scene playback retries and fallback rules handle source
  failures; cancelled requests do not trigger recovery. Transport failures include
  the endpoint in structured application logs.
- If Sonos rejects group creation with UPnP error 501, the scene clears partial
  groups and plays on each target independently. `SceneGroupingUnavailable`
  explains that synchronization requires speakers in the same Sonos system.

Starting or pausing playback manually cancels existing scene timers for the
speaker's group and takes priority over the current schedule occurrence. The
scheduler does not retry that occurrence or pause manually controlled speakers
at its end. Future schedule occurrences run normally. This priority is held in
memory and resets when the application restarts.

## Backup and Restore
Back up these paths together:
1. `SonosControl.Web/Data/app.db`
2. `SonosControl.Web/Data/config.json`
3. Data protection key directory (`DataProtectionKeys` or configured equivalent)

Restore all three to preserve accounts, settings, and active sessions.

For configuration-only recovery, open Administration → Backups. Import and restore
validate and migrate the candidate before changing the active configuration and
create a safety backup under the settings lock. Invalid candidates and failed
writes leave the active file untouched. A valid backup can replace damaged JSON;
the damaged file is preserved as a safety backup. If the file cannot be read for
backup because of permissions, correct those permissions before retrying.

When `/healthz` reports an unhealthy settings check, inspect the application log
and the file's permissions and JSON syntax. The UI shows a settings error and the
API returns HTTP 503 for settings reads. Ordinary saves and automation do not
write defaults over the failed configuration. A missing file alone uses defaults.
Administrator sign-in remains available for recovery; public registration is
disabled while the configuration cannot be read.

Scene and schedule editors share API validation. Scene names are required and
unique, specific sources require a URL, timers cannot be negative, and volumes
must fit the current maximum. Schedule priorities are 0–1000 and fades are
0–600 seconds. Custom recurrence requires valid days, end dates cannot precede
start dates, and schedules must use valid scene references. Enabled schedules
require an enabled scene. Overnight windows and Resume/Random playback keep
their existing semantics.

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
