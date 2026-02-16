# Contributing

Thanks for helping improve SonosControl.

## Development Workflow
1. Fork and clone the repository.
2. Create a feature branch.
3. Run tests with `dotnet test`.
4. Submit a PR with clear change notes and screenshots for UI updates.

## README Assets and Screenshot Refresh
Generate README screenshots in one step:

```powershell
.\run-readme-screenshots.ps1
```

Optional:

```powershell
.\run-readme-screenshots.ps1 -BaseUrl "http://localhost:5107" -Username "admin" -Password "Test1234."
```

For best visuals, load representative demo data before capture (saved stations, logs, and at least one managed user).
