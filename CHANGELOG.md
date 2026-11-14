# Changelog

All notable changes to this project are documented here. Unreleased entries
describe candidate source changes, not published container tags.

## v0.1.1 - Unreleased candidate

Stability work intended to preserve existing routes, JSON fields, cache folder
names and saved image URLs. The candidate is not published or release-qualified.
The latest GitHub release remains `v0.0.6` as of 2026-11-15.

The follow-up service-only implementation is tracked in the
[qualification register](docs/service-qualification.md). Its cleanup, freshness,
recovery and progression criteria must be demonstrated before qualification.

### Changed

- Upgraded the application, build image and runtime container to .NET 10 LTS
  (`Microsoft.AspNetCore.App` 10.0.15) with package lockfiles and a
  `global.json` SDK policy.
- Centralized location identity and validation, including documented state
  abbreviations and ordinary Australian place-name punctuation.
- Cache lookup, deletion, startup recovery and retention now resolve recognised
  direct-child folders with containment, ownership and symlink checks. Unknown
  directories are preserved.
- Captures publish through an on-filesystem staging folder. Follow-up source adds
  bounded metadata and full PNG validation before publication, with corrupt
  snapshots rejected individually; corruption and fallback regressions pass.
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
  wrong-location, reserved-directory and nested snapshot symlink requests.
  Focused outside-sentinel cases pass.
- Incomplete or failed captures no longer replace the previous complete
  snapshot.
- Frame indices are validated against configuration and the selected snapshot
  instead of a hard-coded `0..6` range.
- Capture status records bounded failure details and retry timing, then clears
  failure fields after recovery. Follow-up demo source preserves failure details
  across relative-time updates and uses one polling owner.
- The latest-data 404 path no longer reads the response body twice.

### Validation to date

- Signed service implementation checkpoint: `18f565599f814215e42f5ee47a79294834229e4d`.
- Locked restores, Release build/publish and publication-content audit pass.
- 124 deterministic cases pass across the initial and focused runs. Invalid
  legacy test PNGs were repaired without relaxing image validation; the deadline
  test now observes reservation release before asserting staging cleanup.
- Ten real Chromium/demo cases pass across the initial and focused runs,
  including abrupt Chromium crash/recreation and outage recovery.
- Dependency audit completed with no vulnerable packages reported.
- Replacement runner: independent source audit, 66 pure checks, 11 live
  native/container cases and verified teardown. The initial runner's ambient
  cgroup incident is documented in the qualification register.
- Fifty verifier/soak checks pass. Thirty-hour simulations do not qualify a trial.
- Candidate/promotion helper checks pass; no registry writes were performed.

### Known limitations

The final candidate is not frozen or qualified. Native amd64 image evidence,
fresh two-location verification, fault/restart/rollback exercises and the actual
30-hour trial remain pending. Resource-growth acceptance thresholds need short
baselines before freeze. Path validation does not eliminate concurrent filesystem
replacement races; browser cleanup that never returns remains bounded/best-effort
inside the service, with owned runner teardown as the test boundary. ARM64 testing
and card/HA implementation are deferred. See the
[qualification register](docs/service-qualification.md) for evidence and limits.
