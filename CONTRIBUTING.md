# Contributing

Thanks for helping improve SonosControl.

## Prerequisites

- .NET 10 SDK
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
python scripts/check_markdown_links.py README.md docs CONTRIBUTING.md CHANGELOG.md CODE_OF_CONDUCT.md SECURITY.md
```

AI-agent workflow guidance lives in `AGENTS.md` and should be updated whenever CI or verification workflows change.

## Releases

Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html): bump
the major version for breaking changes (for example settings or deployment changes
that need manual steps), the minor version for new features, and the patch version
for fixes.

1. Add user-facing changes under `## [Unreleased]` in `CHANGELOG.md` as you go.
2. To release, rename that heading to `## [X.Y.Z] - YYYY-MM-DD`, add a new empty
   `## [Unreleased]` above it, and merge the change to `main`.
3. Tag the merged commit and push the tag:

   ```bash
   git tag -a vX.Y.Z -m "SonosControl X.Y.Z"
   git push origin vX.Y.Z
   ```

The `Release` workflow then runs CI on the tagged commit, publishes both Docker
images as `X.Y.Z`, `X.Y` and `X`, and creates the GitHub release from the matching
`CHANGELOG.md` section plus the commits since the previous tag. A tag with a
suffix such as `v1.3.0-rc.1` becomes a prerelease and only gets its exact Docker
tag. Builds from `main` between releases report versions like `1.2.0-main.4`
(four commits after `v1.2.0`).

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
