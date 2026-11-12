# Changelog

All notable changes to this project are documented here. Unreleased entries
describe candidate source changes, not published container tags.

## v0.1.1 - Unreleased candidate

Stability work intended to preserve existing routes, JSON fields, cache folder
names and saved image URLs. The candidate is not published or release-qualified.
The latest GitHub release remains `v0.0.6` as of 2026-11-13.

### Changed

- Upgraded the application, build image and runtime container to .NET 10 LTS
  (`Microsoft.AspNetCore.App` 10.0.15) with package lockfiles and a
  `global.json` SDK policy.
- Centralized location identity and validation, including documented state
  abbreviations and ordinary Australian place-name punctuation.
- Cache lookup, deletion, startup recovery and retention now resolve recognised
  direct-child folders with containment, ownership and symlink checks. Unknown
  directories are preserved.
- Captures publish through an on-filesystem staging folder after metadata,
  frame-index and PNG-header checks. Readers use one snapshot; full PNG decoding
  and malformed-metadata handling remain review gaps.
- Background refresh now has one bounded queue owner with per-location
  deduplication, capture deadlines, retry backoff and shutdown cancellation.
- Historical selection filters by inclusive UTC observation bounds, keeps exact
  timestamps and resolves duplicate observations in favour of the newest capture.
  The four-minute thinning step was removed.
- Added browser disconnection handling and Chromium recreation, plus context
  cleanup on capture cancellation. Fault recovery still needs qualification.
- Added `/api/ready` and `/api/cache/{suburb}/{state}/status`.
- The demo flattens and sorts historical frames explicitly, clamps playback
  after a shorter collection, reads error bodies once and shows stale/failure
  status with retry guidance.
- Docker Compose is CPU-only by default, disables debug output, removes the fixed
  container name and makes the cache path and image configurable.
- Added workflows for locked restore, publication-content checks, CPU container
  smoke tests, live-BOM verification and native `amd64`/`arm64` release checks.
  Adding these workflows does not establish that candidate qualification passed.

### Fixed

- Added rejection and isolation checks for wildcard, traversal, absolute-path,
  wrong-location and top-level snapshot symlink requests. Reserved-directory and
  nested symlink containment still need work.
- Incomplete or failed captures no longer replace the previous complete
  snapshot.
- Frame indices are validated against configuration and the selected snapshot
  instead of a hard-coded `0..6` range.
- Capture status records bounded failure details and retry timing, then clears
  failure fields after recovery. The demo still has a status-rendering gap.
- The latest-data 404 path no longer reads the response body twice.

### Validation to date

- Locked restores, the Release build/publish and 66 deterministic tests passed
  on 2026-11-13. Package vulnerability feeds were unavailable (`NU1900`), so a
  dependency vulnerability audit was not completed. Tests use
  real controllers and cache code, synthetic PNGs and a controllable capture;
  they do not exercise the real browser capture lifecycle.
- One explicit demo browser test passed on 2026-11-12 with intercepted API
  fixtures. It does not cover actual service outages or live BOM capture.
- Saved Pomona verification used an intermediate service on port 18083. Earlier
  two-location baseline results do not qualify the final candidate.
- The saved soak was interrupted after approximately 2 minutes 20 seconds,
  with 3 successful samples, 5 failed samples, no observation advancement and
  null memory metrics. No completed 30-hour trial is recorded in the available
  artifacts.
- Final candidate image, native architecture and release qualification remain
  outstanding; no v0.1.1 release evidence or published image is available.

### Known limitations

Remaining review findings include reserved-directory/nested symlink containment,
malformed metadata, header-only PNG validation, browser setup cancellation,
refresh admission during startup/shutdown, and location/time-input validation.
The demo still has duplicate polling, incomplete network-outage recovery and a
failure-detail rendering issue. See the README's candidate validation section
and the [HA handoff](docs/ha-handoff/README.md) for the related card work.
