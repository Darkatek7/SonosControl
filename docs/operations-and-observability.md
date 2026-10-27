# Operations and Observability

This guide summarizes runtime checks, telemetry endpoints, and day-2 operational practices.

## Runtime Health Endpoints

### Health
- Endpoint: `GET /healthz`
- Purpose: validates database connectivity and settings-file readability
- Expected behavior: returns healthy status when dependencies are available
- If the FlyDJ sidecar is unreachable, the health check reports Degraded, not Unhealthy, so an optional feature does not mark the core app unhealthy.

### Metrics
- Endpoint: `GET /metricsz`
- Purpose: returns lightweight app counters and timing snapshots
- Toggle: `Observability:EnableMetrics` (default `true`)
- `sonosEvents` reports live speaker events: `eventsReceived` and `eventsDropped` (queue full),
  `probesRequested` (full probes after a lost or unreadable event), `subscriptionFailures`, and from the
  last subscription cycle `activeSubscriptions`, `eventedSpeakers` and `polledSpeakers`.

## What to Monitor
1. Dashboard refresh failures and stale states
2. Sonos command failures
3. Playback monitor cycle duration
4. Session update/write rates
5. Speakers that fall back to polling (`sonosEvents.polledSpeakers`, Administration > Devices)
6. Database growth (`Data/app.db`)

## Logging
- UI logs are stored in SQLite and exposed through the Logs page.
- Record key admin actions and playback events for auditability.
- In incident review, correlate logs with `/healthz` and `/metricsz` snapshots.
- Sonos HTTP errors, SOAP faults, invalid sources, and missing required device IDs
  fail the command. Schedule playback retries and fallback rules handle source
  failures; cancelled requests do not trigger recovery. Transport failures include
  the endpoint in structured application logs.
- If Sonos rejects group creation with UPnP error 501, playback clears partial
  groups and plays on each target independently. `SceneGroupingUnavailable`
  explains that synchronization requires speakers in the same Sonos system.

Starting or pausing playback manually cancels the current schedule occurrence
for the affected room and takes priority until that occurrence ends. The
scheduler does not retry that occurrence or pause manually controlled rooms at
its end; future occurrences run normally. A scheduled stop remains enforced
even when a manual command changes playback. This ownership is persisted in
SQLite so a restart does not lose a pending schedule stop.

Speaker auto-stop rules are configured per speaker and per weekday in Device
settings: a Stop time, a Release time, and a grace period in minutes (required
and positive when the rule is enabled; rules are disabled by default). "Copy to
all days" duplicates one day's values to the rest of the week.

- A schedule's end always stops its speakers, even if music was started manually
  during the schedule.
- After the daily Stop time, a manual start from this web app is stopped once the
  grace period has elapsed (start 22:10 with 30 minutes grace stops at 22:40).
  Each new successful start gets its own deadline. Volume changes, skips, failed
  starts, and page reloads do not extend it. Starts made in the Sonos app are not
  registered.
- The rule applies until the Release time or the next effective schedule start on
  that speaker, whichever comes first. A Release time earlier than Stop means the
  following day.
- Only the due speaker is ungrouped and stopped. If ungrouping fails, no
  group-wide pause is sent; the failure is logged and retried.
- Sleep timers are independent of these deadlines.
- Deadlines are stored in SQLite and reconciled on restart, so overdue stops run
  immediately. Evaluation happens within the next scheduler cycle (about 15
  seconds). Room cards and the player show the next auto-stop and its reason.

The playback snapshot monitor is the single process-wide owner of room state
shared by Home, the dock, and connected browser circuits. It polls configured
rooms every five seconds. A room snapshot becomes stale after fifteen seconds
without a successful poll; stale snapshots retain their last known room values
and show that updates are delayed. Room ownership follows the configured
speaker address, so changing the active room does not move one room's observed
state to another.

The sleep timer is one shared, process-local timer across all connected
browsers. Starting a timer replaces the current timer, and any browser can
cancel it. The timer remains attached to the room selected when it was started,
even if the active room changes later. Manual Play or Pause leaves the sleep
timer armed; when its deadline arrives it takes command priority and pauses its
room. Sleep timers are independent of the persisted manual and scheduled stop
deadlines, and a running sleep timer is not persisted across application
restarts.

## FlyDJ (optional, experimental)

FlyDJ is an optional autonomous radio DJ in its own sidecar container. Its
playback runs in an automation lane with these ownership rules:

- Manual transport/source control always wins. Volume changes preserve FlyDJ
  playback and stop timers. Manual playback in SonosControl, in the Sonos app,
  or with the speaker's hardware buttons pauses FlyDJ for that room and starts a
  cooldown. External changes are detected through the playback snapshot monitor,
  so changes made outside the web app also count. The cooldown counts from the
  last manual command in the room; another manual start during the rest starts
  it over. When it ends, FlyDJ takes the room back even if music started by hand
  is still playing. When every room rests, the simulation is stopped so it uses
  no CPU.
- Ordinary schedules own their rooms. A schedule whose source is FlyDJ authorizes
  FlyDJ in its own rooms and volumes. Manual takeover rests that room for the
  same cooldown, capped at the end of that FlyDJ occurrence. When the window
  ends first, the displayed end is the schedule boundary.
- Sleep timers and daily auto-stop rules still apply to rooms FlyDJ plays.
- FlyDJ only chooses between radio stations from the library. A schedule may
  override the global candidate pool. Independent contexts share stream probes
  and sidecar lifetime; stopping one does not pause another context's brain.
- Optional preparation observes ahead of a schedule but never touches speakers
  before its start. Look for **Preparing** in the context selector. The page's
  sample counts, median, typical variation and last-sample age belong to the
  selected context. Selection weights remain 70% brain and 30% listening habits.
  The variation is a robust description of recent measurements, not a prediction
  of listener approval or a statistical confidence interval.
- If a station appears to stop receiving probes, check its evidence count and
  freshness. Non-leading stations may be sampled less often once playing;
  insufficient evidence returns them to the regular cadence. All contexts share
  a two-stream fetch limit. Cancelling preparation cancels its in-flight request.
  Context selection changes the live picture; the global autopilot switch stops
  all currently managed contexts.

The sidecar runs in one of three load modes. See the
[sidecar API contract](../flydj/API.md#load-modes) for details.

- `stopped`: no simulation work. The loop waits for a start request.
- `bursting`: running with no live view open. Only short bursts run, for the
  station audition and listening measurements.
- `continuous`: running while a FlyDJ page is open. The simulation runs
  continuously for the live 3D view, throttled to `FLYDJ_SIM_SPEED` times real
  time.

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

Scenes and schedules are one Schedules surface (`/automation`; the old `/scenes`
and `/schedule-windows` routes redirect there). Each entry embeds its playback
payload: source, selected rooms, and per-room volume. Entries saved without time
control are manual templates, started with Start now. Date bounds, priority,
fades, and exclusions sit in a collapsible Advanced section.

Settings schema version 3 migrates on startup, after writing a backup. Scenes
shared by several schedules are copied per schedule, unreferenced scenes become
manual templates, and invalid references become disabled entries that can be
repaired in the editor. Old scene timers become an optional runtime limit.
Imported backups are validated and migrated before they replace active settings.
Schedule priorities are 0–1000 and fades are 0–600 seconds. Custom recurrence
requires valid days, end dates cannot precede start dates, and overnight windows
and Resume/Random playback keep their existing semantics.

`/api/schedules` returns the full data. Legacy payloads that use `SceneId`, and
`/api/scenes`, still work through a compatibility adapter; write operations on
them require admin or superadmin. Custom recurrence requires valid days, end dates cannot precede
start dates, and overnight windows and Resume/Random playback keep their
existing semantics.

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
