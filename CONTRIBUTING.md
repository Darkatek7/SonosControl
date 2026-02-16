# Contributing

Thanks for helping improve SonosControl.

## Prerequisites
- .NET 9 SDK
- Python 3.10+ and Playwright (for UI smoke and screenshot workflows)

## Development Workflow
1. Fork and clone the repository.
2. Create a feature branch.
3. Run tests with `dotnet test`.
4. Submit a PR with clear change notes and screenshots for UI updates.

## Branch and PR Expectations
1. Keep PRs focused and small when possible.
2. Link related issues in the PR description.
3. Include before/after UI screenshots when layout or interaction changes.
4. Use the repository PR template checklist.

## Test Requirements
Required baseline:

```bash
dotnet test
```

Optional but recommended for UI/docs related changes:

```powershell
.\run-mobile-smoke.ps1
python scripts/check_markdown_links.py README.md docs CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md
```

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

## Issue and Security Reporting
- Use GitHub issue templates for bugs and feature requests.
- Do not create public issues for vulnerabilities. Follow `SECURITY.md`.
