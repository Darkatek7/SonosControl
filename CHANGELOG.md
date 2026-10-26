# Changelog

All notable changes to SonosControl are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release workflow
uses the section whose heading matches the pushed tag as the GitHub release notes;
see [Releases](CONTRIBUTING.md#releases).

## [Unreleased]

## [1.0.0] - 2026-10-26

First versioned release of the existing feature set:

- Home player with a sticky mini-player for queue, rooms, groups, sync, sleep
  timer and volume.
- One Library for TuneIn, Spotify, YouTube and YouTube Music sources, Radio
  Browser discovery, favourites and temporary MP3 uploads.
- Unified Schedules with per-room sources and volumes, synced playback, date
  exceptions, fades, manual templates and per-speaker daily auto-stop rules.
- Listening statistics, an activity trail, role-based access and recoverable
  configuration backups.
- Health and metrics endpoints (`/healthz`, `/metricsz`).
- Optional, experimental FlyDJ sidecar that picks radio stations from your
  library and always yields to manual control.

### Added

- Versioned releases: pushing a `vX.Y.Z` tag runs CI, publishes
  `darkatek7/sonoscontrol` and `darkatek7/sonoscontrol-flydj` as `X.Y.Z`, `X.Y`
  and `X`, and creates a GitHub release from this changelog.
- The Administration page shows the running build version.
- `SONOSCONTROL_VERSION` in `.env` pins the Docker Hub images used by
  `docker-compose.hub.yml`.

### Changed

- Docker Hub images for `main` and `nightly` are published only after CI passes
  for that commit.
