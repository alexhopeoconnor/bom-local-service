#!/usr/bin/env bash
set -euo pipefail

BASE_URL=""
SUBURB="Pomona"
STATE="QLD"
OUTPUT="artifacts/verification"
EXPECTED_FRAMES="7"
WAIT_SECONDS="240"

usage() {
  cat <<'USAGE'
Usage: ./scripts/verify-service.sh --base-url URL [options]

Required:
  --base-url URL          Running service base URL (e.g. http://127.0.0.1:18082)

Options:
  --suburb NAME           Location suburb (default: Pomona)
  --state ABBR            Location state (default: QLD)
  --output DIR            Verification output directory (default: artifacts/verification)
  --expected-frames N     Expected frames per snapshot (default: 7)
  --wait-seconds N        Cold-start capture timeout (default: 240)
  -h, --help              Show this help

The verifier exits nonzero when liveness, readiness, schema, timestamps,
historical bounds, or any returned frame image fails validation.
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

json_get() {
  python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(eval(sys.argv[2], {"d": d}, {"d": d}))' "$1" "$2"
}

fail() {
  echo "VERIFY FAILED: $*" >&2
  exit 1
}

echo "== liveness and readiness =="
health_status=$(curl -sS -o "$OUTPUT/health.txt" -w '%{http_code}' "$BASE_URL/api/health")
[[ "$health_status" == "200" ]] || fail "health returned $health_status"
ready_status=$(curl -sS -o "$OUTPUT/ready.json" -w '%{http_code}' "$BASE_URL/api/ready")
[[ "$ready_status" == "200" ]] || fail "ready returned $ready_status: $(cat "$OUTPUT/ready.json")"
python3 - "$OUTPUT/ready.json" <<'PY'
import json,sys
d=json.load(open(sys.argv[1]))
assert d.get("status")=="ready", d
assert d.get("checks",{}).get("cacheWritable") is True, d
assert d.get("checks",{}).get("refreshWorker") is True, d
PY

encoded_suburb=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$SUBURB")
encoded_state=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$STATE")
radar_url="$BASE_URL/api/radar/$encoded_suburb/$encoded_state"
status_url="$BASE_URL/api/cache/$encoded_suburb/$encoded_state/status"
range_url="$BASE_URL/api/cache/$encoded_suburb/$encoded_state/range"
refresh_url="$BASE_URL/api/cache/$encoded_suburb/$encoded_state/refresh"

echo "== capture =="
status_code=$(curl -sS -o "$OUTPUT/radar-latest.json" -w '%{http_code}' "$radar_url")
if [[ "$status_code" != "200" ]]; then
  echo "latest returned $status_code; requesting refresh"
  curl -sS -X POST -o "$OUTPUT/refresh.json" -w 'refresh=%{http_code}\n' "$refresh_url" || true
  deadline=$((SECONDS + WAIT_SECONDS))
  while [[ $SECONDS -lt $deadline ]]; do
    sleep 3
    status_code=$(curl -sS -o "$OUTPUT/radar-latest.json" -w '%{http_code}' "$radar_url")
    [[ "$status_code" == "200" ]] && break
    curl -sS -o "$OUTPUT/status.json" "$status_url" || true
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

python3 - "$OUTPUT" "$BASE_URL" "$EXPECTED_FRAMES" <<'PY'
import hashlib,json,os,struct,sys,urllib.request
from datetime import datetime, timezone

output, base, expected_text = sys.argv[1], sys.argv[2].rstrip('/'), sys.argv[3]
expected = int(expected_text)
latest = json.load(open(os.path.join(output, "radar-latest.json")))
frames = latest.get("frames") or []
assert len(frames) == expected, f"expected {expected} frames, got {len(frames)}"
assert latest.get("observationTime"), "missing observationTime"

def parse_utc(value):
    assert value, "missing timestamp"
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    assert parsed.tzinfo is not None, f"timestamp lacks offset: {value}"
    return parsed.astimezone(timezone.utc)

observation = parse_utc(latest["observationTime"])
times = [parse_utc(frame["absoluteObservationTime"]) for frame in frames]
assert times == sorted(times), f"frames not chronological: {times}"
assert len(set(times)) == len(times), f"duplicate frame observations: {times}"
assert all(abs((observation - value).total_seconds()) <= 7200 for value in times), "frame observation implausible relative to metadata"
assert all("cacheFolder=" in frame.get("imageUrl", "") for frame in frames), "frame URLs do not identify a snapshot"

manifest = {"frames": [], "observationTime": latest["observationTime"], "cacheFolder": None}
for frame in frames:
    url = frame["imageUrl"]
    if url.startswith("/"):
        url = base + url
    data = urllib.request.urlopen(url, timeout=30).read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", f"not PNG: {url}"
    width, height = struct.unpack(">II", data[16:24])
    assert width > 0 and height > 0, f"invalid PNG dimensions: {url}"
    name = f"frame_{frame['frameIndex']}.png"
    path = os.path.join(output, "frames", name)
    open(path, "wb").write(data)
    manifest["frames"].append({
        "frameIndex": frame["frameIndex"],
        "observationTime": frame["absoluteObservationTime"],
        "imageUrl": frame["imageUrl"],
        "bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "width": width,
        "height": height
    })

json.dump(manifest, open(os.path.join(output, "frame-manifest.json"), "w"), indent=2)
print(f"validated {len(frames)} latest frames, observation {latest['observationTime']}")
PY

echo "== cache range and history =="
range_status=$(curl -sS -o "$OUTPUT/range.json" -w '%{http_code}' "$range_url")
[[ "$range_status" == "200" ]] || fail "range returned $range_status: $(cat "$OUTPUT/range.json")"

start_time=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["frames"][0]["absoluteObservationTime"])' "$OUTPUT/radar-latest.json")
end_time=$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["frames"][-1]["absoluteObservationTime"])' "$OUTPUT/radar-latest.json")
timeseries_url="$radar_url/timeseries?startTime=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$start_time")&endTime=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$end_time")"
history_status=$(curl -sS -o "$OUTPUT/history/timeseries.json" -w '%{http_code}' "$timeseries_url")
[[ "$history_status" == "200" ]] || fail "timeseries returned $history_status: $(cat "$OUTPUT/history/timeseries.json")"

python3 - "$OUTPUT" "$BASE_URL" "$start_time" "$end_time" <<'PY'
import hashlib,json,os,struct,sys,urllib.parse,urllib.request
from datetime import datetime, timezone

output, base, start_text, end_text = sys.argv[1], sys.argv[2].rstrip('/'), sys.argv[3], sys.argv[4]
start = datetime.fromisoformat(start_text.replace("Z", "+00:00")).astimezone(timezone.utc)
end = datetime.fromisoformat(end_text.replace("Z", "+00:00")).astimezone(timezone.utc)
data = json.load(open(os.path.join(output, "history", "timeseries.json")))
assert data.get("cacheFolders"), "empty cacheFolders grouping"
assert data.get("totalFrames") == sum(len(f["frames"]) for f in data["cacheFolders"]), "totalFrames mismatch"
manifest = []
for folder in data["cacheFolders"]:
    assert folder.get("cacheFolderName"), "missing cacheFolderName"
    assert folder.get("frames"), "empty cache folder group"
    for frame in folder["frames"]:
        observed = datetime.fromisoformat(frame["absoluteObservationTime"].replace("Z", "+00:00")).astimezone(timezone.utc)
        assert start <= observed <= end, f"observation {observed} outside inclusive bounds {start}..{end}"
        assert f"cacheFolder={urllib.parse.quote(folder['cacheFolderName'], safe='')}" in frame["imageUrl"] or folder["cacheFolderName"] in frame["imageUrl"], "frame URL lost origin folder"
        url = frame["imageUrl"] if frame["imageUrl"].startswith("http") else base + frame["imageUrl"]
        payload = urllib.request.urlopen(url, timeout=30).read()
        assert payload[:8] == b"\x89PNG\r\n\x1a\n", f"historical frame not PNG: {url}"
        width, height = struct.unpack(">II", payload[16:24])
        assert width > 0 and height > 0
        manifest.append({
            "cacheFolderName": folder["cacheFolderName"],
            "frameIndex": frame["frameIndex"],
            "observationTime": frame["absoluteObservationTime"],
            "imageUrl": frame["imageUrl"],
            "sha256": hashlib.sha256(payload).hexdigest(),
            "width": width,
            "height": height
        })
assert manifest, "no historical frames validated"
json.dump(manifest, open(os.path.join(output, "history", "frame-manifest.json"), "w"), indent=2)
print(f"validated {len(manifest)} historical frames across {len(data['cacheFolders'])} group(s)")
PY

cat > "$OUTPUT/report.json" <<EOF
{
  "generatedAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "baseUrl": "$BASE_URL",
  "location": { "suburb": "$SUBURB", "state": "$STATE" },
  "status": "passed"
}
EOF

echo "PASS: verification report written to $OUTPUT/report.json"
