# Local service qualification

Status: **implementation in progress; unreleased and unqualified; audited runner gate passed; build passed; regression and service verification in progress**.

This document tracks the agreed service-only 20-step plan. Card and Home Assistant
implementation are deferred. Publication, GitHub writes and live deployment are
not authorized by implementation approval.

## Starting point

- Implementation checkpoint: `ec75b98fbede7b948cb4c23ce2f262b1bde6386c`.
- Documentation checkpoint: `f3dea0573bddf3c3d0cac042670584693a601236`.
- Working branch: `fix/service-stability`, initially clean.
- Retained earlier evidence: 66 deterministic tests passed; that run excluded
  browser tests. Earlier live reports and the interrupted approximately 140-second
  soak do not qualify this candidate. The previous dependency audit was unavailable.

## Contracts that must remain true

Snapshots publish by same-filesystem atomic rename only after validation. Readers
must use exact location ownership and pinned snapshot image URLs. History bounds
are inclusive UTC observation bounds, with chronological selection and exact,
deterministic deduplication. Complete supported legacy snapshots remain readable.

Only the refresh worker owns capture admission and execution. It restores durable
state before accepting requests or reporting readiness. Admission, reservations,
queue accounting and shutdown share one synchronization policy. Deleting snapshots
continues monitoring; durable registration must succeed before the only discovery
record is removed. Live operational state remains authoritative during a process;
failed persistence must never be reported as durable success.

Every test owns its processes/containers, temporary cache and port. Service tests
are gated on verified runner cleanup. Test lifetimes and resources are bounded;
cleanup includes Chromium/Xvfb children, with no broad process-name or container
cleanup. Inspection failures are not proof of successful cleanup.

The browser implementation bounds callers' waits and observes/reclaims late
results. A close/dispose operation that never returns is currently reported as
unclean; controlled lifecycle and four actual Chromium boundary checks now pass.
Owned test units have verified residual-process cleanup. Native image fault
qualification remains outstanding.
The native and fault checks must establish resource release before qualification.
Cache path checks reject observed symlinks and revalidate operations, but do not
provide descriptor-based protection against another process replacing directories
between validation and use.

## Qualification policy v1

These criteria are established before qualification; changes require an explicit
new policy identity and appropriate requalification.

| Criterion | Required value |
| --- | --- |
| Completed trial | At least 108000 seconds (30 hours), measured monotonically |
| Outer test lifetime | At most 111600 seconds (31 hours), plus bounded teardown |
| Latest observation freshness | At most 1800 seconds old at a healthy successful sample |
| Future observation clock tolerance | At most 120 seconds |
| Recovery after an injected fault is removed | Valid fresh images and resumed progress within 1800 seconds |
| Progression | No interval longer than 3600 seconds without a strictly newer observation after initial success |
| Request bounds | 5-second connect and 30-second total budget where supported |
| Initial capture wait | Configurable; initial default 660 seconds for two queued locations |
| Container resource caps | 2 CPUs and 2 GiB memory; no automatic restart |
| Native platform evidence | Native amd64; ARM64 testing explicitly deferred by the user on 2026-11-15 |
| Live locations | Separate Pomona QLD and Sydney NSW verification |
| Completion | No interruption, unresolved required check or unavailable required metric |
| Cleanup | No remaining owned service/browser processes or containers |

Process-count and sustained memory/cache-growth thresholds remain unresolved until
short native baselines establish platform expectations. They must be recorded in
the candidate policy before the 30-hour trial. A short completed smoke run is not
a qualifying trial. Fault windows and removal times must be explicit in evidence;
ordinary outages cannot be silently reclassified as planned faults.

The PNG validation memo stores at most 8192 file fingerprints and evicts one
old entry at a time; decoded image pixels are not retained. A focused probe of
1400 retained 1336×1024 radar PNGs reduced the second scan from 30.2 seconds and
8.75 GB cumulative allocation to 2.0 ms and 339 KB. Six snapshot-validation cases
pass, including revalidation after file corruption. This fixes the earlier
512-entry whole-cache clear, which repeatedly decoded a normal two-location
retained history. Probe figures describe that fixture, not a production SLA.

## Frozen runtime configuration

The owning runner records application overrides in `ownership.json` through
`--service-env APPKEY=value`. Container mode maps approved application keys to
Compose interpolation; inherited `BOM_TEST_*` values cannot change the run.
Default live settings match the service: cache expiration 12.5 minutes,
management check interval 5 minutes, initial delay 10 seconds, capture deadline
300 seconds and backoff 60–900 seconds. The seeded image smoke explicitly uses
an initial delay of 3600 seconds to prevent upstream captures during fixtures.
Fault tests record their controlled URL/deadline overrides separately.

The disposable container runs as the host runner UID/GID so generated cache
files remain removable by that runner. Port, cache, image, resource and ownership
variables remain runner-owned. A configuration-only Compose check and a live
cache-writing container fixture verify these settings and teardown.

Image preparation may collect short resource baselines before the final policy
is frozen. A resolved policy is copied and hashed into the candidate manifest;
preliminary baseline reports are not qualification evidence. The final trial
must use the same retained image bytes and the frozen policy/configuration.

## Ordered completion register

| Step | Requirement | Current status | Expected evidence |
| --- | --- | --- | --- |
| 1 | Baseline, criteria and ownership | In progress | This document; execution ledger |
| 2 | Owning test runner and teardown | Passed: independent source audit, 67 pure checks and 11 native/container fixtures, plus the cache-write/UID fixture; verified teardown and empty residual inventory | Incident, pure ownership checks, then isolated fixtures |
| 3 | Full filesystem containment | Focused containment assertions passed in first deterministic run | Outside sentinel fixtures |
| 4 | Corruption handling and bounded PNG decode | Bounded decoder and corruption assertions passed; repaired malformed synthetic legacy PNGs pass compatibility tests | Snapshot regression tests |
| 5 | Exact reversible location identity | Round-trip, canonical parser and capacity assertions passed | Round-trip and legacy tests |
| 6 | API validation and history guarantees | API error/history assertions passed | HTTP and history regression results |
| 7 | Restored-state admission and shutdown | Admission/readiness assertions passed; focused deadline cleanup check passed after observing reservation release | Controlled state-load and shutdown tests |
| 8 | Browser deadlines and ownership | Controlled tests and all four real Chromium cases passed | Boundary tests and real Chromium evidence |
| 9 | Durable monitoring and DELETE | Durable save, failure, DELETE and restart assertions passed | Save-failure, DELETE and restart tests |
| 10 | Demo recovery and polling | Six demo browser scenarios passed across initial/focused runs | Browser assertions |
| 11 | Explicit browser execution | Ten cases executed with no skips; owned teardown verified | Dedicated browser TRX and cleanup |
| 12 | Provenance-aware live verifier | 70 pure checks pass across shared verifier/soak code; live verification unexecuted | Negative fixtures and candidate report |
| 13 | Honest soak metrics and qualification | Actual-loop simulations pass after summary and progression corrections; no real trial run | Duration/interruption/recovery fixtures |
| 14 | CI checks and unconditional teardown | Local checks and workflow integration pass; GitHub jobs not dispatched | Workflow validation and local equivalent checks |
| 15 | Exact-image promotion automation | Preparation/promotion helpers verified; image export/promotion unexecuted | Candidate exports and promotion checks |
| 16 | Signed source and frozen candidate | Signed implementation checkpoint 18f5655; final freeze pending | Signature output; candidate manifest |
| 17 | Native amd64 image and two locations | Unverified; ARM64 deferred | amd64 and per-location reports |
| 18 | Fault/restart/upgrade/rollback | Helper implemented; 27 pure tests pass; mutation boundary reviewed; live exercises unverified | Isolated exercise reports |
| 19 | Complete 30-hour trial | Not started | Samples, summary and teardown report |
| 20 | Evidence and publication material | Pending qualification | Release notes and gap list |

## Evidence and ownership

On 2026-11-15 Brisbane time, the initial local runner smoke test incorrectly
treated the inherited `alex-pc-paseo.service` cgroup as run-owned and sent SIGTERM
to 101 processes. This interrupted Paseo and its workers. Evidence is retained at
`artifacts/execution/runner/runs/selftest-smoke-001/summary.json`. Earlier cleanup
success flags do not establish isolation. The original worker was archived with
user authorization. `artifacts/execution/runner/SAFETY_HOLD` disables execution
if a future execution hold is needed. The dedicated-unit-only replacement passed
independent ownership review and 11 live lifecycle fixtures; the hold is now removed. No ambient-cgroup or process-group sweep is permitted.

Execution evidence belongs under ignored `artifacts/execution/<scope>/`.
Candidate-specific evidence belongs under ignored `artifacts/qualification/<id>/`;
large image exports may be retained in an explicitly recorded external artifact
directory. Never commit runtime state, credentials, local overrides or image
archives. Record source SHA, non-secret effective configuration hash, script and
policy identities, per-architecture image IDs/archive hashes, and registry digests
when available. Reports must not invent provenance for unbound runs.

The machine-readable policy is `.github/qualification-policy.json`. ARM64 is not
a prerequisite for the current amd64 qualification and must not be represented as
tested in publication material.

The coordinator owns integration, staging, signed commits, this document, README,
CHANGELOG, project/lockfiles and API/history changes. Paseo workers have exclusive
file assignments recorded in `artifacts/execution/ownership.json`. Workers must
report conflicts rather than edit another owner's files. Build/test execution
requires a coordinator lease to avoid concurrent output-directory writes.

Substantive source, configuration or packaging changes invalidate the affected
candidate evidence. Publication promotes retained tested image content without a
rebuild. Later documentation-only commits are distinguished from tested source.
