#!/usr/bin/env bash
set -euo pipefail

BASE_URL=""
DURATION_HOURS="30"
OUTPUT="artifacts/soak"
SUBURB="Pomona"
STATE="QLD"
INTERVAL_SECONDS="60"
CONTAINER=""

usage() {
  cat <<'USAGE'
Usage: ./scripts/soak-service.sh --base-url URL [options]

Required:
  --base-url URL          Running service base URL

Options:
  --duration-hours N      Trial duration in hours (default: 30; fractions allowed)
  --output DIR            Output directory (default: artifacts/soak)
  --suburb NAME           Location suburb (default: Pomona)
  --state ABBR            Location state (default: QLD)
  --interval-seconds N    Sampling interval (default: 60)
  --container NAME        Optional Docker container for memory/process/cache sampling
  -h, --help              Show this help

Samples are written to samples.jsonl. A summary is written on completion or
SIGINT/SIGTERM. The script exits nonzero if the trial finds no advancing
observations after the first two hours, or if the service never recovers.
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
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ -z "$BASE_URL" ]]; then
  echo "--base-url is required" >&2
  usage >&2
  exit 2
fi

mkdir -p "$OUTPUT"
BASE_URL="${BASE_URL%/}"

SOAK_BASE_URL="$BASE_URL" \
SOAK_DURATION_HOURS="$DURATION_HOURS" \
SOAK_OUTPUT="$OUTPUT" \
SOAK_SUBURB="$SUBURB" \
SOAK_STATE="$STATE" \
SOAK_INTERVAL_SECONDS="$INTERVAL_SECONDS" \
SOAK_CONTAINER="$CONTAINER" \
python3 - <<'PY'
import hashlib
import json
import os
import signal
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

base = os.environ["SOAK_BASE_URL"].rstrip("/")
duration = float(os.environ["SOAK_DURATION_HOURS"]) * 3600
output = os.environ["SOAK_OUTPUT"]
suburb = os.environ["SOAK_SUBURB"]
state = os.environ["SOAK_STATE"]
interval = max(5, int(os.environ["SOAK_INTERVAL_SECONDS"]))
container = os.environ.get("SOAK_CONTAINER", "")
samples_path = os.path.join(output, "samples.jsonl")
summary_path = os.path.join(output, "summary.json")
encoded_suburb = urllib.parse.quote(suburb, safe="")
encoded_state = urllib.parse.quote(state, safe="")
status_url = f"{base}/api/cache/{encoded_suburb}/{encoded_state}/status"
radar_url = f"{base}/api/radar/{encoded_suburb}/{encoded_state}"
refresh_url = f"{base}/api/cache/{encoded_suburb}/{encoded_state}/refresh"
ready_url = f"{base}/api/ready"
health_url = f"{base}/api/health"

stop = False

def handle_signal(signum, frame):
    global stop
    stop = True

signal.signal(signal.SIGINT, handle_signal)
signal.signal(signal.SIGTERM, handle_signal)

def request(url, method="GET", timeout=30):
    req = urllib.request.Request(url, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            body = response.read()
            return response.status, body
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()
    except Exception as exc:
        return None, str(exc).encode()

def parse_json(body):
    try:
        return json.loads(body)
    except Exception:
        return None

def docker_value(command):
    if not container:
        return None
    try:
        result = subprocess.run(
            ["docker", *command],
            check=False, capture_output=True, text=True, timeout=20)
        return result.stdout.strip() if result.returncode == 0 else None
    except Exception:
        return None

def parse_memory(text):
    if not text:
        return None
    first = text.split("/")[0].strip()
    factors = {"B": 1, "KiB": 1024, "MiB": 1024**2, "GiB": 1024**3}
    for suffix, factor in factors.items():
        if first.endswith(suffix):
            try:
                return float(first[:-len(suffix)].strip()) * factor
            except ValueError:
                return None
    return None

def sample_resources():
    memory = None
    processes = None
    cache_bytes = None
    if container:
        memory = parse_memory(docker_value(["stats", "--no-stream", "--format", "{{.MemUsage}}", container]))
        processes_text = docker_value(["exec", container, "sh", "-c",
            "ps -eo comm | grep -Ec '^(chrome|chromium|headless_shell)$' || true"])
        try:
            processes = int(processes_text) if processes_text is not None else None
        except ValueError:
            processes = None
        cache_text = docker_value(["exec", container, "sh", "-c", "du -sb /app/cache 2>/dev/null | cut -f1"])
        try:
            cache_bytes = int(cache_text) if cache_text else None
        except ValueError:
            cache_bytes = None
    return memory, processes, cache_bytes

def validate_frame(payload):
    if payload[:8] != b"\x89PNG\r\n\x1a\n" or len(payload) < 24:
        return None
    width, height = struct.unpack(">II", payload[16:24])
    return {"width": width, "height": height, "sha256": hashlib.sha256(payload).hexdigest()}

started = datetime.now(timezone.utc)
deadline = time.monotonic() + duration
sample_index = 0
successful = 0
failures = 0
observations = []
max_queue_depth = 0
max_memory = None
max_processes = None
cache_start = None
cache_end = None
last_frame_observation = None
frame_checks = 0
last_error = None

with open(samples_path, "w", encoding="utf-8") as samples:
    while not stop and time.monotonic() < deadline:
        sample_index += 1
        now = datetime.now(timezone.utc)
        health_status, _ = request(health_url, timeout=10)
        ready_status, ready_body = request(ready_url, timeout=10)
        ready = parse_json(ready_body) or {}
        status_code, status_body = request(status_url, timeout=15)
        status = parse_json(status_body) or {}
        radar_status, radar_body = request(radar_url, timeout=30)
        radar = parse_json(radar_body) or {}

        if radar_status == 404:
            request(refresh_url, method="POST", timeout=15)
            time.sleep(2)
            radar_status, radar_body = request(radar_url, timeout=30)
            radar = parse_json(radar_body) or {}

        frames = radar.get("frames") or []
        observation = radar.get("observationTime")
        frame_check = None
        if radar_status == 200 and frames and observation:
            successful += 1
            if observation != last_frame_observation or sample_index % 10 == 1:
                frame_url = frames[-1].get("imageUrl", "")
                if frame_url.startswith("/"):
                    frame_url = base + frame_url
                payload_status, payload = request(frame_url, timeout=30)
                if payload_status == 200:
                    frame_check = validate_frame(payload)
                    if frame_check is None:
                        last_error = "latest frame was not a decodable PNG"
                    else:
                        frame_checks += 1
            if observation not in observations:
                observations.append(observation)
            last_frame_observation = observation
            if last_error is None:
                last_error = None
        elif radar_status is not None:
            failures += 1
            last_error = (radar.get("message") or radar.get("errorCode") or f"HTTP {radar_status}")

        memory, processes, cache_bytes = sample_resources()
        if cache_start is None and cache_bytes is not None:
            cache_start = cache_bytes
        if cache_bytes is not None:
            cache_end = cache_bytes
        max_queue_depth = max(max_queue_depth, int(status.get("queueDepth") or ready.get("queueDepth") or 0))
        if memory is not None:
            max_memory = memory if max_memory is None else max(max_memory, memory)
        if processes is not None:
            max_processes = processes if max_processes is None else max(max_processes, processes)

        record = {
            "sampledAt": now.isoformat(),
            "sample": sample_index,
            "healthStatus": health_status,
            "readyStatus": ready_status,
            "ready": ready,
            "radarStatus": radar_status,
            "frameCount": len(frames),
            "observationTime": observation,
            "cacheIsValid": radar.get("cacheIsValid"),
            "isUpdating": status.get("isUpdating"),
            "updateFailed": status.get("updateFailed"),
            "errorCode": status.get("errorCode"),
            "nextRetryAt": status.get("nextRetryAt"),
            "lastUpdateAttempt": status.get("lastUpdateAttempt"),
            "lastSuccessfulCaptureAt": status.get("lastSuccessfulCaptureAt"),
            "queueDepth": status.get("queueDepth"),
            "memoryBytes": memory,
            "browserProcesses": processes,
            "cacheBytes": cache_bytes,
            "frameCheck": frame_check,
            "lastError": last_error
        }
        samples.write(json.dumps(record) + "\n")
        samples.flush()
        print(json.dumps({k: record[k] for k in ("sample", "radarStatus", "observationTime", "updateFailed", "queueDepth")}), flush=True)
        time.sleep(interval)

elapsed_hours = (datetime.now(timezone.utc) - started).total_seconds() / 3600
summary = {
    "status": "completed" if not stop else "interrupted",
    "startedAt": started.isoformat(),
    "finishedAt": datetime.now(timezone.utc).isoformat(),
    "durationHours": round(elapsed_hours, 3),
    "requestedHours": duration / 3600,
    "samples": sample_index,
    "successfulSamples": successful,
    "failedSamples": failures,
    "uniqueObservations": len(observations),
    "firstObservation": observations[0] if observations else None,
    "lastObservation": observations[-1] if observations else None,
    "observationAdvances": max(0, len(observations) - 1),
    "frameChecks": frame_checks,
    "maxQueueDepth": max_queue_depth,
    "maxMemoryBytes": max_memory,
    "maxBrowserProcesses": max_processes,
    "cacheBytesStart": cache_start,
    "cacheBytesEnd": cache_end,
    "lastError": last_error,
    "samplesFile": samples_path
}
with open(summary_path, "w", encoding="utf-8") as handle:
    json.dump(summary, handle, indent=2)

print(json.dumps(summary, indent=2))
if duration >= 7200 and len(observations) < 2:
    print("SOAK FAILED: no advancing observations after at least two hours", file=sys.stderr)
    sys.exit(1)
if successful == 0:
    print("SOAK FAILED: no successful radar sample", file=sys.stderr)
    sys.exit(1)
PY
