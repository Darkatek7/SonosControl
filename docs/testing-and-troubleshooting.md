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

The owned configured fixture additionally creates a scene, a schedule, and a
saved source. It checks editor field labels, real save-button clicks and hit
tests, nested dialog focus, Escape, focus restoration, keep/discard decisions,
internal navigation and browser back. Mobile editors are checked at 320 pixels,
with short viewports and 200% text; switching between desktop and mobile must
preserve the draft. These writing scenarios never run against a caller-supplied
app. The compact Home player must remain at most 400 pixels tall with the smoke
fixture, with the room heading above the dock. Screenshots include the saved
mobile schedule editor.

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
| App runs but no playback starts | Wrong speaker IP, inactive schedule, or invalid scene | Check Administration > Devices and the scene/schedule status in Automation |
| Tests fail with locked DLL on Windows | App still running during build/test | Stop running `dotnet run` processes, then rerun tests |
| Cookies invalid after restart | Data protection keys not persisted | Persist `DataProtectionKeys` directory in deployment |
| Local smoke fails writing `/root` on macOS | Data protection keys point to a Linux path | Let `verify_mobile_smoke.py` use `artifacts/mobile_smoke_dataprotection_keys` or set `DataProtection__KeysDirectory` |
| README screenshot script cannot log in against a manually started app | Missing credentials in env/args | Pass `-Username/-Password` explicitly |

## Debugging Tips
1. Check app logs first, then inspect `/healthz` and `/metricsz`.
2. Verify `SonosControl.Web/Data/config.json` exists and is valid JSON.
3. Validate DB path from connection string if using non-default storage.
4. Re-run smoke scripts after any layout or navigation change.
