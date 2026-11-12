# BOM Local Service → HA integration handoff

Candidate: **`v0.1.1` - unreleased and not release-qualified** (2026-11-13).
Service repository: `home-assistant/bom-local-service`.
Local build tag: `bom-local-service:candidate`.
Published candidate image/digest: **not available**. There is no v0.1.1 release
commit or release artifact to reference yet. The latest GitHub release remains
`v0.0.6`; published `latest` is separate from this local candidate.

## Contract fixtures

`fixtures/` contains representative response bodies for the scenarios the card
must handle:

| File | Scenario |
| :-- | :-- |
| `fresh-cache.json` | Complete latest snapshot, cache valid |
| `stale-cache.json` | Previous snapshot served while a refresh runs |
| `cold-start-404.json` | No cache yet; structured 404 with retry guidance |
| `failure-status.json` | Capture failed; stale data remains available |
| `recovery-status.json` | Success after failure; failure fields cleared |
| `overlapping-history.json` | Historical capture groups with a deduplicated overlap |

Frame image URLs always include `cacheFolder`. Historical consumers should
flatten `cacheFolders` and sort by `absoluteObservationTime`; grouping by
capture is not a global order guarantee.

## Compatibility check

The recorded `bom-local-card` run using
`./run.sh test --service-path ../bom-local-service` covers the config and Options
flows. In real-service mode that harness skips the deterministic proxy and
WebSocket data assertions, so this is not evidence of end-to-end card/service
compatibility or image playback. Those checks still need to run against the
final candidate.

The service's 66 deterministic tests passed on 2026-11-13; its separate demo
browser test passed on 2026-11-12 using intercepted responses. Neither result
qualifies the Home Assistant card. Saved Pomona verification used an
intermediate service on port 18083, and the interrupted 2-minute-20-second soak
does not constitute the planned 30-hour operational trial.

## Remaining HA defects (tracked separately)

These defects were identified in the separate card repository and remain
outside the service candidate changes:

1. **Connection-error handling** - `custom_components/bom_local/websocket.py`
   catches `ClientError` without importing it. Transport/JSON failures can raise
   `NameError` instead of returning the intended WebSocket error.
2. **Manual retry** - API retry callbacks return successful data without applying
   it to card state, so the error and empty frame collection can remain after a
   successful retry.
3. **Retry scheduling** - The independent refresh interval can bypass server
   backoff and manual-refresh guidance. Scheduling should respect `nextRetryAt`
   and `retryAfterSeconds` as well as the legacy retry fields.
4. **Chronological playback** - The API layer sorts historical frames by
   `absoluteObservationTime`, but the card sorts them again by per-folder
   `frameIndex`, breaking global order across captures.
5. **Replacement collections** - The card does not clamp its selected index
   before loading a shorter replacement collection, which can produce a
   persistent missing-image error.
6. **Installation/release delivery** - Card main contains changes beyond its
   latest published release (`v0.3.0`); `v0.3.1` exists as a tag without a GitHub
   Release. Prepare a versioned card release after fixes, and document the tested
   service image/digest. A mutable `latest` image is not a compatibility pin.

## Install/upgrade

For local candidate testing, build from a checkout containing these changes:

```bash
docker build -t bom-local-service:candidate .
docker run -d --name bom-local-service \
  -p 8082:8080 -v $(pwd)/cache:/app/cache \
  --shm-size=1gb --ipc=host \
  bom-local-service:candidate
```

Do not pull `v0.1.1` until it is published. For the existing published service,
use the registry's `latest` image and record its resolved digest separately.
Automated fixtures cover reading legacy cache folders without renaming. Final
upgrade/rollback and real HA data-path verification remain outstanding; preserve
a cache copy when testing those transitions.
