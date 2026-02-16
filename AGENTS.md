# AGENTS.md

## Purpose and Scope
- This file defines mandatory instructions for AI coding agents working in this repository.
- Audience: AI agents only.
- Canonical location: repository root `AGENTS.md`.
- Verification policy: run only the checks required by changed files, but always run those required checks.

## Repository Map
- `SonosControl.Web`: ASP.NET Core Blazor web application, UI pages, services, EF Core migrations.
- `SonosControl.DAL`: domain and integration logic for Sonos control and data access.
- `SonosControl.Tests`: unit and component test suite.
- `docs`: deployment, operations, testing, and quality documentation.
- `scripts`: repository automation utilities, including markdown link validation.

## Task Routing by Changed Files
| Changed file pattern | Required action |
|---|---|
| `SonosControl.Web/**`, `SonosControl.DAL/**`, `SonosControl.Tests/**`, `TestApp/**`, `*.cs`, `*.csproj`, `SonosControl.sln` | Run `dotnet test SonosControl.sln --verbosity minimal` |
| `SonosControl.Web/Pages/**`, `SonosControl.Web/Shared/**`, `SonosControl.Web/wwwroot/css/**`, `*.razor`, `*.razor.css` | Run `.\run-mobile-smoke.ps1` |
| `README.md`, `docs/**`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`, `.markdownlint-cli2.jsonc`, `scripts/check_markdown_links.py`, `AGENTS.md` | Run markdown lint and markdown link validation commands from the Required Verification Commands section |
| `docs/assets/readme/**`, `capture_readme_screenshots.py`, `run-readme-screenshots.ps1`, and any UI paths from above | Recommend `.\run-readme-screenshots.ps1` and review image diffs |

## Required Verification Commands
Run only the commands required by the routing matrix for the files you changed.

```powershell
dotnet test SonosControl.sln --verbosity minimal
```

```powershell
.\run-mobile-smoke.ps1
```

```powershell
markdownlint-cli2 README.md "docs/**/*.md" CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md AGENTS.md
```

If `markdownlint-cli2` is not installed globally, use:

```powershell
npx --yes markdownlint-cli2 README.md "docs/**/*.md" CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md AGENTS.md
```

```powershell
python scripts/check_markdown_links.py README.md docs CONTRIBUTING.md CODE_OF_CONDUCT.md SECURITY.md AGENTS.md
```

## Optional Verification Commands
- Preferred accelerator when available: use the `verify-sonoscontrol-change` skill to select and run scoped checks.
- Optional visual validation for UI or README visual updates:

```powershell
.\run-readme-screenshots.ps1
```

## Safety Rules
- Never commit secrets, tokens, or real credentials.
- Do not edit EF migration designer or snapshot files manually unless they are regenerated consistently.
- Do not run destructive git commands unless explicitly requested.
- Keep verification scope minimal but mandatory according to the routing matrix.

## PR Evidence Format
Use concise evidence in PR descriptions:

```text
Validation summary
- Changed file scope: <scope>
- Required checks run:
  - <command>: PASS/FAIL
  - <command>: PASS/FAIL
- Optional checks run:
  - <command>: PASS/FAIL or N/A
- Notes: <key outputs, artifact paths, or screenshots>
```

## Done Criteria
- All required checks for changed files have been executed and passed.
- Optional screenshot refresh completed when visual surfaces changed.
- Documentation and workflow updates are synchronized when process/verification behavior changed.
- PR evidence includes commands executed and pass/fail status.

## Maintenance Rule
- When CI workflows, verification scripts, or required checks change, update `AGENTS.md`, `.github/workflows/docs-quality.yml`, and `.markdownlint-cli2.jsonc` in the same change.
