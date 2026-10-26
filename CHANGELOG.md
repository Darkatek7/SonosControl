# Changelog

All notable changes to SonosControl are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release workflow
uses the section whose heading matches the pushed tag as the GitHub release notes;
see [Releases](CONTRIBUTING.md#releases).

## [Unreleased]

First versioned release of the existing feature set.

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

### Fixed

- A manual playback preemption test no longer depends on thread pool timing.
