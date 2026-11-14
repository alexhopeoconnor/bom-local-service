#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

BASE_URL=""
SUBURB="Pomona"
STATE="QLD"
OUTPUT="artifacts/verification"
EXPECTED_FRAMES="7"
WAIT_SECONDS="660"
MAX_FRESHNESS_SECONDS="1800"
FUTURE_SKEW_SECONDS="120"
MAX_IMAGE_BYTES="26214400"
CONNECT_TIMEOUT="5"
REQUEST_TIMEOUT="30"
CANDIDATE_JSON=""
ARCHITECTURE=""
IMAGE_ID=""
IMAGE_ARCHIVE=""
SMOKE=0

usage() {
  cat <<'USAGE'
Usage: ./scripts/verify-service.sh --base-url URL [options]

Required:
  --base-url URL                 Running service base URL (e.g. http://127.0.0.1:18082)

Options:
  --suburb NAME                  Location suburb (default: Pomona)
  --state ABBR                   Location state (default: QLD)
  --output DIR                   Verification output directory (default: artifacts/verification)
  --expected-frames N            Expected frames per snapshot (default: 7)
  --wait-seconds N               Cold-start wait (default: 660; service capture timeout 300)
  --max-freshness-seconds N      Maximum latest-observation age (default: 1800)
  --future-skew-seconds N        Allowed future timestamp skew (default: 120)
  --max-image-bytes N            Maximum image payload (default: 26214400)
  --connect-timeout N            Per-request connect timeout (default: 5)
  --request-timeout N            Per-request total timeout (default: 30)
  --candidate-json PATH          candidate.json schema v1 identity
  --architecture ARCH            amd64 or arm64 (required for bound qualification)
  --image-id ID                  Actual inspected image ID (required for bound qualification)
  --image-archive PATH           Actual candidate archive for sidecar SHA-256 checking
  --smoke                        Mark as smoke only; never qualification evidence
  -h, --help                     Show this help

The verifier exits nonzero when liveness, readiness, schema, timestamps,
freshness, snapshot ownership, historical bounds, or any returned frame image
fails validation. Missing candidate identity makes the run unqualified but does
not fabricate identity.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base-url) BASE_URL="${2:-}"; shift 2 ;;
    --suburb) SUBURB="${2:-}"; shift 2 ;;
    --state) STATE="${2:-}"; shift 2 ;;
    --output) OUTPUT="${2:-}"; shift 2 ;;
    --expected-frames) EXPECTED_FRAMES="${2:-}"; shift 2 ;;
    --wait-seconds) WAIT_SECONDS="${2:-}"; shift 2 ;;
    --max-freshness-seconds) MAX_FRESHNESS_SECONDS="${2:-}"; shift 2 ;;
    --future-skew-seconds) FUTURE_SKEW_SECONDS="${2:-}"; shift 2 ;;
    --max-image-bytes) MAX_IMAGE_BYTES="${2:-}"; shift 2 ;;
    --connect-timeout) CONNECT_TIMEOUT="${2:-}"; shift 2 ;;
    --request-timeout) REQUEST_TIMEOUT="${2:-}"; shift 2 ;;
    --candidate-json) CANDIDATE_JSON="${2:-}"; shift 2 ;;
    --architecture) ARCHITECTURE="${2:-}"; shift 2 ;;
    --image-id) IMAGE_ID="${2:-}"; shift 2 ;;
    --image-archive) IMAGE_ARCHIVE="${2:-}"; shift 2 ;;
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

BASE_URL="${BASE_URL%/}"
mkdir -p "$OUTPUT/frames" "$OUTPUT/history"

fail() {
  echo "VERIFY FAILED: $*" >&2
  exit 1
}

bounded_curl() {
  curl --silent --show-error \
    --connect-timeout "$CONNECT_TIMEOUT" \
    --max-time "$REQUEST_TIMEOUT" \
    --max-filesize "$MAX_IMAGE_BYTES" \
    "$@"
}

echo "== liveness and readiness =="
health_status=$(bounded_curl -o "$OUTPUT/health.txt" -w '%{http_code}' "$BASE_URL/api/health") \
  || fail "health request failed"
[[ "$health_status" == "200" ]] || fail "health returned $health_status"
ready_status=$(bounded_curl -o "$OUTPUT/ready.json" -w '%{http_code}' "$BASE_URL/api/ready") \
  || fail "ready request failed"
[[ "$ready_status" == "200" ]] || fail "ready returned $ready_status: $(cat "$OUTPUT/ready.json")"

encoded_suburb=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$SUBURB")
encoded_state=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$STATE")
radar_url="$BASE_URL/api/radar/$encoded_suburb/$encoded_state"
status_url="$BASE_URL/api/cache/$encoded_suburb/$encoded_state/status"
range_url="$BASE_URL/api/cache/$encoded_suburb/$encoded_state/range"
refresh_url="$BASE_URL/api/cache/$encoded_suburb/$encoded_state/refresh"

echo "== capture =="
status_code=$(bounded_curl -o "$OUTPUT/radar-latest.json" -w '%{http_code}' "$radar_url") \
  || fail "latest request failed"
if [[ "$status_code" != "200" ]]; then
  echo "latest returned $status_code; requesting refresh"
  refresh_status=$(bounded_curl -X POST -o "$OUTPUT/refresh.json" -w '%{http_code}' "$refresh_url") \
    || fail "refresh request failed"
  echo "refresh=$refresh_status"
  if [[ "$refresh_status" == "429" ]]; then
    fail "refresh rejected by backoff: $(cat "$OUTPUT/refresh.json")"
  fi
  deadline=$((SECONDS + WAIT_SECONDS))
  while [[ $SECONDS -lt $deadline ]]; do
    sleep 3
    status_code=$(bounded_curl -o "$OUTPUT/radar-latest.json" -w '%{http_code}' "$radar_url") \
      || fail "latest retry failed"
    [[ "$status_code" == "200" ]] && break
    bounded_curl -o "$OUTPUT/status.json" "$status_url" || true
    if python3 - "$OUTPUT/status.json" <<'PY'
import json,sys
try:
    d=json.load(open(sys.argv[1]))
except Exception:
    sys.exit(1)
sys.exit(0 if d.get("updateFailed") and d.get("nextRetryAt") else 1)
PY
    then
      fail "capture failed: $(cat "$OUTPUT/status.json")"
    fi
  done
  [[ "$status_code" == "200" ]] || fail "no radar data after ${WAIT_SECONDS}s: $(cat "$OUTPUT/radar-latest.json")"
fi

echo "== cache range and history =="
range_status=$(bounded_curl -o "$OUTPUT/range.json" -w '%{http_code}' "$range_url") \
  || fail "range request failed"
[[ "$range_status" == "200" ]] || fail "range returned $range_status: $(cat "$OUTPUT/range.json")"

start_time=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["frames"][0]["absoluteObservationTime"])' "$OUTPUT/radar-latest.json")
end_time=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["frames"][-1]["absoluteObservationTime"])' "$OUTPUT/radar-latest.json")
timeseries_url="$radar_url/timeseries?startTime=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$start_time")&endTime=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$end_time")"
history_status=$(bounded_curl -o "$OUTPUT/history/timeseries.json" -w '%{http_code}' "$timeseries_url") \
  || fail "timeseries request failed"
[[ "$history_status" == "200" ]] || fail "timeseries returned $history_status: $(cat "$OUTPUT/history/timeseries.json")"

verification_args=(
  verify
  --repo-root "$REPO_ROOT"
  --base-url "$BASE_URL"
  --suburb "$SUBURB"
  --state "$STATE"
  --output "$OUTPUT"
  --expected-frames "$EXPECTED_FRAMES"
  --wait-seconds "$WAIT_SECONDS"
  --max-freshness-seconds "$MAX_FRESHNESS_SECONDS"
  --future-skew-seconds "$FUTURE_SKEW_SECONDS"
  --max-image-bytes "$MAX_IMAGE_BYTES"
  --connect-timeout "$CONNECT_TIMEOUT"
  --request-timeout "$REQUEST_TIMEOUT"
  --health-status "$health_status"
  --ready-status "$ready_status"
)
[[ -n "$CANDIDATE_JSON" ]] && verification_args+=(--candidate-json "$CANDIDATE_JSON")
[[ -n "$ARCHITECTURE" ]] && verification_args+=(--architecture "$ARCHITECTURE")
[[ -n "$IMAGE_ID" ]] && verification_args+=(--image-id "$IMAGE_ID")
[[ -n "$IMAGE_ARCHIVE" ]] && verification_args+=(--image-archive "$IMAGE_ARCHIVE")
[[ "$SMOKE" == "1" ]] && verification_args+=(--smoke)

python3 "$SCRIPT_DIR/service_verification.py" "${verification_args[@]}"
