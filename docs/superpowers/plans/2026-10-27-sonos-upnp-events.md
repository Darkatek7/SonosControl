# Sonos UPnP Event Subscriptions Implementation Plan

Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the 5-second playback polling with UPnP GENA event subscriptions on each configured Sonos speaker, so external changes (Sonos app, buttons on the speaker, other controllers) reach Home, the dock, room cards, auto-stop and FlyDJ within about a second, while the speakers receive far fewer SOAP requests.

**Architecture:** Speakers push `NOTIFY` requests to a new anonymous endpoint on the existing app port. A subscription manager keeps one `AVTransport` and one `RenderingControl` subscription per configured speaker alive. Parsed events are merged into the existing `PlaybackSnapshotStore`, which stays the single source of truth for every consumer. Polling is not removed: it becomes a slow reconciliation pass for speakers with healthy subscriptions and stays at today's 5 s for speakers without one (no callback URL, speaker refuses, renewal failed).

**Tech Stack:** ASP.NET Core (.NET 10) controllers and hosted services, `HttpClient` with custom `SUBSCRIBE`/`UNSUBSCRIBE` methods, `System.Xml.Linq`, xUnit + Moq, `TimeProvider` fakes, Playwright smoke test with the Python fake speaker.

## Current State

- `PlaybackSnapshotMonitorService` polls every 5 s. Per speaker it issues four SOAP calls (`GetVolume`, `GetTransportInfo`, `GetMediaInfo`, `GetPositionInfo`), with at most 4 speakers in parallel and a 10 s device timeout. Snapshots go stale 15 s after the last successful poll.
- The same poll cycle re-reads settings and publishes `SettingsChanged` through a fingerprint. That duty must survive the change.
- `PlaybackSnapshotStore` guards writes with per-speaker generations and a configuration epoch. `BeginProbe` bumps the generation and `TrySet` rejects writes from older probes.
- Consumers of the store: `PlaybackUiStateService` (all UI circuits), `DeviceHealthMonitorService` (online/playing/volume), `FlyDjConductorService` (manual-control detection via `Changed`). `RefreshNowAsync` is called after commands by the player bar, sleep timer, scheduler and scene orchestration.
- `PlaybackMonitorService` (listening statistics) runs its own 15 s poller with three more SOAP calls per speaker. It does not use the store.
- `SonosConnectorRepo.GetTrackInfoAsync` parses DIDL metadata with regexes and then applies two enrichments: the YouTube session fallback and the queue item fallback. Event metadata must go through the same path or titles regress for YouTube and queue playback.
- `Playback:PublicBaseUrl` already holds the LAN URL that speakers use to reach the app (YouTube and MP3 streaming), and the Docker setup publishes port 8080. No new port or host networking is needed.
- The smoke fake speaker (`SimulatedSpeakerHandler` in `verify_mobile_smoke.py`) has no `SUBSCRIBE` handler, so it answers 501 today. That is a free fallback test.

Today one speaker costs about 60 SOAP requests per minute (48 from the snapshot poller and 12 from the statistics poller). The target is about 8 per minute plus a renewal every 15 minutes.

## Protocol Notes

- Subscribe: `SUBSCRIBE http://{ip}:1400/MediaRenderer/AVTransport/Event` (and `/MediaRenderer/RenderingControl/Event`) with `CALLBACK: <{url}>`, `NT: upnp:event`, `TIMEOUT: Second-1800`. The response carries `SID: uuid:...` and the granted `TIMEOUT`.
- Renew: `SUBSCRIBE` with `SID` and `TIMEOUT`, without `CALLBACK` and `NT`. `412 Precondition Failed` means the subscription is gone and a fresh subscribe is needed.
- Unsubscribe: `UNSUBSCRIBE` with `SID`.
- Notify: `NOTIFY {callback}` with `NT: upnp:event`, `NTS: upnp:propchange`, `SID`, `SEQ`. The body is an `e:propertyset` whose `LastChange` property holds XML-escaped event XML. `AVTransport` carries `TransportState`, `AVTransportURI`, `CurrentTrackURI`, `CurrentTrack` and `CurrentTrackMetaData` (DIDL, escaped again). `RenderingControl` carries `<Volume channel="Master" val="..."/>`.
- The speaker sends an initial `NOTIFY` with the full state (`SEQ: 0`) right after subscribing, and it can arrive before the `SUBSCRIBE` response. Routing by a per-subscription token in the callback path, created before the request is sent, avoids that race.
- `SEQ` increases by one per event. A gap means a lost event and triggers an immediate reconciliation probe for that speaker.

## Hardware Findings (2026-10-27)

Recorded with a Play:1 and an Era 100 on Sonos OS 85.0 (`Server: Linux UPnP/1.0 Sonos/85.0-66270 (ZPS12)`), both playing the same AAC radio stream. The recorded bodies are in `SonosControl.Tests/SonosEventFixtures.cs`.

- `SUBSCRIBE`, renewal, `412` for an unknown `SID` and `UNSUBSCRIBE` behave as described above. The granted `TIMEOUT` equals the requested one (300 s and 600 s tested). `SID` values look like `uuid:RINCON_<device>_sub<counter>`.
- The initial `NOTIFY` arrives within a few milliseconds, in one case before the `SUBSCRIBE` response of the speaker's next service. Token routing is required, not optional.
- The `NOTIFY` source address is the speaker. The `HOST` header is the callback host, so the speaker used the callback address it was given and routing worked across the Mac's two interfaces.
- Follow-up events are partial. The `SEQ 1` event omitted `AVTransportURI` and all transport capability fields, so the Task 4 merge must keep unreported fields.
- A radio stream title change produces an `AVTransport` event (`SEQ 1`) with new `CurrentTrackMetaData`. Radio "now playing" updates arrive live without polling.
- Radio metadata uses the stream file name as `dc:title` and the real title in `r:streamContent`. `CurrentTrackURI` (`aac://http://...`) differs from `AVTransportURI` (`x-rincon-mp3radio://...`). Station identity must keep coming from `TransportUri`.
- A second run sent Pause, Play and a volume change (8 → 10 → 8) to the Era 100 over SOAP. Each `NOTIFY` arrived within about 10 ms of the command.
- Pause on a radio stream reports `STOPPED`, because radio cannot pause. Play goes through `TRANSITIONING` with `r:streamContent` `ZPSTR_CONNECTING`, then `TRANSITIONING` with `ZPSTR_BUFFERING`, then `PLAYING` with an empty stream title, and finally `PLAYING` with the real title, about 3.6 s in total.
- Follow-up `RenderingControl` events carry only the volume channels (`Master`, `LF`, `RF`).
- Not covered yet: a cleared queue, a speaker reboot, Docker networking, groups. The two speakers belong to different Sonos households, and they cannot be moved because the Sonos app cannot reach the cloud from this network (TLS inspection) and the speakers are in a different VLAN than the WLAN. Groups are therefore handled by design instead of by test (see decision 2).
- The speakers reached the Mac on its address in the speaker VLAN, so this test did not cross a firewall between VLANs. In production the speaker VLAN must reach the SonosControl host on its app port. YouTube and MP3 playback already need the same path.

## Global Constraints

- `PlaybackSnapshotStore` remains the only shared playback state. No consumer talks to the event layer directly.
- Every event write calls `BeginProbe` and then `TrySet` with the current epoch, so an older in-flight poll cannot overwrite newer event data.
- Events are partial. Merge them into the previous snapshot. If no snapshot exists yet, request a full probe instead of writing a partial one.
- The callback endpoint is anonymous (speakers send no cookie), like `YouTubeAudioController` and `Mp3AudioController`. It must reject unknown tokens, mismatched `SID` values and oversized bodies, and it must not trigger device commands.
- Answer `NOTIFY` quickly with 200. Parse and apply on a bounded background channel so a slow store subscriber never delays the speaker.
- Polling stays the fallback. If no callback base URL is configured, behaviour is identical to today.
- Use `TimeProvider` for all timers so renewals and reconciliation are testable.
- Required checks per `AGENTS.md`: `dotnet test SonosControl.sln --verbosity minimal`, `dotnet format SonosControl.sln --verify-no-changes --no-restore`, the NuGet audit restore, `python3 verify_mobile_smoke.py` (the smoke script changes), and markdown lint plus link validation for doc changes.
- Smoke-test behaviour is documented in detail in `AGENTS.md`. Update it, `docs/testing-and-troubleshooting.md`, `.github/workflows/docs-quality.yml` and `.markdownlint-cli2.jsonc` together when the smoke checks change.
- Record user-facing changes under `## [Unreleased]` in `CHANGELOG.md`.

## Configuration

New `SonosEvents` section in `appsettings.json`:

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` (from PR 3; `false` in PR 2) | Master switch. Without a callback base URL the feature stays off. |
| `CallbackBaseUrl` | empty | Overrides the callback base. Falls back to `Playback:PublicBaseUrl`. Needed when a reverse proxy with authentication sits in front of the public URL. |
| `SubscriptionTimeoutSeconds` | `1800` | Requested subscription lifetime. Renewal starts at half the granted timeout. |
| `ReconcileSeconds` | `30` | Full probe interval for speakers with healthy subscriptions. |

Docker Compose gets `SonosEvents__Enabled` and `SonosEvents__CallbackBaseUrl` with `.env` defaults.

## Decisions to Confirm

1. **Offline detection gets slower.** Without a lost event there is nothing to notice an unplugged speaker except the reconciliation probe and failed renewals. With `ReconcileSeconds = 30`, an evented speaker goes stale after 75 s (2.5 × reconcile) instead of 15 s. Recommendation: accept this. The alternative, a one-call `GetTransportInfo` liveness probe every 10 s, keeps most of the polling load.
2. **Group members.** Decided: do not depend on what a member reports. A member's own `AVTransport` event names its coordinator (`AVTransportURI` = `x-rincon:RINCON_<coordinator>`). While that is the case, the member's transport state, track and station come from the coordinator's snapshot when the coordinator is configured. Volume stays per speaker, because `RenderingControl` is per device. This is part of Task 4, so the hardware check no longer gates it.
3. **Default on.** PR 2 ships the feature off by default. PR 3 turns it on after the hardware check, because the polling fallback makes a misconfigured network harmless.

---

### Task 1: Extract DIDL metadata parsing (no behaviour change)

**Files:**
- Modify: `SonosControl.DAL/Repos/SonosMetadataParser.cs`, `SonosControl.DAL/Repos/SonosConnectorRepo.cs`
- Test: `SonosControl.Tests/SonosConnectorRepoTests.cs` and a new `SonosMetadataParserTests.cs`

- [x] Move the regex DIDL parsing from `GetTrackInfoAsync` into `SonosMetadataParser.ParseTrackMetadata(string didl, string speakerIp)`, including the relative album art rewrite to `http://{ip}:1400/...`.
- [x] Add a connector method `ResolveTrackInfoAsync(string ip, string? trackMetadata, string? trackUri, int? trackNumber, CancellationToken)` that parses the DIDL and applies the YouTube session fallback and then the queue fallback. `GetTrackInfoAsync` delegates to it, so its output stays identical. One method instead of a separate enrich step keeps `SonosMetadataParser` internal to the DAL.
- [x] Add `ResolveTrackInfoAsync` to `ISonosConnectorRepo`.
- [x] Tests: the existing connector tests pass unchanged. New parser tests cover radio `streamContent`, relative art, `dc:creator` fallback and HTML entities. A parity test checks that event metadata and `GetTrackInfoAsync` produce the same track. The DAL now has `InternalsVisibleTo("SonosControl.Tests")`, like the web project.

### Task 2: Parse event bodies

**Files:**
- Create: `SonosControl.DAL/Repos/SonosEventParser.cs`, `SonosControl.DAL/Models/SonosPlaybackEvent.cs`
- Test: `SonosControl.Tests/SonosEventParserTests.cs` with handwritten edge cases and bodies recorded from real speakers in `SonosControl.Tests/SonosEventFixtures.cs`. Add recordings for play/pause, volume and a cleared queue during the Task 8 hardware check.

- [x] `SonosPlaybackEvent` holds nullable fields: `TransportState`, `TransportUri`, `TrackUri`, `TrackNumber`, `TrackMetadata`, `Volume`. Null means "not part of this event".
- [x] `SonosEventParser.Parse(SonosEventService service, string? body)` reads `propertyset` → `LastChange` → `InstanceID val="0"` with `XDocument` (DTDs prohibited, bodies above 256 KB rejected). Use `RenderingControl` only for the `Master` channel. Ignore unknown elements. Return null for malformed XML; never throw into the endpoint.
- [x] Report empty and `NOT_IMPLEMENTED` values as an empty string, not as absent. A cleared queue reports empty metadata, and polling shows an empty track in that case too, so the merge in Task 4 must clear the old title instead of keeping it.
- [x] Tests: full initial `AVTransport` event, a track-change-only event, a volume event, transport states including `TRANSITIONING`, a grouped member (`x-rincon:` URI), a YouTube session URI, other instances, malformed, DTD and oversized input.

### Task 3: Subscription client

**Files:**
- Create: `SonosControl.DAL/Repos/SonosEventSubscriptionClient.cs`
- Modify: `SonosControl.Web/Program.cs` (named `HttpClient`, 5 s timeout)
- Test: `SonosControl.Tests/SonosEventSubscriptionClientTests.cs` with a fake `HttpMessageHandler`

- [x] `SubscribeAsync(ip, service, callbackUrl, timeout)` returns `(Sid, GrantedTimeout)`. `RenewAsync(ip, service, sid, timeout)` returns the granted timeout or a `SubscriptionLost` result on 412. `UnsubscribeAsync(ip, service, sid)` is best effort.
- [x] Use `new HttpMethod("SUBSCRIBE")` and `new HttpMethod("UNSUBSCRIBE")`. Set `CALLBACK`, `NT`, `SID` and `TIMEOUT` without header validation (`TryAddWithoutValidation`).
- [x] Log failures in the same shape as `SonosSoapTransport`: service, speaker IP, HTTP status.
- [x] Tests: exact request headers for subscribe, renew and unsubscribe; `Second-infinite` and missing `TIMEOUT` handling; 412; 501 (the fake speaker today); timeouts.

### Task 4: NOTIFY endpoint and event pipeline

**Files:**
- Create: `SonosControl.Web/Controllers/SonosEventsController.cs`, `SonosControl.Web/Services/SonosEvents/SonosEventDispatcher.cs`
- Modify: `SonosControl.Web/Services/PlaybackSnapshotStore.cs`
- Test: `SonosControl.Tests/SonosEventsControllerTests.cs`, `SonosControl.Tests/SonosEventDispatcherTests.cs`, `SonosControl.Tests/PlaybackSnapshotStoreTests.cs`

- [x] `[AllowAnonymous]`, `[Route("api/sonos-events")]`, `[AcceptVerbs("NOTIFY", Route = "{token}")]`. Check: token known (404 otherwise), `SID` matches the token's subscription once known (412 otherwise), `NT`/`NTS` present (400), body at most 256 KB (413). Read the body, enqueue `(token, sid, seq, body)` on a bounded `Channel` and return 200.
- [x] Make sure `UseHttpsRedirection` and antiforgery never touch this route.
- [x] `SonosEventDispatcher` (hosted, singleton) reads the channel, parses with `SonosEventParser`, checks `SEQ` (a gap requests a reconciliation probe), turns `TrackMetadata` into `SonosTrackInfo` through `ISonosConnectorRepo.ResolveTrackInfoAsync` in a scope, and merges into the store.
- [x] Add `PlaybackSnapshotStore.TryApplyEvent(string ip, Func<PlaybackSnapshot, PlaybackSnapshot> merge, DateTimeOffset now)`. It checks that the speaker is still configured instead of taking an epoch, because the subscription manager reads settings on its own schedule. It calls `BeginProbe`, merges into the current snapshot, sets `IsStale = false`, `LastSuccessfulRefreshUtc = now`, clears `Error` and writes through the generation check. It returns false if there is no snapshot yet, and the dispatcher then requests a full probe.
- [x] Merge rules from the hardware findings:
  - `TRANSITIONING` counts as playing, so the UI does not show "Paused" for two seconds after Play or a track skip. `STOPPED` and `PAUSED_PLAYBACK` count as not playing.
  - Stream titles starting with `ZPSTR_` (`ZPSTR_CONNECTING`, `ZPSTR_BUFFERING`) are placeholders. Treat them as empty, and keep the previous display title while the track URI is unchanged and the new stream title is empty or a placeholder. Apply the same placeholder filter to polled metadata through `SonosTrackInfo.IsValidMetadata`.
  - A member whose `TransportUri` is `x-rincon:RINCON_<coordinator>` takes transport state, track and station from the coordinator's snapshot (decision 2).
- [x] Tests: event wins over an older in-flight probe; a probe started after the event wins over it; partial events keep the other fields; unknown speaker or old epoch is ignored; `Changed` fires once per applied event; the recorded Pause/Play sequence in `SonosEventFixtures` ends as playing with the real title and never shows a placeholder; a member follows its coordinator.

### Task 5: Subscription manager

**Files:**
- Create: `SonosControl.Web/Services/SonosEvents/SonosEventSubscriptionManager.cs`, `SonosControl.Web/Services/SonosEvents/SonosEventOptions.cs`
- Modify: `SonosControl.Web/Program.cs`, `SonosControl.Web/appsettings.json`
- Test: `SonosControl.Tests/SonosEventSubscriptionManagerTests.cs` with `FakeTimeProvider`

- [x] Hosted service, registered only when background playback snapshots are enabled (same condition as `PlaybackSnapshotMonitorService`) and a callback base URL resolves.
- [x] Desired state comes from the configured speakers in settings. For each speaker × service: create a random token, subscribe with `{base}/api/sonos-events/{token}`, renew at half the granted timeout, resubscribe on 412, back off from 15 s to 5 min on errors.
- [x] Removed speakers: unsubscribe and drop their tokens. Shutdown: unsubscribe all, best effort with a short timeout.
- [x] Expose `IsHealthy(ip)` (both services subscribed, last renewal or event within the timeout), `LastError(ip)`, and `LastEventUtc(ip)` on the registry. Metrics counters move to Task 8.
- [x] Tests: renew timing, 412 recovery, backoff, removed speakers, shutdown unsubscribe, no subscriptions without a callback URL.

### Implementation notes for PR 2 (Tasks 3–5)

- The `ZPSTR_` placeholder filter sits in `SonosMetadataParser.ParseTrackMetadata` instead of `SonosTrackInfo.IsValidMetadata`, so polling and events both drop placeholders at the source.
- `PlaybackSnapshotMerge.KeepStreamTitle` applies to polled snapshots as well, so a reconnecting stream no longer flashes its file name during the remaining 5 s polling.
- A group member that follows its coordinator keeps its own `CurrentStationUrl` (`x-rincon:...`), because `FlyDjConductorService.SnapshotMatchesStation` relies on it.
- The subscription manager reads the configured speakers from settings, like the snapshot monitor. Hosted services are registered only when both `PlaybackSnapshots:Enabled` and `SonosEvents:Enabled` are true; the controller and singletons always exist, so unknown tokens get 404.
- Live check on 2026-10-27 against the Play:1 and Era 100: four subscriptions, four initial `NOTIFY` requests answered with 200, four renewals, and four `UNSUBSCRIBE` requests answered with 200 on shutdown.

### Task 6: Adaptive polling and freshness

**Files:**
- Modify: `SonosControl.Web/Services/PlaybackSnapshotMonitorService.cs`, `SonosControl.Web/Services/PlaybackSnapshotStore.cs`
- Test: `SonosControl.Tests/PlaybackSnapshotMonitorServiceTests.cs`, `SonosControl.Tests/DeviceHealthPlaybackSnapshotTests.cs`

- [x] Keep the 5 s loop, because it also detects settings changes, but skip the four-call probe for a speaker whose subscriptions are healthy and whose last full probe is newer than `ReconcileSeconds`.
- [x] Make the stale threshold per speaker: 15 s for polled speakers, 2.5 × `ReconcileSeconds` for evented ones. `MarkExpiredStale` and `GetNextStaleDeadline` take a threshold lookup instead of one `TimeSpan`. The stale heartbeat now sleeps at most one 5 s poll interval, because a speaker that loses its events switches to the shorter threshold.
- [x] `RefreshNowAsync` keeps probing every speaker, so command follow-ups stay exact.
- [x] A reconciliation request from the dispatcher (SEQ gap, missing snapshot) probes that speaker immediately. Done in PR 2 through `PlaybackSnapshotMonitorService.ProbeSpeakerAsync`.
- [x] Apply the coordinator rule to probes too. A polled member follows its coordinator when the coordinator is configured and its snapshot is fresh.
- [x] Tests: evented speakers are skipped and reconciled on time; a subscription turning unhealthy brings the speaker back to 5 s polling; per-speaker staleness; a polled member follows its coordinator. Device health reads `IsStale` from the snapshot, so the staleness tests cover its longer offline threshold.

### Task 7: Move listening statistics onto the store

**Files:**
- Modify: `SonosControl.Web/Services/PlaybackMonitorService.cs`
- Test: `SonosControl.Tests/PlaybackMonitorServiceTests.cs`

- [x] When the snapshot monitor maintains the store (`PlaybackSnapshotStore.HasConfiguration`), read `IsPlaying`, `TrackInfo` and `CurrentStationUrl` from fresh snapshots instead of calling the connector. Checking for a running monitor instead of a registered store keeps statistics working when `PlaybackSnapshots:Enabled` is false. Keep the 15 s cycle for duration bookkeeping and `MaxObservationGap`. Stale snapshots count as "not observed".
- [x] Keep the connector path when snapshots are disabled, as `DeviceHealthMonitorService` already does.
- [x] Tests: sessions start, change track and end from snapshot data; stale snapshots do not end a session early; an unmonitored store falls back to device calls.

Live check on 2026-10-27 with both speakers, 65 s each: 62 and 52 SOAP requests with events off, 9 and 8 with events on (about 85 % fewer), five `NOTIFY` requests answered with 200, no warnings, all subscriptions ended on shutdown.

### Task 8: Diagnostics, smoke test, docs, real hardware

**Files:**
- Modify: `SonosControl.Web/Services/MetricsCollector.cs`, `SonosControl.Web/Pages/DevicesPage.razor`, `verify_mobile_smoke.py`, `docker-compose.yml`, `docker-compose.hub.yml`, `docs/deploy-and-config.md`, `docs/operations-and-observability.md`, `docs/testing-and-troubleshooting.md`, `AGENTS.md`, `CHANGELOG.md`

- [x] `/metricsz`: active subscriptions, events received, renewal failures, reconciliation probes, polled speakers.
- [x] Devices page: per speaker "Live events" or "Polling", with the last event time and the last subscription error.
- [x] Smoke fake speaker: add `do_SUBSCRIBE` and `do_UNSUBSCRIBE`, remember callbacks, and send `NOTIFY` with `LastChange` bodies from `set_external_state`. The owned configured fixture sets `SonosEvents__CallbackBaseUrl` to the app's loopback URL and checks external Play/Pause and volume within 3 s instead of 10 s. Add a variant where `SUBSCRIBE` answers 501 and the existing 10 s checks still pass through polling.
- [x] Docs: callback reachability (the speaker must reach `CallbackBaseUrl`; a firewall or an authenticating reverse proxy forces polling), the new settings, the diagnostics. Sync `AGENTS.md` with the new smoke behaviour.
- [x] Docs also cover separate VLANs: the speaker VLAN must reach `CallbackBaseUrl`, the same path YouTube and MP3 playback already need.
- [ ] Real hardware checklist before default on. Done on 2026-10-27: single rooms on two models, radio, play/pause, volume, subscription renewal and expiry, unsubscribe on app shutdown, and the request reduction. Still open: Spotify, YouTube session playback, a cleared queue, a speaker reboot, an unplugged speaker, an app restart (old subscriptions expire on their own), Docker bridge networking with the published port, and a group or stereo pair where hardware in one household is available.

### Task 9 (optional, follow-up): Topology events

- [x] Subscribe to `/ZoneGroupTopology/Event` and use topology changes to refresh the group and sync UI without waiting for a probe. Member routing itself is already covered by Task 4.

### Implementation notes for PR 4 (Tasks 8 and 9)

- Events are on by default (`SonosEvents:Enabled`, `SONOS_EVENTS_ENABLED`), although the hardware checklist above is not complete. Decision 3 accepted this risk because a speaker without working events falls back to 5 s polling; the Devices page and `/metricsz` make that visible.
- `/metricsz` gains a `sonosEvents` block. Active subscriptions and the evented and polled speaker counts come from the last subscription cycle. Topology refreshes count as probes.
- The Devices page shows "Live updates, last HH:mm:ss" or "Polling every 5 s" with the last subscription error, through `ISonosEventStatusProvider`. While events are off it shows nothing.
- `docker-compose.hub.yml` only overrides the images and inherits the environment from `docker-compose.yml`, so it needed no change. `.env.example` documents the two new variables.
- The smoke runner has four configurations now. The configured fixture runs with live events and expects external changes within 3 s. A new short polling configuration answers `SUBSCRIBE` with 501 and expects them within 10 s. On machines where the installed Chrome cannot start headless, `PLAYWRIGHT_CHROME_PATH` can point to Playwright's `chrome-headless-shell`.
- Every configured speaker subscribes to topology events, because household membership is not known in advance. Only a changed group layout counts: the dispatcher compares a fingerprint of coordinators and members, ignores the first event of each subscription, and coalesces the reports of all speakers into one `RefreshNowAsync` after one second. Topology subscriptions do not count for speaker health.

## Delivery

| PR | Contents | Behaviour change |
|---|---|---|
| 1 | Task 1 and Task 2 | None |
| 2 | Tasks 3–5, metrics and diagnostics from Task 8, `SonosEvents:Enabled = false` | Only when switched on |
| 3 | Tasks 6 and 7 | Only when switched on |
| 4 | Task 8, Task 9, default on | Release 1.1.0 |

## Out of Scope

- Track position: events do not carry `RelTime`. Progress keeps its current behaviour.
- Removing polling entirely. It remains the fallback and the safety net for lost events.
- Discovery changes. SSDP stays as it is.
