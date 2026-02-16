# Testing and Troubleshooting

This guide covers test commands, smoke checks, and common failure recovery steps.

## Test Commands

### Full suite
```bash
dotnet test SonosControl.sln --no-build
```

### Targeted service tests
```bash
dotnet test SonosControl.Tests/SonosControl.Tests.csproj --no-build --filter "FullyQualifiedName~SonosControlServiceTests"
```

## UI Smoke Checks

### Mobile smoke
```powershell
.\run-mobile-smoke.ps1
```

Optional:
```powershell
.\run-mobile-smoke.ps1 -BaseUrl "http://localhost:5107" -Username "admin" -Password "Test1234." -NoAutoStart
```

### README screenshot capture
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
| App runs but no playback starts | Wrong speaker IP or inactive day | Check Config page speaker IP, active days, and schedule window |
| Tests fail with locked DLL on Windows | App still running during build/test | Stop running `dotnet run` processes, then rerun tests |
| Cookies invalid after restart | Data protection keys not persisted | Persist `DataProtectionKeys` directory in deployment |
| README screenshot script cannot log in | Missing credentials in env/args | Pass `-Username/-Password` explicitly |

## Debugging Tips
1. Check app logs first, then inspect `/healthz` and `/metricsz`.
2. Verify `SonosControl.Web/Data/config.json` exists and is valid JSON.
3. Validate DB path from connection string if using non-default storage.
4. Re-run smoke scripts after any layout or navigation change.
