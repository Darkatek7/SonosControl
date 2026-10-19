# Testing and Troubleshooting

This guide covers test commands, smoke checks, and common failure recovery steps.

## Test Commands

### Full suite
```bash
dotnet test SonosControl.sln --verbosity minimal
dotnet format SonosControl.sln --verify-no-changes --no-restore
dotnet restore -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
```

### Targeted service tests
```bash
dotnet test SonosControl.Tests/SonosControl.Tests.csproj --no-build --filter "FullyQualifiedName~AutomationSchedulerServiceTests|FullyQualifiedName~SettingsSchemaMigrationServiceTests"
```

## UI Smoke Checks

### Mobile smoke
macOS/Linux:
```bash
python3 -m venv artifacts/ui-smoke-venv
artifacts/ui-smoke-venv/bin/python -m pip install --requirement requirements-ui.txt
artifacts/ui-smoke-venv/bin/python verify_mobile_smoke.py
```

The auto-start path creates disposable settings and SQLite directories under
`artifacts`, disables background services, and checks all primary pages in
light and dark themes at 390, 768, and 1280 pixels in two configurations:
empty setup and a configured simulated speaker. The simulation uses only
loopback port 1400; keep that port free for the auto-start check. Empty setup
must show the large Home onboarding, no playback dock on any route, and no
reserved dock clearance. The configured fixture must retain the dashboard,
dock, volume controls, and expanded player. Checks also fail on browser
console errors, horizontal overflow, or content hidden behind the player.
The floating player must stay at its configured bottom inset, and mobile
content clearance includes both the dock height and the inset.

Only the runner-owned configured fixture enables the shared playback snapshot
monitor. It polls its loopback speaker every five seconds, and a snapshot is
marked stale after fifteen seconds without a successful refresh. The smoke
changes the fake speaker's Play/Pause and volume state outside the app, then
checks that Home, the dock, and the active room card show those changes within
ten seconds. Empty setup disables the monitor. A caller-supplied running app
is only inspected; the runner never enables services, changes playback, or
performs the writing workflows there.

The owned configured fixture additionally creates a unified schedule with its
playback source and room mixer, a manual playback template, and a saved source.
It checks that schedules can be created without a prior scene, empty room
selection is rejected without a device command, templates can be started now,
and legacy scenes remain editable as manual templates. Per-room daily stop
rules validate the required manual limit, copy one day's values to all days,
and persist across reload. It also checks editor labels, real save-button hit
tests, nested dialog focus, keep/discard decisions, and draft preservation when
the editor changes size. Mobile editors are checked at 320 pixels, with short
viewports and 200% text. The Time control toggle must turn a schedule into a
manual template and back without losing its timing, and the Advanced section
(priority, dates, fades, excluded dates) must stay reachable above its save
action on small screens. After a manual start with a passed daily stop rule, the
room card and player must show "Stops at" with its reason. These writing scenarios never run against a
caller-supplied app. The compact Home player must remain at most 400 pixels tall
with the smoke fixture, with the room heading above the dock. Screenshots
include the saved mobile schedule editor.

The same disposable fixture verifies 0–100 volume limits while typing, including
appending digits to 100, negative values, and exponent notation. Startup volume
and the system maximum must retain their bounded values after reload; optional
startup and schedule room volume fields can be cleared. A broken artwork response must
restore the record, which spins after starting a source manually. Service tests
cover cancellation of legacy scene timers, manual playback taking priority over
the current schedule occurrence, and independent playback when Sonos refuses
grouping with UPnP error 501. Future schedule occurrences still run normally.

The configured schedule workflow also verifies that an explicit empty speaker
selection cannot be saved and sends no device commands. It opens a legacy
`SpeakerIps` scene migrated as a manual template and checks that its original
room selection is preserved, then lowers the configured maximum and saves an
embedded playback draft clamped to the new limit. A one-minute sleep timer is started before the remaining
workflows, survives a page reload, and appears in a second browser context.
That context cancels and replaces the shared timer; the replacement remains
armed beyond the original one-minute deadline and is then canceled in both
contexts. After the editor workflows, a fresh one-minute timer remains armed
through manual Pause/Play and page reload, then expires and pauses the
simulated room.

When targeting an already running app, the runner checks its current
configuration without changing settings. Use an unused app port with
auto-start enabled to exercise both isolated configurations.

Optional:
```bash
MOBILE_SMOKE_BASE_URL="http://localhost:5107" \
MOBILE_SMOKE_USERNAME="admin" \
MOBILE_SMOKE_PASSWORD="Test1234." \
MOBILE_SMOKE_AUTOSTART=0 \
python3 verify_mobile_smoke.py
```

On macOS, the runner auto-detects Google Chrome at
`/Applications/Google Chrome.app/Contents/MacOS/Google Chrome`. If Chrome is
installed elsewhere, set `PLAYWRIGHT_CHROME_PATH`. If the installed Chrome
cannot start, install a separate test browser with
`artifacts/ui-smoke-venv/bin/python -m playwright install chromium` and set
`PLAYWRIGHT_CHROME_PATH` to that browser's executable. This does not require
changing the smoke scripts.

Windows:
```powershell
.\run-mobile-smoke.ps1
```

Optional:
```powershell
.\run-mobile-smoke.ps1 -BaseUrl "http://localhost:5107" -Username "admin" -Password "Test1234." -NoAutoStart
```

### README screenshot capture
macOS/Linux:
```bash
artifacts/ui-smoke-venv/bin/python capture_readme_screenshots.py
```

The auto-start path uses disposable settings and SQLite storage, disables
background services, and captures Light and Dark at desktop and mobile sizes.

Windows:
```powershell
.\run-readme-screenshots.ps1
```

Optional:
```powershell
.\run-readme-screenshots.ps1 -BaseUrl "http://localhost:5107" -Username "admin" -Password "Test1234."
```

## Common Issues
| Symptom | Likely cause | Resolution |
|---|---|---|
| Login fails on first run | Missing `ADMIN_*` values | Set all three admin variables and restart the app |
| App runs but no playback starts | Wrong speaker IP, inactive schedule, disabled entry with an invalid reference, or an auto-stop rule that already ended playback | Check Administration > Devices (speaker IP and auto-stop rules) and the entry status in Automation |
| Playback stops shortly after a manual start | A daily auto-stop rule's grace period elapsed after its Stop time | Adjust or disable the speaker's rule in Administration > Devices, or start playback after its Release time |
| A synchronized schedule reports grouping unavailable | Speakers cannot join the requested Sonos group, for example because they belong to separate Sonos systems | Playback falls back to independent speakers and logs `SceneGroupingUnavailable`; place speakers in the same Sonos system to synchronize them |
| Tests fail with locked DLL on Windows | App still running during build/test | Stop running `dotnet run` processes, then rerun tests |
| Cookies invalid after restart | Data protection keys not persisted | Persist `DataProtectionKeys` directory in deployment |
| Local smoke fails writing `/root` on macOS | Data protection keys point to a Linux path | Let `verify_mobile_smoke.py` use `artifacts/mobile_smoke_dataprotection_keys` or set `DataProtection__KeysDirectory` |
| README screenshot script cannot log in against a manually started app | Missing credentials in env/args | Pass `-Username/-Password` explicitly |

## Debugging Tips
1. Check app logs first, then inspect `/healthz` and `/metricsz`.
2. Verify `SonosControl.Web/Data/config.json` exists and is valid JSON.
3. Validate DB path from connection string if using non-default storage.
4. Re-run smoke scripts after any layout or navigation change.
