# Changelog

All notable changes to SonosControl are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release workflow
uses the section whose heading matches the pushed tag as the GitHub release notes;
see [Releases](CONTRIBUTING.md#releases).

## [Unreleased]

### Added

- Experimental live speaker events (`SonosEvents__Enabled`, off by default):
  speakers report playback, track and volume changes to the app as they
  happen. Speakers with working events are only reconciled every 30 seconds
  instead of being polled every 5 seconds; the others keep polling. See
  [Live Speaker Events](docs/deploy-and-config.md#live-speaker-events-experimental-off-by-default).

### Changed

- Listening statistics read the shared playback status instead of querying
  every speaker again every 15 seconds.

### Fixed

- Radio titles no longer show `ZPSTR_CONNECTING` or `ZPSTR_BUFFERING`, and the
  last song title stays visible while a stream reconnects instead of the stream
  file name.

- FlyDJ now takes rooms back once "Stay away after manual control" has passed
  since the last manual command, even if music started by hand is still
  playing. The same cooldown applies inside FlyDJ schedules, capped at the end
  of the occurrence.

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
