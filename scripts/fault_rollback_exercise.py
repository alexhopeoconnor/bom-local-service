#!/usr/bin/env python3
"""Bounded Step 18 fault/restart/upgrade/rollback exercise helper.

This script is a test command for an already-owned runner run. It does not
start, build, pull, remove or clean any runner resource. It verifies the
immutable ownership manifest and the exact full container ID before every
Docker mutation, uses only the manifest cache directory for service data, and
writes stage evidence under the run evidence directory.

Subcommands
-----------
fault              Main upstream-failure/recovery, restart-backoff,
                   interrupted-staging, DELETE-conflict and state-write case.
timeout            Controlled 5-second capture-deadline case (fallback only).
upgrade-prepare    Old retained image: seed a synthetic legacy snapshot, prove
                   it serves, copy the cache to the exercise backup directory.
upgrade-candidate  Candidate image: read the legacy seed, capture once, preserve
                   it, copy the upgraded cache to the exercise directory.
rollback           Old retained image: serve the backup copy only.

Environment contract (all values are supplied by the runner or the coordinator;
no other variable is read):

- BOM_TEST_RUN_ID, BOM_TEST_OWNER_TOKEN, BOM_TEST_CACHE_DIR,
  BOM_TEST_BASE_URL: runner-provided for the already-owned run.
- BOM_EXERCISE_DATA_DIR: required for upgrade-prepare, upgrade-candidate and
  rollback; the same absolute coordinator-owned directory must be passed to all
  three stages. It must already exist strictly below
  artifacts/execution/fault-rollback/data with no symlink component, and contain
  `.fault-rollback-owner.json` with
  {"schema": "fault-rollback-data/v1"}. The coordinator creates it and cleans
  that exact tree after the evidence hashes are retained.
- BOM_EXERCISE_OLD_IMAGE: optional exact retained-baseline image ID; defaults
  to the coordinator-supplied local baseline ID in this file. No pull occurs.

Synthetic seeds are labelled as synthetic in evidence. No production source,
runner, compose or workflow change is made here.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import shutil
import sys
import time
import urllib.parse
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

import service_test_runner as runner  # noqa: E402
import service_verification as sv  # noqa: E402


def _load_check_candidate() -> Any:
    path = ROOT / "scripts" / "check-candidate-image.py"
    spec = importlib.util.spec_from_file_location("bom_check_candidate_image", path)
    if spec is None or spec.loader is None:  # pragma: no cover - import failure
        raise RuntimeError(f"cannot load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


cc = _load_check_candidate()

RUN_ID_RE = re.compile(r"\d{8}T\d{6}Z-[0-9a-f]{8}")
TOKEN_RE = re.compile(r"[0-9a-f]{64}")
CONTAINER_ID_RE = re.compile(r"[0-9a-f]{64}")
IMAGE_ID_RE = re.compile(r"sha256:[0-9a-f]{64}")

OLD_BASELINE_IMAGE = "sha256:f08b8906b344245016eb9280ede9377bd07d57293d829b09e886ccdbdc1c3e73"
FAULT_PORT = 19099
FAULT_MODES = ("redirect", "fault")
NODE_PATHS = (
    "/app/.playwright/node/linux-x64/node",
    "/app/.playwright/node/linux-arm64/node",
)
FRESH_SECONDS = 1800
FUTURE_SKEW_SECONDS = 120
RECOVERY_SECONDS = 1800
CAPTURE_WAIT_SECONDS = 660
FAULT_STATUS_WAIT_SECONDS = 120
READY_WAIT_SECONDS = 600
STAGING_WAIT_SECONDS = 180
FRESH_WAIT_SECONDS = 900
STATUS_POLL_SECONDS = 1.0
TIMEOUT_SEED_AGE_SECONDS = 3600
MAX_COMPLETED_WAIT_CYCLES = 1
HTTP_CONNECT_TIMEOUT = 5.0
HTTP_TOTAL_TIMEOUT = 30.0
MAX_EVIDENCE_BYTES = 1024 * 1024
MAX_LOG_BYTES = 2 * 1024 * 1024


class ExerciseError(RuntimeError):
    """A bounded exercise check failed."""


class UnsafeState(ExerciseError):
    """Ownership or evidence state is uncertain; no mutation is permitted."""


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def utc_text(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def evidence_safe(value: Any) -> Any:
    """Make check/evidence details safe for the strict JSON writer."""
    if isinstance(value, datetime):
        return utc_text(value)
    if isinstance(value, Path):
        return str(value)
    if isinstance(value, dict):
        return {str(key): evidence_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [evidence_safe(item) for item in value]
    return value


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_json(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")


def write_json_exclusive(path: Path, value: Any) -> None:
    """Create a new evidence/manifest file without replacing an earlier one."""
    if path.exists() or path.is_symlink():
        raise ExerciseError(f"refusing to replace existing data evidence: {path}")
    data = json.dumps(value, indent=2, sort_keys=True).encode("utf-8") + b"\n"
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    flags |= getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags, 0o600)
    try:
        os.write(descriptor, data)
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def read_json(path: Path) -> Any:
    if path.is_symlink():
        raise UnsafeState(f"refusing symlink JSON path: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def status_value(status: dict[str, Any] | None, name: str) -> Any:
    if not isinstance(status, dict):
        return None
    if name in status:
        return status[name]
    pascal = name[:1].upper() + name[1:]
    return status.get(pascal)


def error_value(body: Any, name: str) -> Any:
    if not isinstance(body, dict):
        return None
    if name in body:
        return body[name]
    pascal = name[:1].upper() + name[1:]
    return body.get(pascal)


def parse_status_time(status: dict[str, Any] | None, name: str) -> datetime | None:
    value = status_value(status, name)
    if not isinstance(value, str) or not value.strip():
        return None
    try:
        return sv.parse_utc(value, name)
    except sv.VerificationError:
        return None


def validate_manifest_scope(
    manifest: dict[str, Any],
    *,
    run_id: str,
    token: str,
    cache_env: str,
    base_url: str,
) -> list[str]:
    errors: list[str] = []
    if not RUN_ID_RE.fullmatch(run_id):
        errors.append("BOM_TEST_RUN_ID is not the runner format")
    if not TOKEN_RE.fullmatch(token):
        errors.append("BOM_TEST_OWNER_TOKEN is not the runner format")
    if not isinstance(manifest, dict):
        return ["ownership manifest is not an object"]
    if manifest.get("runId") != run_id:
        errors.append("manifest run id does not match the environment")
    if manifest.get("ownerToken") != token:
        errors.append("manifest owner token does not match the environment")
    if manifest.get("mode") != "container":
        errors.append("exercise requires a container run")
    if manifest.get("cacheOwned") is not True:
        errors.append("manifest does not declare an owned cache")
    try:
        if Path(str(manifest.get("cacheDir") or "")).resolve() != Path(cache_env).resolve():
            errors.append("BOM_TEST_CACHE_DIR does not match the manifest cache")
    except OSError as exc:
        errors.append(f"cache path cannot be resolved: {exc}")
    parsed = urllib.parse.urlsplit(base_url or "")
    if parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost", "::1"):
        errors.append(f"base URL is not loopback HTTP: {base_url!r}")
    network = manifest.get("network") or {}
    expected_base = f"http://{network.get('host')}:{network.get('port')}"
    if base_url and base_url.rstrip("/") != expected_base:
        errors.append("BOM_TEST_BASE_URL does not match the manifest network")
    container = manifest.get("container") or {}
    if not IMAGE_ID_RE.fullmatch(str(container.get("imageId") or "")):
        errors.append("manifest container image ID is not exact")
    if not isinstance(container.get("project"), str) or not container["project"]:
        errors.append("manifest container project is missing")
    return errors


def validate_container_identity(
    container_id: str,
    inspected: dict[str, Any],
    manifest: dict[str, Any],
    *,
    run_id: str,
    token: str,
    require_running: bool,
) -> list[str]:
    errors: list[str] = []
    if not CONTAINER_ID_RE.fullmatch(container_id):
        errors.append("container ID is not a full 64-hex ID")
    if not isinstance(inspected, dict):
        return errors + ["container inspect result is not an object"]
    if inspected.get("Id") != container_id:
        errors.append("inspect ID does not match the listed container")
    container = manifest.get("container") or {}
    if inspected.get("Image") != container.get("imageId"):
        errors.append("container image ID does not match the manifest")
    labels = (inspected.get("Config") or {}).get("Labels") or {}
    expected = {
        cc.RUN_LABEL: run_id,
        cc.TOKEN_LABEL: token,
        cc.PROJECT_LABEL: container.get("project"),
        "io.bom.test.runner-version": manifest.get("runnerVersion"),
        "io.bom.test.role": "service",
    }
    for key, value in expected.items():
        if labels.get(key) != value:
            errors.append(f"container label {key} does not match")
    state = inspected.get("State") or {}
    if require_running and state.get("Running") is not True:
        errors.append("container is not running")
    return errors


def recovery_window(start: datetime, end: datetime, limit: int = RECOVERY_SECONDS) -> dict[str, Any]:
    seconds = (end - start).total_seconds()
    return {
        "start": utc_text(start),
        "end": utc_text(end),
        "seconds": max(0.0, seconds),
        "limitSeconds": limit,
        "ok": 0 <= seconds <= limit,
    }


def tree_manifest(root: Path) -> dict[str, Any]:
    root = root.resolve()
    if not root.is_dir():
        raise ExerciseError(f"tree root is not a directory: {root}")
    entries: dict[str, Any] = {}
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        base = Path(dirpath)
        for name in dirnames:
            path = base / name
            if path.is_symlink():
                raise UnsafeState(f"refusing symlink in exercise tree: {path}")
            entries[path.relative_to(root).as_posix()] = {"kind": "dir"}
        for name in filenames:
            path = base / name
            if path.is_symlink():
                raise UnsafeState(f"refusing symlink in exercise tree: {path}")
            if not path.is_file():
                raise UnsafeState(f"refusing special file in exercise tree: {path}")
            entries[path.relative_to(root).as_posix()] = {
                "kind": "file",
                "bytes": path.stat().st_size,
                "sha256": sha256_file(path),
            }
    encoded = canonical_json(entries)
    return {
        "root": str(root),
        "entries": entries,
        "treeSha256": sha256_bytes(encoded),
        "fileCount": sum(1 for value in entries.values() if value["kind"] == "file"),
    }


def verify_tree_manifest(root: Path, expected: dict[str, Any], label: str) -> None:
    actual = tree_manifest(root)
    if actual["treeSha256"] != expected.get("treeSha256"):
        raise ExerciseError(f"{label} tree hash changed")
    if actual["entries"] != expected.get("entries"):
        raise ExerciseError(f"{label} entries changed")


def copy_tree_verified(source: Path, destination: Path) -> dict[str, Any]:
    source = source.resolve()
    destination = destination.resolve()
    if not source.is_dir():
        raise ExerciseError(f"copy source is not a directory: {source}")
    if destination.exists():
        raise ExerciseError(f"copy destination already exists: {destination}")
    for path in source.rglob("*"):
        if path.is_symlink():
            raise UnsafeState(f"refusing to copy symlink: {path}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, destination, symlinks=False, copy_function=shutil.copy2)
    expected = tree_manifest(source)
    actual = tree_manifest(destination)
    if actual["treeSha256"] != expected["treeSha256"]:
        raise ExerciseError("copied tree does not match its source")
    return actual


def copy_tree_into_empty(source: Path, destination: Path) -> dict[str, Any]:
    """Copy into an existing, empty owned cache directory."""
    source = source.resolve()
    destination = destination.resolve()
    if not source.is_dir():
        raise ExerciseError(f"copy source is not a directory: {source}")
    if not destination.is_dir():
        raise ExerciseError(f"copy destination is not a directory: {destination}")
    if any(destination.iterdir()):
        raise ExerciseError(f"copy destination is not empty: {destination}")
    for path in source.rglob("*"):
        if path.is_symlink():
            raise UnsafeState(f"refusing to copy symlink: {path}")
    shutil.copytree(source, destination, dirs_exist_ok=True, symlinks=False, copy_function=shutil.copy2)
    expected = tree_manifest(source)
    actual = tree_manifest(destination)
    if actual["treeSha256"] != expected["treeSha256"]:
        raise ExerciseError("copied cache contents do not match the source")
    return actual


def clear_directory_contents(directory: Path) -> None:
    directory = directory.resolve()
    if not directory.is_dir():
        raise ExerciseError(f"cache directory is missing: {directory}")
    for child in list(directory.iterdir()):
        if child.is_symlink():
            raise UnsafeState(f"refusing symlink cache child: {child}")
        if child.is_dir():
            shutil.rmtree(child)
        else:
            child.unlink()


def staging_entries(cache_dir: Path) -> list[str]:
    root = cache_dir / ".staging"
    if not root.is_dir():
        return []
    entries: list[str] = []
    for child in root.iterdir():
        if child.is_symlink():
            raise UnsafeState(f"refusing symlink staging entry: {child}")
        if child.is_dir():
            entries.append(child.name)
    return sorted(entries)


def cache_folders(cache_dir: Path) -> list[Path]:
    if not cache_dir.is_dir():
        return []
    result: list[Path] = []
    for child in cache_dir.iterdir():
        if child.is_symlink():
            raise UnsafeState(f"refusing symlink cache entry: {child}")
        if child.is_dir() and child.name not in (".staging", ".service", ".exercise-state-backup"):
            result.append(child)
    return sorted(result, key=lambda path: path.name)


def incomplete_folders(cache_dir: Path) -> list[str]:
    return [path.name for path in cache_folders(cache_dir) if not (path / ".complete").is_file()]


def complete_folder_hashes(cache_dir: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    for folder in cache_folders(cache_dir):
        if (folder / ".complete").is_file():
            result[folder.name] = tree_manifest(folder)["treeSha256"]
    return result


def state_files(cache_dir: Path) -> list[dict[str, Any]]:
    directory = cache_dir / ".service" / "locations"
    if not directory.is_dir():
        return []
    result: list[dict[str, Any]] = []
    for path in sorted(directory.glob("*.json")):
        if path.is_symlink():
            raise UnsafeState(f"refusing symlink state file: {path}")
        try:
            data = read_json(path)
        except (OSError, ValueError):
            data = None
        result.append({"name": path.name, "sha256": sha256_file(path), "data": data})
    return result


def state_file_for(cache_dir: Path, suburb: str) -> dict[str, Any] | None:
    for entry in state_files(cache_dir):
        data = entry.get("data") or {}
        value = data.get("Suburb") or data.get("suburb")
        if value == suburb:
            return entry
    return None


def legacy_folder_name(suburb: str, state: str, when: datetime) -> str:
    prefix = sv._legacy_prefix(sv.normalize_location(suburb, state))
    return f"{prefix}_{when.strftime('%Y%m%d_%H%M%S')}"


def node_paths() -> tuple[str, ...]:
    return NODE_PATHS


def build_fault_server_script(port: int = FAULT_PORT) -> str:
    return f"""
const http = require('http');
const port = {int(port)};
let mode = 'redirect';
const server = http.createServer((req, res) => {{
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname === '/__health') {{
    res.writeHead(200, {{'content-type': 'application/json'}});
    res.end(JSON.stringify({{mode: mode, port: port}}));
    return;
  }}
  if (url.pathname === '/__mode') {{
    const value = url.searchParams.get('value');
    if (!['redirect', 'fault'].includes(value)) {{
      res.writeHead(400, {{'content-type': 'text/plain'}});
      res.end('invalid mode');
      return;
    }}
    mode = value;
    res.writeHead(200, {{'content-type': 'text/plain'}});
    res.end(mode);
    return;
  }}
  if (url.pathname === '/__exit') {{
    res.writeHead(200, {{'content-type': 'text/plain'}});
    res.end('bye');
    server.close(() => process.exit(0));
    return;
  }}
  if (mode === 'fault') {{
    res.writeHead(503, {{'content-type': 'text/plain'}});
    res.end('injected upstream fault');
    return;
  }}
  res.writeHead(302, {{Location: 'https://www.bom.gov.au/'}});
  res.end();
}});
server.listen(port, '127.0.0.1');
server.on('error', (error) => {{ console.error(error); process.exit(1); }});
""".strip()


def validate_fault_mode(value: str) -> str:
    if value not in FAULT_MODES:
        raise ExerciseError(f"invalid fault mode: {value!r}")
    return value


class Checks:
    def __init__(self, stage: str) -> None:
        self.stage = stage
        self.items: list[dict[str, Any]] = []

    def record(
        self,
        name: str,
        ok: bool,
        detail: Any = None,
        classification: str = "real",
        *,
        required: bool = False,
    ) -> None:
        self.items.append({
            "name": name,
            "ok": bool(ok),
            "detail": evidence_safe(detail),
            "classification": classification,
            "required": bool(required),
        })

    def require(self, name: str, ok: bool, detail: Any = None, classification: str = "real") -> None:
        self.record(name, ok, detail, classification, required=True)
        if not ok:
            raise ExerciseError(f"{name}: {detail if detail is not None else 'failed'}")

    def required_failures(self) -> list[str]:
        return [item["name"] for item in self.items if item["required"] and not item["ok"]]


def http_json(base: str, path: str, *, method: str = "GET") -> tuple[int | None, Any, str | None]:
    status, data, error, transport = sv.request_json(
        base.rstrip("/") + path,
        method=method,
        connect_timeout=HTTP_CONNECT_TIMEOUT,
        total_timeout=HTTP_TOTAL_TIMEOUT,
        max_bytes=4 * 1024 * 1024,
    )
    if transport:
        return status, data, error or "transport failure"
    return status, data, error


def http_status(base: str, path: str, *, method: str = "GET") -> int | None:
    try:
        status, _body, _headers = sv.fetch_bounded(
            base.rstrip("/") + path,
            method=method,
            connect_timeout=HTTP_CONNECT_TIMEOUT,
            total_timeout=HTTP_TOTAL_TIMEOUT,
            max_bytes=1024 * 1024,
        )
        return status
    except Exception:
        return None


def wait_http_ready(base: str, path: str, timeout: int) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    last: dict[str, Any] = {"status": None, "error": "not attempted"}
    while time.monotonic() < deadline:
        status, body, error = http_json(base, path)
        last = {"status": status, "body": body, "error": error}
        if status == 200:
            if path.endswith("/api/ready"):
                sv.validate_ready(body)
            return {"ok": True, **last, "observedAt": utc_text(utc_now())}
        time.sleep(1)
    return {"ok": False, **last, "observedAt": utc_text(utc_now())}


def token_from_path(value: str) -> str:
    return urllib.parse.quote(value, safe="")


def status_path(suburb: str, state: str) -> str:
    return f"/api/cache/{token_from_path(suburb)}/{token_from_path(state)}/status"


def radar_path(suburb: str, state: str) -> str:
    return f"/api/radar/{token_from_path(suburb)}/{token_from_path(state)}"


def refresh_path(suburb: str, state: str) -> str:
    return f"/api/cache/{token_from_path(suburb)}/{token_from_path(state)}/refresh"


def delete_path(suburb: str, state: str) -> str:
    return f"/api/cache/{token_from_path(suburb)}/{token_from_path(state)}"


def trigger_refresh(base: str, suburb: str, state: str) -> dict[str, Any]:
    status, body, error = http_json(base, refresh_path(suburb, state), method="POST")
    return {"status": status, "body": body, "error": error}


def delete_location(base: str, suburb: str, state: str) -> dict[str, Any]:
    status, body, error = http_json(base, delete_path(suburb, state), method="DELETE")
    return {"status": status, "body": body, "error": error}


def get_status(base: str, suburb: str, state: str) -> dict[str, Any] | None:
    status, body, _error = http_json(base, status_path(suburb, state))
    return body if status == 200 and isinstance(body, dict) else None


def wait_status(
    base: str,
    suburb: str,
    state: str,
    predicate: Callable[[dict[str, Any]], bool],
    timeout: int,
) -> tuple[dict[str, Any], dict[str, Any]]:
    deadline = time.monotonic() + timeout
    last: dict[str, Any] | None = None
    while time.monotonic() < deadline:
        last = get_status(base, suburb, state)
        if last is not None and predicate(last):
            return last, {"observedAt": utc_text(utc_now()), "status": last}
        time.sleep(1)
    raise ExerciseError(f"status predicate not met within {timeout}s; last={last}")


def failure_predicate(status: dict[str, Any]) -> bool:
    if status_value(status, "updateFailed") is not True:
        return False
    if status_value(status, "isUpdating") is True:
        return False
    if not isinstance(status_value(status, "errorCode"), str) or not status_value(status, "errorCode"):
        return False
    failures = status_value(status, "consecutiveFailures")
    if not isinstance(failures, int) or failures < 1:
        return False
    retry = parse_status_time(status, "nextRetryAt")
    return retry is not None and retry > utc_now() - timedelta(seconds=5)


def timeout_predicate(status: dict[str, Any]) -> bool:
    return failure_predicate(status) and status_value(status, "errorCode") == "TIMEOUT_ERROR"


def success_predicate(status: dict[str, Any]) -> bool:
    if status_value(status, "updateFailed") is not False:
        return False
    if status_value(status, "isUpdating") is True:
        return False
    if status_value(status, "consecutiveFailures") not in (0, None):
        return False
    if status_value(status, "nextRetryAt") is not None:
        return False
    return status_value(status, "lastSuccessfulCaptureAt") is not None


def refresh_response_outcome(response: dict[str, Any]) -> dict[str, Any]:
    """Classify a refresh POST without treating a plain HTTP 200 as acceptance.

    TriggerCacheUpdateAsync returns 200 for a fresh cache with all three flags
    false. Only an explicit accepted/updating response is a real admission.
    """
    status = response.get("status")
    body = response.get("body")
    rejection = status_value(body, "rejectionCode")
    accepted = (
        status == 200
        and isinstance(body, dict)
        and (
            status_value(body, "updateAccepted") is True
            or status_value(body, "updateTriggered") is True
            or status_value(body, "isUpdating") is True
        )
        and (rejection is None or rejection == "AlreadyInProgress")
    )
    return {
        "accepted": accepted,
        "status": status,
        "rejectionCode": rejection,
        "updateAccepted": status_value(body, "updateAccepted"),
        "updateTriggered": status_value(body, "updateTriggered"),
        "isUpdating": status_value(body, "isUpdating"),
        "cacheIsValid": status_value(body, "cacheIsValid"),
        "message": status_value(body, "message"),
    }


def refresh_window_ready(status: dict[str, Any] | None) -> bool:
    """True when a read-only status permits a real capture request now."""
    if not isinstance(status, dict):
        return False
    if status_value(status, "isUpdating") is True:
        return True
    if status_value(status, "cacheIsValid") is not False:
        return False
    retry = parse_status_time(status, "nextRetryAt")
    return retry is None or retry <= utc_now()


def success_newer_than(status: dict[str, Any] | None, baseline: datetime | None) -> bool:
    """Require a completed success strictly after the pre-trigger baseline."""
    if not success_predicate(status):
        return False
    current = parse_status_time(status, "lastSuccessfulCaptureAt")
    return current is not None and (baseline is None or current > baseline)


def attempt_newer_than(status: dict[str, Any] | None, baseline: datetime | None) -> bool:
    current = parse_status_time(status, "lastUpdateAttempt")
    return current is not None and (baseline is None or current > baseline)


def expected_attempt_completed(
    status: dict[str, Any] | None,
    *,
    baseline_success: datetime | None,
    baseline_attempt: datetime | None,
    expected_failure: bool,
) -> bool:
    if expected_failure:
        return failure_predicate(status) and attempt_newer_than(status, baseline_attempt)
    return success_newer_than(status, baseline_success)


def wait_for_refresh_window(
    base: str,
    suburb: str,
    state: str,
    *,
    timeout: int = FRESH_WAIT_SECONDS,
    baseline_success: datetime | None = None,
    baseline_attempt: datetime | None = None,
    expected_failure: bool = False,
    fetcher: Callable[[str, str, str], dict[str, Any] | None] | None = None,
    clock: Callable[[], float] | None = None,
    sleeper: Callable[[float], None] | None = None,
) -> tuple[dict[str, Any], dict[str, Any]]:
    """Poll read-only status until the cache is stale or an expected attempt is done."""
    fetch = fetcher or get_status
    monotonic = clock or time.monotonic
    pause = sleeper or time.sleep
    started_at = utc_now()
    deadline = monotonic() + timeout
    last: dict[str, Any] | None = None
    polls = 0
    while True:
        last = fetch(base, suburb, state)
        polls += 1
        observed_at = utc_now()
        if isinstance(last, dict):
            if expected_attempt_completed(
                last,
                baseline_success=baseline_success,
                baseline_attempt=baseline_attempt,
                expected_failure=expected_failure,
            ):
                reason = "completed-failure" if expected_failure else "completed-success"
                return last, {
                    "reason": reason,
                    "polls": polls,
                    "startedAt": utc_text(started_at),
                    "observedAt": utc_text(observed_at),
                    "waitedSeconds": (observed_at - started_at).total_seconds(),
                    "baselineSuccess": utc_text(baseline_success) if baseline_success else None,
                    "baselineAttempt": utc_text(baseline_attempt) if baseline_attempt else None,
                }
            if refresh_window_ready(last):
                reason = "updating" if status_value(last, "isUpdating") is True else "stale"
                return last, {
                    "reason": reason,
                    "polls": polls,
                    "startedAt": utc_text(started_at),
                    "observedAt": utc_text(observed_at),
                    "waitedSeconds": (observed_at - started_at).total_seconds(),
                    "baselineSuccess": utc_text(baseline_success) if baseline_success else None,
                    "baselineAttempt": utc_text(baseline_attempt) if baseline_attempt else None,
                }
        if monotonic() >= deadline:
            raise ExerciseError(
                f"refresh window not ready within {timeout}s after {polls} status reads; last={last}"
            )
        pause(STATUS_POLL_SECONDS)


def resolve_refresh_acceptance(
    base: str,
    suburb: str,
    state: str,
    response: dict[str, Any],
    *,
    baseline_success: datetime | None,
    baseline_attempt: datetime | None,
    expected_failure: bool,
    allow_completed: bool,
) -> dict[str, Any]:
    """Accept only explicit admission or an already-completed expected attempt."""
    outcome = refresh_response_outcome(response)
    if outcome["accepted"]:
        return {"accepted": True, "reason": "response", "outcome": outcome, "status": None}
    current = get_status(base, suburb, state)
    if allow_completed and expected_attempt_completed(
        current,
        baseline_success=baseline_success,
        baseline_attempt=baseline_attempt,
        expected_failure=expected_failure,
    ):
        return {
            "accepted": True,
            "reason": "completed-attempt",
            "outcome": outcome,
            "status": current,
        }
    return {"accepted": False, "reason": "not-accepted", "outcome": outcome, "status": current}


def trigger_recapture(
    base: str,
    suburb: str,
    state: str,
    *,
    label: str,
    checks: Checks,
    baseline_success: datetime | None,
    baseline_attempt: datetime | None,
    expected_failure: bool = False,
    allow_completed: bool = True,
    wait_timeout: int = FRESH_WAIT_SECONDS,
    max_completed_cycles: int = MAX_COMPLETED_WAIT_CYCLES,
    classification: str = "real",
    acceptance_check: str | None = None,
) -> dict[str, Any]:
    """Wait for a refresh window, then require a real admission/new attempt."""
    acceptance_name = acceptance_check or f"{label}.refreshAccepted"
    success_baseline = baseline_success
    attempt_baseline = baseline_attempt
    completed_cycles = 0
    while True:
        window_status, wait_detail = wait_for_refresh_window(
            base,
            suburb,
            state,
            timeout=wait_timeout,
            baseline_success=success_baseline,
            baseline_attempt=attempt_baseline,
            expected_failure=expected_failure,
        )
        wait_name = f"{label}.staleWait" if completed_cycles == 0 else f"{label}.staleWait{completed_cycles + 1}"
        checks.record(wait_name, True, wait_detail, classification)
        if wait_detail["reason"].startswith("completed"):
            if allow_completed:
                acceptance = {
                    "accepted": True,
                    "reason": "completed-attempt",
                    "outcome": None,
                    "status": window_status,
                    "response": None,
                    "wait": wait_detail,
                    "baselineSuccess": success_baseline,
                    "baselineAttempt": attempt_baseline,
                }
                checks.record(f"{label}.completedAttempt", True, acceptance, classification)
                checks.require(acceptance_name, True, acceptance, classification)
                return acceptance
            completed_cycles += 1
            if completed_cycles > max_completed_cycles:
                checks.require(
                    acceptance_name,
                    False,
                    {
                        "reason": "completed-background-success-without-capture-window",
                        "status": window_status,
                        "wait": wait_detail,
                    },
                )
            checks.record(f"{label}.completedAttempt{completed_cycles}", True, {
                "status": window_status,
                "wait": wait_detail,
            }, classification)
            success_baseline = parse_status_time(window_status, "lastSuccessfulCaptureAt")
            attempt_baseline = parse_status_time(window_status, "lastUpdateAttempt")
            continue

        response = trigger_refresh(base, suburb, state)
        acceptance = resolve_refresh_acceptance(
            base,
            suburb,
            state,
            response,
            baseline_success=success_baseline,
            baseline_attempt=attempt_baseline,
            expected_failure=expected_failure,
            allow_completed=allow_completed,
        )
        acceptance["wait"] = wait_detail
        acceptance["baselineSuccess"] = success_baseline
        acceptance["baselineAttempt"] = attempt_baseline
        acceptance["response"] = response
        if acceptance["accepted"]:
            checks.require(acceptance_name, True, acceptance, classification)
            return acceptance
        if not allow_completed and success_newer_than(
            acceptance.get("status"), success_baseline
        ):
            completed_cycles += 1
            if completed_cycles > max_completed_cycles:
                checks.require(acceptance_name, False, acceptance, classification)
            checks.record(f"{label}.completedAttempt{completed_cycles}", True, acceptance, classification)
            success_baseline = parse_status_time(acceptance.get("status"), "lastSuccessfulCaptureAt")
            attempt_baseline = parse_status_time(acceptance.get("status"), "lastUpdateAttempt")
            continue
        checks.require(acceptance_name, False, acceptance, classification)
        raise AssertionError("unreachable")


def validate_radar(
    base: str,
    body: Any,
    *,
    suburb: str,
    state: str,
    require_fresh: bool,
    expected_frames: int = 7,
) -> dict[str, Any]:
    max_age = FRESH_SECONDS if require_fresh else 10 * 365 * 24 * 3600
    validated = sv.validate_latest(
        body,
        expected_frames=expected_frames,
        suburb=suburb,
        state=state,
        max_freshness_seconds=max_age,
        future_skew_seconds=FUTURE_SKEW_SECONDS,
    )
    images: list[dict[str, Any]] = []
    for frame in validated["frames"]:
        url = sv.frame_url(base, frame.get("imageUrl"))
        status, data, _headers = sv.fetch_bounded(
            url,
            connect_timeout=HTTP_CONNECT_TIMEOUT,
            total_timeout=HTTP_TOTAL_TIMEOUT,
            max_bytes=sv.DEFAULT_MAX_IMAGE_BYTES,
        )
        if status != 200:
            raise ExerciseError(f"frame image returned HTTP {status}: {url}")
        decoded = sv.validate_png(data, url=url)
        images.append({"frameIndex": frame.get("frameIndex"), **decoded})
    age = (utc_now() - validated["observation"]).total_seconds()
    if require_fresh and age > FRESH_SECONDS:
        raise ExerciseError(f"validated observation is {age:.0f}s old")
    return {
        "observation": utc_text(validated["observation"]),
        "cacheFolder": validated["cacheFolder"],
        "frames": len(validated["frames"]),
        "ageSeconds": age,
        "images": images,
    }


def validate_radar_compat(
    base: str,
    body: Any,
    *,
    suburb: str,
    state: str,
    expected_folder: str | None = None,
) -> dict[str, Any]:
    """Read an old retained image's response without requiring the new schema.

    The v0.1-era frame URLs do not carry a cacheFolder query, so callers that
    seeded a known folder pass it explicitly. The newer pinned query is used
    when present.
    """
    if not isinstance(body, dict):
        raise ExerciseError("radar response is not an object")
    frames = body.get("frames")
    if not isinstance(frames, list) or not frames:
        raise ExerciseError("radar response has no frames")
    observation = body.get("observationTime") or body.get("ObservationTime")
    parsed = sv.parse_utc(observation, "observationTime")
    folder = expected_folder
    for frame in frames:
        if not isinstance(frame, dict):
            raise ExerciseError("radar frame is not an object")
        image_url = frame.get("imageUrl") or frame.get("ImageUrl")
        url = sv.frame_url(base, image_url)
        status, data, _headers = sv.fetch_bounded(
            url,
            connect_timeout=HTTP_CONNECT_TIMEOUT,
            total_timeout=HTTP_TOTAL_TIMEOUT,
            max_bytes=sv.DEFAULT_MAX_IMAGE_BYTES,
        )
        if status != 200:
            raise ExerciseError(f"old-image frame returned HTTP {status}: {url}")
        sv.validate_png(data, url=url)
        try:
            pinned_folder = sv.cache_folder_from_image_url(image_url)
        except sv.VerificationError:
            pinned_folder = None
        if pinned_folder is not None:
            folder = pinned_folder
    if folder is None:
        raise ExerciseError("old-image response has no cache folder")
    sv.assert_cache_folder_owned(folder, suburb, state)
    return {
        "observation": utc_text(parsed),
        "cacheFolder": folder,
        "frames": len(frames),
    }


def fetch_radar(base: str, suburb: str, state: str) -> dict[str, Any]:
    status, body, error = http_json(base, radar_path(suburb, state))
    return {"status": status, "body": body, "error": error}


def manifest_for_stage(stage: str) -> dict[str, Any]:
    run_id = (os.environ.get("BOM_TEST_RUN_ID") or "").strip()
    token = (os.environ.get("BOM_TEST_OWNER_TOKEN") or "").strip()
    cache_env = (os.environ.get("BOM_TEST_CACHE_DIR") or "").strip()
    base_url = (os.environ.get("BOM_TEST_BASE_URL") or os.environ.get("BOM_TEST_URL") or os.environ.get("BOM_BASE_URL") or "").strip().rstrip("/")
    if not RUN_ID_RE.fullmatch(run_id):
        raise UnsafeState("BOM_TEST_RUN_ID is missing or invalid")
    if not token or not cache_env or not base_url:
        raise UnsafeState("runner run/token/cache/base environment is incomplete")
    run_dir = ROOT / "artifacts" / "execution" / "runner" / "runs" / run_id
    if not run_dir.is_dir() or run_dir.is_symlink():
        raise UnsafeState(f"run evidence directory is missing or unsafe: {run_dir}")
    manifest = runner.load_manifest(run_dir, require_binding=True)
    errors = validate_manifest_scope(
        manifest, run_id=run_id, token=token, cache_env=cache_env, base_url=base_url
    )
    if errors:
        raise UnsafeState("; ".join(errors))
    expected_image = expected_image_for_stage(stage, manifest)
    actual_image = str((manifest.get("container") or {}).get("imageId") or "")
    if expected_image is not None and actual_image != expected_image:
        raise UnsafeState(f"manifest image {actual_image} is not the stage image {expected_image}")
    return {
        "runId": run_id,
        "token": token,
        "cacheDir": Path(cache_env).resolve(),
        "baseUrl": base_url,
        "runDir": run_dir,
        "manifest": manifest,
    }


def expected_image_for_stage(stage: str, manifest: dict[str, Any]) -> str | None:
    if stage in ("upgrade-prepare", "rollback"):
        return os.environ.get("BOM_EXERCISE_OLD_IMAGE", OLD_BASELINE_IMAGE).strip()
    return None


def docker_env(manifest: dict[str, Any]) -> dict[str, str]:
    return runner.docker_env_from_manifest(manifest)


def verify_owned_container(
    manifest: dict[str, Any],
    *,
    run_id: str,
    token: str,
    require_running: bool,
) -> tuple[str, dict[str, Any], dict[str, str]]:
    endpoint = runner.docker_endpoint_matches(manifest)
    if not endpoint.get("ok"):
        raise UnsafeState(f"pinned Docker endpoint cannot be verified: {endpoint.get('error')}")
    env = docker_env(manifest)
    try:
        container_id, inspected = cc.find_owned_container(run_id, token, env)
    except Exception as exc:  # noqa: BLE001 - convert to bounded refusal
        raise UnsafeState(f"owned container cannot be verified: {exc}") from exc
    errors = validate_container_identity(
        container_id,
        inspected,
        manifest,
        run_id=run_id,
        token=token,
        require_running=require_running,
    )
    if errors:
        raise UnsafeState("; ".join(errors))
    return container_id, inspected, env


def docker_mutate(
    manifest: dict[str, Any],
    *,
    run_id: str,
    token: str,
    argv: list[str],
    require_running: bool = True,
    timeout: float = 60,
) -> dict[str, Any]:
    container_id, _inspected, env = verify_owned_container(
        manifest, run_id=run_id, token=token, require_running=require_running
    )
    command = argv + [container_id]
    completed = cc.run_bounded(command, timeout=timeout, env=env, max_output=4 * 1024 * 1024)
    if completed.returncode != 0:
        raise ExerciseError(
            f"Docker mutation failed ({completed.returncode}): "
            + completed.stderr.decode("utf-8", "replace")[:500]
        )
    return {"command": command[:2], "containerId": container_id, "returncode": completed.returncode}


def wait_container_state(
    container_id: str,
    env: dict[str, str],
    *,
    running: bool,
    timeout: int = 120,
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    last: dict[str, Any] = {}
    while time.monotonic() < deadline:
        try:
            inspected = cc.docker_json(container_id, env)
        except Exception as exc:  # noqa: BLE001
            last = {"error": str(exc)}
            time.sleep(1)
            continue
        state = inspected.get("State") or {}
        last = {"running": state.get("Running"), "status": state.get("Status")}
        if bool(state.get("Running")) is running:
            return {"ok": True, **last}
        time.sleep(1)
    return {"ok": False, **last}


def restart_exact(manifest: dict[str, Any], *, run_id: str, token: str) -> dict[str, Any]:
    grace = int(manifest.get("shutdownGraceSeconds") or 20)
    result = docker_mutate(
        manifest,
        run_id=run_id,
        token=token,
        argv=["docker", "restart", "-t", str(grace)],
        timeout=grace + 60,
    )
    env = docker_env(manifest)
    state = wait_container_state(result["containerId"], env, running=True, timeout=120)
    if not state.get("ok"):
        raise ExerciseError(f"container did not return to running after restart: {state}")
    return {**result, "state": state}


def kill_exact(manifest: dict[str, Any], *, run_id: str, token: str) -> dict[str, Any]:
    result = docker_mutate(
        manifest,
        run_id=run_id,
        token=token,
        argv=["docker", "kill", "--signal", "KILL"],
        timeout=60,
    )
    env = docker_env(manifest)
    state = wait_container_state(result["containerId"], env, running=False, timeout=120)
    if not state.get("ok"):
        raise ExerciseError(f"container did not stop after SIGKILL: {state}")
    return {**result, "state": state}


def start_exact(manifest: dict[str, Any], *, run_id: str, token: str) -> dict[str, Any]:
    result = docker_mutate(
        manifest,
        run_id=run_id,
        token=token,
        argv=["docker", "start"],
        require_running=False,
        timeout=60,
    )
    env = docker_env(manifest)
    state = wait_container_state(result["containerId"], env, running=True, timeout=120)
    if not state.get("ok"):
        raise ExerciseError(f"container did not start: {state}")
    return {**result, "state": state}


def stop_exact(manifest: dict[str, Any], *, run_id: str, token: str) -> dict[str, Any]:
    grace = int(manifest.get("shutdownGraceSeconds") or 20)
    result = docker_mutate(
        manifest,
        run_id=run_id,
        token=token,
        argv=["docker", "stop", "-t", str(grace)],
        timeout=grace + 60,
    )
    env = docker_env(manifest)
    state = wait_container_state(result["containerId"], env, running=False, timeout=120)
    if not state.get("ok"):
        raise ExerciseError(f"container did not stop: {state}")
    return {**result, "state": state}


def docker_exec(
    manifest: dict[str, Any],
    *,
    run_id: str,
    token: str,
    argv: list[str],
    timeout: float = 30,
    detached: bool = False,
    max_output: int = 1024 * 1024,
) -> Any:
    container_id, _inspected, env = verify_owned_container(
        manifest, run_id=run_id, token=token, require_running=True
    )
    command = ["docker", "exec"]
    if detached:
        command.append("-d")
    command.extend([container_id, *argv])
    completed = cc.run_bounded(command, timeout=timeout, env=env, max_output=max_output)
    if completed.returncode != 0:
        raise ExerciseError(
            f"docker exec failed ({completed.returncode}): "
            + completed.stderr.decode("utf-8", "replace")[:500]
        )
    return completed


def resolve_node(manifest: dict[str, Any], *, run_id: str, token: str) -> str:
    errors: list[str] = []
    for path in node_paths():
        try:
            completed = docker_exec(
                manifest, run_id=run_id, token=token, argv=[path, "--version"], timeout=20
            )
        except ExerciseError as exc:
            errors.append(f"{path}: {exc}")
            continue
        if completed.stdout.strip():
            return path
    raise ExerciseError("no executable Node in the candidate image: " + "; ".join(errors))


def install_fault_server(manifest: dict[str, Any], *, run_id: str, token: str) -> dict[str, Any]:
    node = resolve_node(manifest, run_id=run_id, token=token)
    script = build_fault_server_script(FAULT_PORT)
    docker_exec(
        manifest,
        run_id=run_id,
        token=token,
        argv=[node, "-e", script],
        detached=True,
        timeout=20,
    )
    deadline = time.monotonic() + 30
    last = "not attempted"
    while time.monotonic() < deadline:
        try:
            completed = docker_exec(
                manifest,
                run_id=run_id,
                token=token,
                argv=["curl", "-fsS", f"http://127.0.0.1:{FAULT_PORT}/__health"],
                timeout=10,
            )
            data = json.loads(completed.stdout.decode("utf-8"))
            if data.get("mode") in FAULT_MODES:
                return {"node": node, "port": FAULT_PORT, "health": data}
        except (ExerciseError, ValueError) as exc:
            last = f"{type(exc).__name__}: {exc}"
        time.sleep(1)
    raise ExerciseError(f"fault server did not become healthy: {last}")


def fault_mode(manifest: dict[str, Any], *, run_id: str, token: str, value: str) -> str:
    value = validate_fault_mode(value)
    docker_exec(
        manifest,
        run_id=run_id,
        token=token,
        argv=[
            "curl", "-fsS",
            f"http://127.0.0.1:{FAULT_PORT}/__mode?value={value}",
        ],
        timeout=10,
    )
    return value


def fault_exit(manifest: dict[str, Any], *, run_id: str, token: str) -> None:
    try:
        docker_exec(
            manifest,
            run_id=run_id,
            token=token,
            argv=["curl", "-fsS", f"http://127.0.0.1:{FAULT_PORT}/__exit"],
            timeout=10,
        )
    except ExerciseError:
        pass


def container_logs(container_id: str, env: dict[str, str], *, since: datetime | None = None) -> str:
    command = ["docker", "logs", "--tail", "2000", "--timestamps"]
    if since is not None:
        command.extend(["--since", utc_text(since)])
    command.append(container_id)
    completed = cc.run_bounded(
        command,
        timeout=20,
        env=env,
        max_output=MAX_LOG_BYTES,
    )
    if completed.returncode != 0:
        raise ExerciseError("container logs could not be read")
    return (completed.stdout + completed.stderr).decode("utf-8", "replace")


DATA_ROOT_BASE = ROOT / "artifacts" / "execution" / "fault-rollback" / "data"
OWNER_MARKER_NAME = ".fault-rollback-owner.json"
OWNER_MARKER_SCHEMA = "fault-rollback-data/v1"


def _assert_no_symlink_components(path: Path, root: Path) -> None:
    try:
        relative = path.relative_to(root)
    except ValueError as exc:
        raise UnsafeState(f"path is outside the allowed data root: {path}") from exc
    current = root
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            raise UnsafeState(f"data path has a symlink component: {current}")


def data_root() -> Path:
    """Return the coordinator-owned experiment directory.

    The directory must already exist strictly below
    artifacts/execution/fault-rollback/data, must not contain a symlink
    component, must not be a filesystem root, and must carry the coordinator's
    owner marker. The helper never creates or mkdirs the caller-supplied path.
    """
    raw = (os.environ.get("BOM_EXERCISE_DATA_DIR") or "").strip()
    if not raw:
        raise UnsafeState("BOM_EXERCISE_DATA_DIR is required for this stage")
    candidate = Path(raw)
    if not candidate.is_absolute():
        raise UnsafeState("BOM_EXERCISE_DATA_DIR must be absolute")
    if candidate == Path(candidate.anchor):
        raise UnsafeState("BOM_EXERCISE_DATA_DIR must not be a filesystem root")
    base = Path(os.path.abspath(DATA_ROOT_BASE))
    if not base.is_dir():
        raise UnsafeState(f"coordinator data root is missing: {base}")
    _assert_no_symlink_components(base, ROOT)
    lexical = Path(os.path.abspath(candidate))
    if lexical == base or base not in lexical.parents:
        raise UnsafeState(f"BOM_EXERCISE_DATA_DIR is not strictly below {base}")
    _assert_no_symlink_components(lexical, base)
    try:
        resolved = lexical.resolve(strict=True)
    except OSError as exc:
        raise UnsafeState(f"BOM_EXERCISE_DATA_DIR must be an existing directory: {lexical}") from exc
    if resolved != lexical:
        raise UnsafeState("BOM_EXERCISE_DATA_DIR changed through a symlink or alternate path")
    if not resolved.is_dir():
        raise UnsafeState(f"BOM_EXERCISE_DATA_DIR is not a directory: {resolved}")
    marker = resolved / OWNER_MARKER_NAME
    if marker.is_symlink() or not marker.is_file():
        raise UnsafeState(f"coordinator owner marker is missing or unsafe: {marker}")
    try:
        marker_bytes = marker.read_bytes()
        if len(marker_bytes) > 4096:
            raise UnsafeState("coordinator owner marker is too large")
        marker_value = json.loads(marker_bytes.decode("utf-8"))
    except (OSError, UnicodeError, ValueError) as exc:
        raise UnsafeState(f"coordinator owner marker is unreadable: {exc}") from exc
    if not isinstance(marker_value, dict) or marker_value.get("schema") != OWNER_MARKER_SCHEMA:
        raise UnsafeState("coordinator owner marker schema does not match")
    return resolved


def old_image_id(env: dict[str, str]) -> str:
    value = (os.environ.get("BOM_EXERCISE_OLD_IMAGE") or OLD_BASELINE_IMAGE).strip()
    if not IMAGE_ID_RE.fullmatch(value):
        raise ExerciseError("BOM_EXERCISE_OLD_IMAGE is not an exact image ID")
    completed = cc.run_bounded(
        ["docker", "image", "inspect", "--format", "{{.Id}}|{{.Os}}/{{.Architecture}}", value],
        timeout=20,
        env=env,
    )
    if completed.returncode != 0:
        raise ExerciseError(
            "retained old image is not available locally; coordinator pull is required first"
        )
    inspected_id, separator, platform = completed.stdout.decode("utf-8").strip().partition("|")
    if not separator or inspected_id != value:
        raise ExerciseError("retained old image ID could not be verified exactly")
    if platform != "linux/amd64":
        raise ExerciseError(f"retained old image platform is {platform}, expected linux/amd64")
    return value


def seed_legacy_snapshot(cache_dir: Path, *, suburb: str, state: str, when: datetime) -> dict[str, Any]:
    folder = legacy_folder_name(suburb, state, when)
    seeded = cc.seed_snapshot(cache_dir, folder, suburb, state, when)
    return {"classification": "synthetic", **seeded}


def stage_fault(ctx: dict[str, Any], checks: Checks) -> dict[str, Any]:
    manifest = ctx["manifest"]
    run_id = ctx["runId"]
    token = ctx["token"]
    cache_dir = ctx["cacheDir"]
    base = ctx["baseUrl"]
    suburb, state = "Pomona", "QLD"

    container_id, _inspected, env = verify_owned_container(
        manifest, run_id=run_id, token=token, require_running=True
    )
    checks.record("ownership.manifest", True, {"containerId": container_id})
    install = install_fault_server(manifest, run_id=run_id, token=token)
    checks.record("faultServer.installed", True, install, "controlled")
    checks.require("faultServer.mode", fault_mode(manifest, run_id=run_id, token=token, value="redirect") == "redirect", None, "controlled")

    ready = wait_http_ready(base, "/api/ready", READY_WAIT_SECONDS)
    checks.require("readiness.initial", ready.get("ok") is True, ready)
    checks.record("readiness.body", True, ready.get("body"))

    baseline_status_before = get_status(base, suburb, state)
    baseline_success_before = parse_status_time(baseline_status_before, "lastSuccessfulCaptureAt")
    baseline_attempt_before = parse_status_time(baseline_status_before, "lastUpdateAttempt")
    baseline_folders_before = complete_folder_hashes(cache_dir)
    checks.record("baseline.statusBefore", True, baseline_status_before)
    baseline_acceptance = trigger_recapture(
        base,
        suburb,
        state,
        label="baseline",
        checks=checks,
        baseline_success=baseline_success_before,
        baseline_attempt=baseline_attempt_before,
        expected_failure=False,
        allow_completed=True,
    )
    baseline_status, baseline_detail = wait_status(
        base,
        suburb,
        state,
        lambda status: success_newer_than(status, baseline_acceptance["baselineSuccess"]),
        CAPTURE_WAIT_SECONDS,
    )
    baseline_last = parse_status_time(baseline_status, "lastSuccessfulCaptureAt")
    checks.require(
        "baseline.success",
        baseline_last is not None
        and (baseline_success_before is None or baseline_last > baseline_success_before),
        baseline_detail,
    )
    radar = fetch_radar(base, suburb, state)
    baseline_radar = validate_radar(base, radar.get("body"), suburb=suburb, state=state, require_fresh=True)
    checks.record("baseline.radar", True, baseline_radar, "real-bom")
    baseline_folders = complete_folder_hashes(cache_dir)
    checks.require("baseline.folders", len(baseline_folders) >= 1, baseline_folders)
    checks.require(
        "baseline.newFolder",
        baseline_radar["cacheFolder"] not in baseline_folders_before,
        {"before": sorted(baseline_folders_before), "captured": baseline_radar["cacheFolder"]},
    )

    sydney = seed_legacy_snapshot(cache_dir, suburb="Sydney", state="NSW", when=utc_now() - timedelta(minutes=1))
    checks.record("sydney.seed", True, sydney, "synthetic")

    failure_status_before = get_status(base, suburb, state)
    failure_success_before = parse_status_time(failure_status_before, "lastSuccessfulCaptureAt")
    failure_attempt_before = parse_status_time(failure_status_before, "lastUpdateAttempt")
    failure_folders_before = complete_folder_hashes(cache_dir)
    checks.record("failure.statusBefore", True, failure_status_before)

    checks.require(
        "fault.mode.fault",
        fault_mode(manifest, run_id=run_id, token=token, value="fault") == "fault",
        None,
        "controlled",
    )
    failure_acceptance = trigger_recapture(
        base,
        suburb,
        state,
        label="failure",
        checks=checks,
        baseline_success=failure_success_before,
        baseline_attempt=failure_attempt_before,
        expected_failure=True,
        allow_completed=True,
    )
    failed_status, failed_detail = wait_status(
        base,
        suburb,
        state,
        lambda status: failure_predicate(status)
        and attempt_newer_than(status, failure_acceptance["baselineAttempt"]),
        FAULT_STATUS_WAIT_SECONDS,
    )
    checks.record("failure.status", True, failed_detail)
    checks.require(
        "failure.newAttempt",
        attempt_newer_than(failed_status, failure_attempt_before),
        {"before": utc_text(failure_attempt_before) if failure_attempt_before else None,
         "after": status_value(failed_status, "lastUpdateAttempt")},
    )
    failed_retry = status_value(failed_status, "nextRetryAt")
    fallback = fetch_radar(base, suburb, state)
    fallback_radar = validate_radar(base, fallback.get("body"), suburb=suburb, state=state, require_fresh=False)
    checks.require("fallback.served", True, fallback_radar)
    checks.require("staging.emptyAfterFailure", staging_entries(cache_dir) == [], staging_entries(cache_dir))
    checks.require("folders.noIncompleteAfterFailure", incomplete_folders(cache_dir) == [], incomplete_folders(cache_dir))
    checks.require(
        "folders.noPartialAfterFailure",
        set(complete_folder_hashes(cache_dir)) == set(failure_folders_before),
        {"before": sorted(failure_folders_before), "after": sorted(complete_folder_hashes(cache_dir))},
    )

    immediate = trigger_refresh(base, suburb, state)
    checks.require("backoff.immediateRejected", immediate.get("status") == 429, immediate)
    checks.require(
        "backoff.code",
        (immediate.get("body") or {}).get("rejectionCode") in ("RejectedBackoff",)
        or error_value(immediate.get("body"), "rejectionCode") == "RejectedBackoff",
        immediate,
    )
    current = get_status(base, suburb, state)
    checks.require(
        "backoff.nextRetryUnchanged",
        status_value(current, "nextRetryAt") == failed_retry,
        {"before": failed_retry, "after": status_value(current, "nextRetryAt")},
    )
    checks.require(
        "backoff.lastAttemptUnchanged",
        status_value(current, "lastUpdateAttempt") == status_value(failed_status, "lastUpdateAttempt"),
        {"before": status_value(failed_status, "lastUpdateAttempt"), "after": status_value(current, "lastUpdateAttempt")},
    )

    pomona_state_before = state_file_for(cache_dir, suburb)
    checks.require("state.persistedBeforeRestart", pomona_state_before is not None, pomona_state_before)

    restart_started = utc_now()
    restart = restart_exact(manifest, run_id=run_id, token=token)
    checks.record("restart.exact", True, restart)
    ready_after = wait_http_ready(base, "/api/ready", READY_WAIT_SECONDS)
    checks.require("restart.readiness", ready_after.get("ok") is True, ready_after)
    checks.require(
        "restart.readinessAfterStart",
        sv.parse_utc(ready_after.get("observedAt")) > restart_started,
        {"restartStarted": utc_text(restart_started), "readyAt": ready_after.get("observedAt")},
    )
    fault_removed = utc_now()
    install_after_restart = install_fault_server(manifest, run_id=run_id, token=token)
    checks.record("restart.faultServer", True, install_after_restart, "controlled")
    checks.require("restart.mode", fault_mode(manifest, run_id=run_id, token=token, value="redirect") == "redirect", None, "controlled")
    after_restart = get_status(base, suburb, state)
    restored_retry = parse_status_time(after_restart, "nextRetryAt")
    expected_retry = parse_status_time(failed_status, "nextRetryAt")
    checks.require(
        "restart.backoffRestored",
        status_value(after_restart, "updateFailed") is True
        and restored_retry is not None
        and expected_retry is not None
        and abs((restored_retry - expected_retry).total_seconds()) <= 2,
        {"status": after_restart, "expectedRetry": failed_retry},
    )
    after_restart_trigger = trigger_refresh(base, suburb, state)
    checks.require("restart.backoffStillRejected", after_restart_trigger.get("status") == 429, after_restart_trigger)
    logs = container_logs(container_id, env, since=restart_started)
    restore_index = logs.find("restoring persisted state")
    accept_index = logs.find("accepting work")
    checks.require(
        "restart.restoreBeforeAdmission",
        restore_index >= 0 and accept_index >= 0 and restore_index < accept_index,
        {"restoreFound": restore_index >= 0, "acceptFound": accept_index >= 0},
    )
    checks.record("restart.stateFileBefore", True, pomona_state_before)

    checks.require("recovery.modeRedirect", fault_mode(manifest, run_id=run_id, token=token, value="redirect") == "redirect", None, "controlled")
    recovery_folders_before = complete_folder_hashes(cache_dir)
    recovery_status, recovery_detail = wait_status(
        base,
        suburb,
        state,
        lambda status: success_newer_than(status, baseline_last),
        RECOVERY_SECONDS,
    )
    recovery_radar = validate_radar(
        base, fetch_radar(base, suburb, state).get("body"), suburb=suburb, state=state, require_fresh=True
    )
    checks.require(
        "recovery.newFolder",
        recovery_radar["cacheFolder"] not in recovery_folders_before,
        {"before": sorted(recovery_folders_before), "captured": recovery_radar["cacheFolder"]},
    )
    window = recovery_window(fault_removed, utc_now())
    checks.require("recovery.window", window["ok"] is True, window, "real-bom")
    checks.record("recovery.status", True, recovery_detail, "real-bom")
    checks.record("recovery.radar", True, recovery_radar, "real-bom")

    checks.require("staging.modeRedirect", fault_mode(manifest, run_id=run_id, token=token, value="redirect") == "redirect", None, "controlled")
    staging_status_before = get_status(base, suburb, state)
    staging_success_before = parse_status_time(staging_status_before, "lastSuccessfulCaptureAt")
    staging_attempt_before = parse_status_time(staging_status_before, "lastUpdateAttempt")
    trigger_recapture(
        base,
        suburb,
        state,
        label="staging",
        checks=checks,
        baseline_success=staging_success_before,
        baseline_attempt=staging_attempt_before,
        expected_failure=False,
        allow_completed=False,
    )
    deadline = time.monotonic() + STAGING_WAIT_SECONDS
    staging: list[str] = []
    while time.monotonic() < deadline:
        staging = staging_entries(cache_dir)
        if staging:
            break
        time.sleep(0.5)
    checks.require("staging.observed", bool(staging), staging)
    delete_during = delete_location(base, suburb, state)
    checks.require("delete.activeConflict", delete_during.get("status") == 409, delete_during)
    checks.require(
        "delete.activeConflictCode",
        error_value(delete_during.get("body"), "errorCode") == "CACHE_DELETION_CONFLICT",
        delete_during,
    )
    pre_kill_folders = complete_folder_hashes(cache_dir)

    kill = kill_exact(manifest, run_id=run_id, token=token)
    checks.record("staging.killed", True, kill)
    kill_recovery_start = utc_now()
    start = start_exact(manifest, run_id=run_id, token=token)
    checks.record("staging.restarted", True, start)
    ready_after_kill = wait_http_ready(base, "/api/ready", READY_WAIT_SECONDS)
    checks.require("staging.readiness", ready_after_kill.get("ok") is True, ready_after_kill)
    checks.require("staging.emptyAfterRestart", staging_entries(cache_dir) == [], staging_entries(cache_dir))
    checks.require("staging.noIncompleteAfterRestart", incomplete_folders(cache_dir) == [], incomplete_folders(cache_dir))
    post_kill_folders = complete_folder_hashes(cache_dir)
    checks.require(
        "staging.preKillFoldersIntact",
        all(post_kill_folders.get(name) == digest for name, digest in pre_kill_folders.items()),
        {"before": pre_kill_folders, "after": post_kill_folders},
    )
    kill_status_before = get_status(base, suburb, state)
    kill_success_before = parse_status_time(kill_status_before, "lastSuccessfulCaptureAt")
    kill_attempt_before = parse_status_time(kill_status_before, "lastUpdateAttempt")
    install_after_kill = install_fault_server(manifest, run_id=run_id, token=token)
    checks.record("staging.faultServer", True, install_after_kill, "controlled")
    checks.require("staging.mode", fault_mode(manifest, run_id=run_id, token=token, value="redirect") == "redirect", None, "controlled")
    checks.require("staging.validSnapshotServed", True, validate_radar(
        base, fetch_radar(base, suburb, state).get("body"), suburb=suburb, state=state, require_fresh=False
    ))

    kill_acceptance = trigger_recapture(
        base,
        suburb,
        state,
        label="stagingRecovery",
        checks=checks,
        baseline_success=kill_success_before,
        baseline_attempt=kill_attempt_before,
        expected_failure=False,
        allow_completed=True,
        acceptance_check="stagingRecovery.accepted",
    )
    kill_status, kill_detail = wait_status(
        base,
        suburb,
        state,
        lambda status: success_newer_than(status, kill_acceptance["baselineSuccess"]),
        RECOVERY_SECONDS,
    )
    kill_radar = validate_radar(
        base, fetch_radar(base, suburb, state).get("body"), suburb=suburb, state=state, require_fresh=True
    )
    checks.require(
        "stagingRecovery.newFolder",
        kill_radar["cacheFolder"] not in post_kill_folders,
        {"before": sorted(post_kill_folders), "captured": kill_radar["cacheFolder"]},
    )
    kill_window = recovery_window(kill_recovery_start, utc_now())
    checks.require("stagingRecovery.window", kill_window["ok"] is True, kill_window)
    checks.record("stagingRecovery.status", True, kill_detail)
    checks.record("stagingRecovery.radar", True, kill_radar)

    service_dir = cache_dir / ".service"
    state_backup = cache_dir / ".exercise-state-backup"
    checks.require("state.serviceDirectory", service_dir.is_dir(), str(service_dir))
    checks.require("state.backupPathFree", not state_backup.exists(), str(state_backup))
    state_fault_started = utc_now()
    os.replace(service_dir, state_backup)
    (cache_dir / ".service").write_text("exercise-unavailable\n", encoding="utf-8")
    checks.record("state.faultInjected", True, {"file": str(cache_dir / ".service"), "backup": str(state_backup)}, "controlled")
    try:
        state_status_before = get_status(base, suburb, state)
        state_success_before = parse_status_time(state_status_before, "lastSuccessfulCaptureAt")
        state_attempt_before = parse_status_time(state_status_before, "lastUpdateAttempt")
        state_folders_before = complete_folder_hashes(cache_dir)
        state_acceptance = trigger_recapture(
            base,
            suburb,
            state,
            label="state",
            checks=checks,
            baseline_success=state_success_before,
            baseline_attempt=state_attempt_before,
            expected_failure=False,
            allow_completed=True,
            acceptance_check="state.faultRefreshAccepted",
        )
        state_success, state_detail = wait_status(
            base,
            suburb,
            state,
            lambda status: success_newer_than(status, state_acceptance["baselineSuccess"]),
            CAPTURE_WAIT_SECONDS,
        )
        checks.record("state.liveSuccess", True, state_detail)
        checks.require("state.noDurableFile", state_files(cache_dir) == [], state_files(cache_dir))
        state_radar = validate_radar(
            base, fetch_radar(base, suburb, state).get("body"), suburb=suburb, state=state, require_fresh=True
        )
        checks.record("state.liveRadar", True, state_radar)
        checks.require(
            "state.newFolder",
            state_radar["cacheFolder"] not in state_folders_before,
            {"before": sorted(state_folders_before), "captured": state_radar["cacheFolder"]},
        )
        logs_state = container_logs(container_id, env, since=state_fault_started)
        warning_found = "not persisted" in logs_state or "Failed to persist" in logs_state
        checks.require(
            "state.persistenceWarning",
            warning_found,
            {"warningFound": warning_found},
        )
        _sydney_idle, sydney_idle_detail = wait_status(
            base,
            "Sydney",
            "NSW",
            lambda status: status_value(status, "isUpdating") is not True
            and status_value(status, "cacheExists") is True,
            CAPTURE_WAIT_SECONDS,
        )
        checks.record("state.sydneyIdleBeforeDelete", True, sydney_idle_detail, "controlled")
        delete_state = delete_location(base, "Sydney", "NSW")
        checks.require("state.deleteRejected", delete_state.get("status") == 500, delete_state)
        checks.require(
            "state.deleteCode",
            error_value(delete_state.get("body"), "errorCode") == "STORAGE_ERROR",
            delete_state,
        )
        details = (delete_state.get("body") or {}).get("details") or {}
        checks.require(
            "state.deleteNoSnapshots",
            details.get("snapshotsDeleted") is False
            or (details.get("SnapshotsDeleted") is False),
            {"details": details},
        )
        checks.require("state.sydneyStillServed", True, validate_radar(
            base, fetch_radar(base, "Sydney", "NSW").get("body"), suburb="Sydney", state="NSW", require_fresh=False
        ))
    finally:
        fault_file = cache_dir / ".service"
        if fault_file.is_file() and not fault_file.is_symlink():
            fault_file.unlink()
        if state_backup.is_dir() and not (cache_dir / ".service").exists():
            os.replace(state_backup, cache_dir / ".service")
    checks.record("state.restored", (cache_dir / ".service").is_dir() and not state_backup.exists())
    checks.require("state.restoredStateFiles", len(state_files(cache_dir)) >= 1, [entry["name"] for entry in state_files(cache_dir)])
    _sydney_recovered_idle, sydney_recovered_detail = wait_status(
        base,
        "Sydney",
        "NSW",
        lambda status: status_value(status, "isUpdating") is not True
        and status_value(status, "cacheExists") is True,
        CAPTURE_WAIT_SECONDS,
    )
    checks.record("state.sydneyIdleBeforeRecoveredDelete", True, sydney_recovered_detail, "controlled")
    delete_recovered = delete_location(base, "Sydney", "NSW")
    checks.require("state.deleteRecovered", delete_recovered.get("status") == 200, delete_recovered)
    sydney_status = get_status(base, "Sydney", "NSW")
    checks.require(
        "state.sydneyDeleted",
        sydney_status is not None and status_value(sydney_status, "cacheExists") is False,
        sydney_status,
    )
    checks.require("state.pomonaUnaffected", True, validate_radar(
        base, fetch_radar(base, suburb, state).get("body"), suburb=suburb, state=state, require_fresh=True
    ))
    checks.require("staging.emptyFinal", staging_entries(cache_dir) == [], staging_entries(cache_dir))
    checks.require("folders.noIncompleteFinal", incomplete_folders(cache_dir) == [], incomplete_folders(cache_dir))

    fault_exit(manifest, run_id=run_id, token=token)
    return {
        "classification": "real service/browser with controlled synthetic upstream and storage faults",
        "realBom": True,
        "syntheticSeed": sydney,
        "recovery": window,
        "stagingRecovery": kill_window,
        "stateFault": "controlled .service file replacement",
    }


def stage_timeout(ctx: dict[str, Any], checks: Checks) -> dict[str, Any]:
    manifest = ctx["manifest"]
    run_id = ctx["runId"]
    token = ctx["token"]
    cache_dir = ctx["cacheDir"]
    base = ctx["baseUrl"]
    suburb, state = "Pomona", "QLD"

    verify_owned_container(manifest, run_id=run_id, token=token, require_running=True)
    install = install_fault_server(manifest, run_id=run_id, token=token)
    checks.record("faultServer.installed", True, install, "controlled")
    checks.require("faultServer.mode", fault_mode(manifest, run_id=run_id, token=token, value="redirect") == "redirect", None, "controlled")
    ready = wait_http_ready(base, "/api/ready", READY_WAIT_SECONDS)
    checks.require("readiness.initial", ready.get("ok") is True, ready)

    timeout_status_before = get_status(base, suburb, state)
    timeout_success_before = parse_status_time(timeout_status_before, "lastSuccessfulCaptureAt")
    timeout_attempt_before = parse_status_time(timeout_status_before, "lastUpdateAttempt")
    seeded = seed_legacy_snapshot(
        cache_dir,
        suburb=suburb,
        state=state,
        when=utc_now() - timedelta(seconds=TIMEOUT_SEED_AGE_SECONDS),
    )
    checks.record("fallback.seed", True, seeded, "synthetic")
    seed_status = get_status(base, suburb, state)
    checks.record("timeout.seedStatus", True, seed_status, "controlled-config")
    checks.require(
        "timeout.seedStale",
        status_value(seed_status, "cacheIsValid") is False,
        seed_status,
        "controlled-config",
    )
    before = complete_folder_hashes(cache_dir)
    timeout_acceptance = trigger_recapture(
        base,
        suburb,
        state,
        label="timeout",
        checks=checks,
        baseline_success=timeout_success_before,
        baseline_attempt=timeout_attempt_before,
        expected_failure=True,
        allow_completed=True,
        classification="controlled-config",
    )
    timeout_status, timeout_detail = wait_status(
        base,
        suburb,
        state,
        lambda status: timeout_predicate(status)
        and attempt_newer_than(status, timeout_acceptance["baselineAttempt"]),
        FAULT_STATUS_WAIT_SECONDS,
    )
    checks.record("timeout.status", True, timeout_detail, "controlled-config")
    checks.require("timeout.code", status_value(timeout_status, "errorCode") == "TIMEOUT_ERROR", timeout_detail)
    checks.require(
        "timeout.newAttempt",
        attempt_newer_than(timeout_status, timeout_attempt_before),
        {"before": utc_text(timeout_attempt_before) if timeout_attempt_before else None,
         "after": status_value(timeout_status, "lastUpdateAttempt")},
    )
    checks.require("timeout.reservationReleased", status_value(timeout_status, "isUpdating") is False, timeout_detail)
    checks.require("timeout.stagingEmpty", staging_entries(cache_dir) == [], staging_entries(cache_dir))
    checks.require("timeout.noIncomplete", incomplete_folders(cache_dir) == [], incomplete_folders(cache_dir))
    after = complete_folder_hashes(cache_dir)
    checks.require("timeout.noNewPartial", set(after) == set(before), {"before": before, "after": after})
    fallback_radar = validate_radar(
        base, fetch_radar(base, suburb, state).get("body"), suburb=suburb, state=state, require_fresh=False
    )
    checks.require(
        "timeout.fallbackServed",
        fallback_radar["cacheFolder"] == seeded["folder"],
        fallback_radar,
        "synthetic",
    )
    fault_exit(manifest, run_id=run_id, token=token)
    return {
        "classification": "controlled 5s capture deadline; fallback synthetic",
        "faultActiveUntil": utc_text(utc_now()),
        "seed": seeded,
    }


def old_stage_common(ctx: dict[str, Any], checks: Checks, *, label: str) -> dict[str, Any]:
    manifest = ctx["manifest"]
    run_id = ctx["runId"]
    token = ctx["token"]
    cache_dir = ctx["cacheDir"]
    base = ctx["baseUrl"]
    expected = old_image_id(docker_env(manifest))
    actual = (manifest.get("container") or {}).get("imageId")
    checks.require(f"{label}.imagePinned", actual == expected, {"manifest": actual, "old": expected})
    ready = wait_http_ready(base, "/api/health", READY_WAIT_SECONDS)
    checks.require(f"{label}.health", ready.get("ok") is True, ready)
    return {"oldImageId": expected, "containerImageId": actual}


def stage_upgrade_prepare(ctx: dict[str, Any], checks: Checks) -> dict[str, Any]:
    manifest = ctx["manifest"]
    run_id = ctx["runId"]
    token = ctx["token"]
    cache_dir = ctx["cacheDir"]
    base = ctx["baseUrl"]
    identity = old_stage_common(ctx, checks, label="prepare")
    seed_time = utc_now() - timedelta(minutes=1)
    stop_exact(manifest, run_id=run_id, token=token)
    clear_directory_contents(cache_dir)
    seed = seed_legacy_snapshot(cache_dir, suburb="Pomona", state="QLD", when=seed_time)
    checks.record("prepare.seed", True, seed, "synthetic")
    start_exact(manifest, run_id=run_id, token=token)
    ready = wait_http_ready(base, "/api/health", READY_WAIT_SECONDS)
    checks.require("prepare.restartHealth", ready.get("ok") is True, ready)
    served = validate_radar_compat(
        base,
        fetch_radar(base, "Pomona", "QLD").get("body"),
        suburb="Pomona",
        state="QLD",
        expected_folder=seed["folder"],
    )
    checks.require("prepare.oldServesSeed", True, served, "synthetic")
    checks.require(
        "prepare.observationMatches",
        sv.parse_utc(served["observation"]) == sv.parse_utc(seed["lastObservation"]),
        {"served": served["observation"], "seed": seed["lastObservation"]},
    )

    data = data_root()
    backup = data / "backup"
    checks.require("prepare.backupPathFree", not backup.exists(), str(backup))
    backup_manifest = copy_tree_verified(cache_dir, backup)
    write_json_exclusive(data / "backup.manifest.json", backup_manifest)
    checks.record("prepare.backup", True, {"treeSha256": backup_manifest["treeSha256"], "files": backup_manifest["fileCount"], "path": str(backup)})
    return {
        "classification": "real old retained image with synthetic legacy seed",
        **identity,
        "seed": seed,
        "served": served,
        "backup": {"path": str(backup), "treeSha256": backup_manifest["treeSha256"]},
    }


def stage_upgrade_candidate(ctx: dict[str, Any], checks: Checks) -> dict[str, Any]:
    manifest = ctx["manifest"]
    run_id = ctx["runId"]
    token = ctx["token"]
    cache_dir = ctx["cacheDir"]
    base = ctx["baseUrl"]
    verify_owned_container(manifest, run_id=run_id, token=token, require_running=True)
    candidate_image = (manifest.get("container") or {}).get("imageId")
    checks.record("upgrade.candidateImage", True, candidate_image)
    ready = wait_http_ready(base, "/api/ready", READY_WAIT_SECONDS)
    checks.require("upgrade.readiness", ready.get("ok") is True, ready)

    data = data_root()
    backup = data / "backup"
    if not backup.is_dir() or backup.is_symlink():
        raise ExerciseError(f"backup copy is missing or unsafe: {backup}")
    backup_manifest = read_json(data / "backup.manifest.json")
    verify_tree_manifest(backup, backup_manifest, "backup")
    backup_folders = complete_folder_hashes(backup)
    expected_legacy_folder = next(
        (name for name in backup_folders if name.startswith("Pomona_QLD_")),
        None,
    )
    checks.require(
        "upgrade.backupLegacyFolder",
        expected_legacy_folder is not None,
        sorted(backup_folders),
    )

    stop_exact(manifest, run_id=run_id, token=token)
    clear_directory_contents(cache_dir)
    copy_tree_into_empty(backup, cache_dir)
    checks.record("upgrade.copiedBackup", True, backup_manifest["treeSha256"])
    start_exact(manifest, run_id=run_id, token=token)
    ready_after = wait_http_ready(base, "/api/ready", READY_WAIT_SECONDS)
    checks.require("upgrade.readinessAfterSeed", ready_after.get("ok") is True, ready_after)
    legacy = validate_radar(
        base, fetch_radar(base, "Pomona", "QLD").get("body"), suburb="Pomona", state="QLD", require_fresh=False
    )
    checks.require("upgrade.legacyServed", True, legacy)
    checks.require(
        "upgrade.legacyFolder",
        legacy["cacheFolder"] == expected_legacy_folder,
        {"expected": expected_legacy_folder, "served": legacy["cacheFolder"]},
    )
    legacy_before = complete_folder_hashes(cache_dir)
    legacy_status = get_status(base, "Pomona", "QLD")
    legacy_success = parse_status_time(legacy_status, "lastSuccessfulCaptureAt")
    legacy_attempt = parse_status_time(legacy_status, "lastUpdateAttempt")
    checks.require(
        "upgrade.legacyBaseline",
        legacy_success is not None,
        {"status": legacy_status, "expectedFolder": expected_legacy_folder},
    )

    upgrade_acceptance = trigger_recapture(
        base,
        "Pomona",
        "QLD",
        label="upgrade",
        checks=checks,
        baseline_success=legacy_success,
        baseline_attempt=legacy_attempt,
        expected_failure=False,
        allow_completed=True,
    )
    captured_status, captured_detail = wait_status(
        base,
        "Pomona",
        "QLD",
        lambda status: success_newer_than(status, upgrade_acceptance["baselineSuccess"]),
        CAPTURE_WAIT_SECONDS,
    )
    checks.record("upgrade.captureSuccess", True, captured_detail)
    checks.require(
        "upgrade.successNewer",
        success_newer_than(captured_status, legacy_success),
        {"legacy": legacy_success.isoformat() if legacy_success else None,
         "captured": status_value(captured_status, "lastSuccessfulCaptureAt")},
    )
    fresh = validate_radar(
        base, fetch_radar(base, "Pomona", "QLD").get("body"), suburb="Pomona", state="QLD", require_fresh=True
    )
    checks.record("upgrade.freshRadar", True, fresh, "real-bom")
    checks.require(
        "upgrade.freshDistinct",
        fresh["cacheFolder"] != expected_legacy_folder
        and fresh["cacheFolder"] not in legacy_before,
        {"legacy": expected_legacy_folder, "fresh": fresh["cacheFolder"], "before": sorted(legacy_before)},
    )
    legacy_after = complete_folder_hashes(cache_dir)
    checks.require(
        "upgrade.legacyUnchanged",
        all(legacy_after.get(name) == digest for name, digest in legacy_before.items()),
        {"before": legacy_before, "after": legacy_after},
    )

    verify_tree_manifest(backup, backup_manifest, "backup after upgrade")
    checks.record("upgrade.backupUnchanged", True, backup_manifest["treeSha256"])
    upgraded = data / "upgraded"
    checks.require("upgrade.copyPathFree", not upgraded.exists(), str(upgraded))
    upgraded_manifest = copy_tree_verified(cache_dir, upgraded)
    write_json_exclusive(data / "upgraded.manifest.json", upgraded_manifest)
    checks.record("upgrade.copy", True, {"treeSha256": upgraded_manifest["treeSha256"], "files": upgraded_manifest["fileCount"], "path": str(upgraded)})
    return {
        "classification": "candidate image writing on a copy of synthetic legacy seed",
        "candidateImageId": candidate_image,
        "legacy": legacy,
        "fresh": fresh,
        "backup": {"treeSha256": backup_manifest["treeSha256"]},
        "upgraded": {"treeSha256": upgraded_manifest["treeSha256"], "path": str(upgraded)},
    }


def stage_rollback(ctx: dict[str, Any], checks: Checks) -> dict[str, Any]:
    manifest = ctx["manifest"]
    run_id = ctx["runId"]
    token = ctx["token"]
    cache_dir = ctx["cacheDir"]
    base = ctx["baseUrl"]
    identity = old_stage_common(ctx, checks, label="rollback")
    data = data_root()
    backup = data / "backup"
    if not backup.is_dir() or backup.is_symlink():
        raise ExerciseError(f"backup copy is missing or unsafe: {backup}")
    backup_manifest = read_json(data / "backup.manifest.json")
    verify_tree_manifest(backup, backup_manifest, "backup before rollback")

    stop_exact(manifest, run_id=run_id, token=token)
    clear_directory_contents(cache_dir)
    copy_tree_into_empty(backup, cache_dir)
    start_exact(manifest, run_id=run_id, token=token)
    ready = wait_http_ready(base, "/api/health", READY_WAIT_SECONDS)
    checks.require("rollback.health", ready.get("ok") is True, ready)
    complete = [path.name for path in cache_folders(cache_dir) if (path / ".complete").is_file()]
    expected_folder = next(
        (name for name in complete if name.startswith("Pomona_QLD_")),
        None,
    )
    served = validate_radar_compat(
        base,
        fetch_radar(base, "Pomona", "QLD").get("body"),
        suburb="Pomona",
        state="QLD",
        expected_folder=expected_folder,
    )
    checks.require("rollback.servesBackup", True, served, "synthetic")
    verify_tree_manifest(backup, backup_manifest, "backup after rollback")
    checks.record("rollback.backupUnchanged", True, backup_manifest["treeSha256"])
    return {
        "classification": "real old retained image on a copy of the pre-upgrade backup",
        **identity,
        "served": served,
        "backup": {"treeSha256": backup_manifest["treeSha256"], "path": str(backup)},
    }


STAGES: dict[str, Callable[[dict[str, Any], Checks], dict[str, Any]]] = {
    "fault": stage_fault,
    "timeout": stage_timeout,
    "upgrade-prepare": stage_upgrade_prepare,
    "upgrade-candidate": stage_upgrade_candidate,
    "rollback": stage_rollback,
}


def evidence_path(run_id: str, stage: str) -> Path:
    return ROOT / "artifacts" / "execution" / "runner" / "runs" / run_id / f"fault-rollback-{stage}.json"


def run_stage(stage: str) -> int:
    checks = Checks(stage)
    started = utc_now()
    payload: dict[str, Any] = {
        "schema": "fault-rollback-stage/v1",
        "stage": stage,
        "status": "failed",
        "startedAt": utc_text(started),
        "finishedAt": None,
        "checks": checks.items,
        "error": None,
    }
    run_id = (os.environ.get("BOM_TEST_RUN_ID") or "").strip()
    try:
        ctx = manifest_for_stage(stage)
        payload.update({
            "runId": ctx["runId"],
            "cacheDir": str(ctx["cacheDir"]),
            "baseUrl": ctx["baseUrl"],
            "imageId": (ctx["manifest"].get("container") or {}).get("imageId"),
        })
        result = STAGES[stage](ctx, checks)
        failures = checks.required_failures()
        if failures:
            raise ExerciseError("required checks failed: " + ", ".join(failures))
        payload["result"] = result
        payload["status"] = "passed"
        code = 0
    except UnsafeState as exc:
        payload["error"] = f"UnsafeState: {exc}"
        code = 4
    except ExerciseError as exc:
        payload["error"] = f"ExerciseError: {exc}"
        code = 1
    except Exception as exc:  # noqa: BLE001 - record exact bounded failure
        payload["error"] = f"{type(exc).__name__}: {exc}"
        code = 1
    finally:
        payload["finishedAt"] = utc_text(utc_now())
        payload["checks"] = checks.items
        payload["elapsedSeconds"] = (utc_now() - started).total_seconds()
        if RUN_ID_RE.fullmatch(run_id):
            path = evidence_path(run_id, stage)
            try:
                write_json_exclusive(path, payload)
                payload["evidence"] = str(path)
            except Exception as exc:  # noqa: BLE001
                code = 1
                payload["status"] = "failed"
                payload["error"] = f"evidence write failed: {type(exc).__name__}: {exc}"
                print(f"could not write evidence: {exc}", file=sys.stderr)
        print(json.dumps(payload, indent=2, sort_keys=True))
    return code


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=sorted(STAGES))
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return run_stage(args.stage)


if __name__ == "__main__":
    raise SystemExit(main())
