#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

BASE_URL=""
DURATION_HOURS="30"
OUTPUT="artifacts/soak"
SUBURB="Pomona"
STATE="QLD"
INTERVAL_SECONDS="60"
CONTAINER=""
RUN_ID="${BOM_TEST_RUN_ID:-}"
OWNER_TOKEN="${BOM_TEST_OWNER_TOKEN:-}"
MAX_FRESHNESS_SECONDS="1800"
FUTURE_SKEW_SECONDS="120"
MAX_NO_PROGRESS_SECONDS="3600"
RECOVERY_SECONDS="1800"
MAX_IMAGE_BYTES="26214400"
CONNECT_TIMEOUT="5"
REQUEST_TIMEOUT="30"
CANDIDATE_JSON=""
ARCHITECTURE=""
IMAGE_ID=""
IMAGE_ARCHIVE=""
SMOKE=0
MAX_MEMORY_MIB=""
MAX_BROWSER_PROCESSES=""
MAX_CACHE_GROWTH_MIB=""

usage() {
  cat <<'USAGE'
Usage: ./scripts/soak-service.sh --base-url URL [options]

Required:
  --base-url URL                 Running service base URL

Options:
  --duration-hours N             Trial duration (default: 30; fractions allowed)
  --output DIR                   Output directory (default: artifacts/soak)
  --suburb NAME                  Location suburb (default: Pomona)
  --state ABBR                   Location state (default: QLD)
  --interval-seconds N           Sampling interval (default: 60)
  --run-id ID                    Verified runner run id (default: BOM_TEST_RUN_ID)
  --owner-token TOKEN            Expected runner owner token (default: BOM_TEST_OWNER_TOKEN)
  --container ID                 Deprecated: must equal the verified full container id
  --max-freshness-seconds N      Recorded freshness policy (default: 1800)
  --future-skew-seconds N        Allowed future timestamp skew (default: 120)
  --max-no-progress-seconds N    No-forward-progress gate (default: 3600)
  --recovery-seconds N           Failure recovery gate (default: 1800)
  --max-image-bytes N            Maximum image payload (default: 26214400)
  --connect-timeout N            Per-request connect timeout (default: 5)
  --request-timeout N            Per-request total timeout (default: 30)
  --candidate-json PATH          candidate.json schema v1 identity
  --architecture ARCH            amd64 or arm64 (required for bound qualification)
  --image-id ID                  Actual inspected image ID (required for bound qualification)
  --image-archive PATH           Actual candidate archive for sidecar SHA-256 checking
  --max-memory-mib N             Optional empirical memory cap (no agreed default)
  --max-browser-processes N      Optional empirical process cap (no agreed default)
  --max-cache-growth-mib N       Optional empirical cache-growth cap (no agreed default)
  --smoke                        Short smoke only; never qualification evidence
  -h, --help                     Show this help

A qualification run requires 30h actual monotonic elapsed time, a validated
image each sample, forward observation progress, complete container metrics
from a verified runner run/token manifest with the exact full container and
image id, candidate identity, and explicitly supplied empirical resource
thresholds. Short or interrupted runs cannot qualify.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base-url) BASE_URL="${2:-}"; shift 2 ;;
    --duration-hours) DURATION_HOURS="${2:-}"; shift 2 ;;
    --output) OUTPUT="${2:-}"; shift 2 ;;
    --suburb) SUBURB="${2:-}"; shift 2 ;;
    --state) STATE="${2:-}"; shift 2 ;;
    --interval-seconds) INTERVAL_SECONDS="${2:-}"; shift 2 ;;
    --container) CONTAINER="${2:-}"; shift 2 ;;
    --run-id) RUN_ID="${2:-}"; shift 2 ;;
    --owner-token) OWNER_TOKEN="${2:-}"; shift 2 ;;
    --max-freshness-seconds) MAX_FRESHNESS_SECONDS="${2:-}"; shift 2 ;;
    --future-skew-seconds) FUTURE_SKEW_SECONDS="${2:-}"; shift 2 ;;
    --max-no-progress-seconds) MAX_NO_PROGRESS_SECONDS="${2:-}"; shift 2 ;;
    --recovery-seconds) RECOVERY_SECONDS="${2:-}"; shift 2 ;;
    --max-image-bytes) MAX_IMAGE_BYTES="${2:-}"; shift 2 ;;
    --connect-timeout) CONNECT_TIMEOUT="${2:-}"; shift 2 ;;
    --request-timeout) REQUEST_TIMEOUT="${2:-}"; shift 2 ;;
    --candidate-json) CANDIDATE_JSON="${2:-}"; shift 2 ;;
    --architecture) ARCHITECTURE="${2:-}"; shift 2 ;;
    --image-id) IMAGE_ID="${2:-}"; shift 2 ;;
    --image-archive) IMAGE_ARCHIVE="${2:-}"; shift 2 ;;
    --max-memory-mib) MAX_MEMORY_MIB="${2:-}"; shift 2 ;;
    --max-browser-processes) MAX_BROWSER_PROCESSES="${2:-}"; shift 2 ;;
    --max-cache-growth-mib) MAX_CACHE_GROWTH_MIB="${2:-}"; shift 2 ;;
    --smoke) SMOKE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ -z "$BASE_URL" ]]; then
  echo "--base-url is required" >&2
  usage >&2
  exit 2
fi

soak_args=(
  soak
  --repo-root "$REPO_ROOT"
  --base-url "${BASE_URL%/}"
  --suburb "$SUBURB"
  --state "$STATE"
  --output "$OUTPUT"
  --duration-hours "$DURATION_HOURS"
  --interval-seconds "$INTERVAL_SECONDS"
  --container "$CONTAINER"
  --max-freshness-seconds "$MAX_FRESHNESS_SECONDS"
  --future-skew-seconds "$FUTURE_SKEW_SECONDS"
  --max-no-progress-seconds "$MAX_NO_PROGRESS_SECONDS"
  --recovery-seconds "$RECOVERY_SECONDS"
  --max-image-bytes "$MAX_IMAGE_BYTES"
  --connect-timeout "$CONNECT_TIMEOUT"
  --request-timeout "$REQUEST_TIMEOUT"
)
[[ -n "$CANDIDATE_JSON" ]] && soak_args+=(--candidate-json "$CANDIDATE_JSON")
[[ -n "$ARCHITECTURE" ]] && soak_args+=(--architecture "$ARCHITECTURE")
[[ -n "$IMAGE_ID" ]] && soak_args+=(--image-id "$IMAGE_ID")
[[ -n "$IMAGE_ARCHIVE" ]] && soak_args+=(--image-archive "$IMAGE_ARCHIVE")
[[ -n "$RUN_ID" ]] && soak_args+=(--run-id "$RUN_ID")
[[ -n "$OWNER_TOKEN" ]] && soak_args+=(--owner-token "$OWNER_TOKEN")
[[ -n "$MAX_MEMORY_MIB" ]] && soak_args+=(--max-memory-mib "$MAX_MEMORY_MIB")
[[ -n "$MAX_BROWSER_PROCESSES" ]] && soak_args+=(--max-browser-processes "$MAX_BROWSER_PROCESSES")
[[ -n "$MAX_CACHE_GROWTH_MIB" ]] && soak_args+=(--max-cache-growth-mib "$MAX_CACHE_GROWTH_MIB")
[[ "$SMOKE" == "1" ]] && soak_args+=(--smoke)

exec python3 "$SCRIPT_DIR/service_verification.py" "${soak_args[@]}"
