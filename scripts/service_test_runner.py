#!/usr/bin/env python3
"""Dedicated-unit runner for bounded BOM service and test runs.

Safety model (2.0.0)
--------------------
Every executable run is launched through ``systemd-run --user`` as a
transient service whose name is ``bom-local-test-<32 hex>.service``.  The unit
has a cryptographically random ownership token in its description and
environment, a finite ``RuntimeMaxSec``/``TimeoutStopSec``, ``Restart=no``,
``KillMode=control-group`` and CPU/memory/task bounds.  There is deliberately
no local, foreground or unmanaged fallback.

Ownership is recorded once in ``ownership.json`` using an exclusive create.
The live unit identity (``InvocationID`` and canonical ``ControlGroup``) is
recorded in ``unit-identity.json`` only after the generated unit, token,
``Transient=yes`` and control-group ownership have been verified.  Cleanup
requires that manifest and identity to match both on disk and in systemd.

Cleanup never enumerates ``/proc``, never uses a PID or process group, and
never calls ``os.kill``/``killpg``/``pkill``.  The only unit-level destructive
operation is ``systemctl --user stop`` on the exact generated unit after a
read-only identity check.  A failed or uncertain check performs no destructive
action and reports ``cleanup_failed``.  Container cleanup uses only exact
container/network IDs discovered through the full run id + ownership token
label pair, after inspecting every ID and matching the recorded image and
labels.  A failed inspection is never treated as an empty result.

CLI
---
    scripts/run-service-test.sh preflight
    scripts/run-service-test.sh run --mode command -- <argv...>
    scripts/run-service-test.sh run --mode native \
        --service-command "dotnet BomLocalService.dll" \
        --readiness-url http://127.0.0.1:{port}/api/ready \
        --test-command "dotnet test ..."
    scripts/run-service-test.sh run --mode container --image IMAGE \
        --test-command "dotnet test ..." --max-duration 30h --detach
    scripts/run-service-test.sh status --run-id ID
    scripts/run-service-test.sh wait --run-id ID
    scripts/run-service-test.sh cleanup --run-id ID
    scripts/run-service-test.sh logs --run-id ID [--tail N]

``status``, ``logs`` and ``wait`` are read-only.  ``wait`` never performs a
surprise stop or cleanup.  Only the explicit ``cleanup`` command may stop the
verified unit; the unit's own ``ExecStopPost`` finalizer performs the bounded
post-stop evidence and container/cache cleanup.

Exit codes: 0 pass, 1 command/test failure, 2 usage, 3 preflight/supervision
failure, 4 cleanup failure, 5 supervisor/startup failure, 124 deadline,
128+N external interruption.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime
import fcntl
import hashlib
import json
import math
import os
import pathlib
import posixpath
import re
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

SCHEMA_VERSION = 2
RUNNER_VERSION = "2.0.0"
ROOT = pathlib.Path(__file__).resolve().parent.parent
RUN_ROOT = ROOT / "artifacts" / "execution" / "runner" / "runs"
SAFETY_HOLD = ROOT / "artifacts" / "execution" / "runner" / "SAFETY_HOLD"
DEFAULT_COMPOSE_FILE = ROOT / "docker-compose.test.yml"
CGROUP_ROOT = pathlib.Path("/sys/fs/cgroup")

UNIT_PREFIX = "bom-local-test-"
UNIT_RE = re.compile(r"^bom-local-test-[0-9a-f]{32}\.service$")
RUN_ID_RE = re.compile(r"^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$")
TOKEN_RE = re.compile(r"^[0-9a-f]{64}$")
INVOCATION_RE = re.compile(r"^[0-9a-f]{32}$")
IMAGE_ID_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
CPU_QUOTA_RE = re.compile(r"^[1-9][0-9]{0,3}%$")
MEMORY_RE = re.compile(r"^[1-9][0-9]*[KMGTP]?$")

RUN_ID_LABEL = "io.bom.test.run-id"
TOKEN_LABEL = "io.bom.test.owner-token"
RUNNER_LABEL = "io.bom.test.runner-version"
ROLE_LABEL = "io.bom.test.role"
PROJECT_LABEL = "com.docker.compose.project"

OWNERSHIP_NAME = "ownership.json"
UNIT_IDENTITY_NAME = "unit-identity.json"
BOUNDARY_NAME = "boundary.json"
SUMMARY_NAME = "summary.json"
STATE_NAME = "state.json"
SCHEMA_NAME = "schema.json"
FINALIZATION_NAME = "finalization.json"
REFUSED_NAME = "cleanup-refused.json"
EVENTS_NAME = "events.jsonl"
MAX_CAPTURE_CHARS = 24000
LONG_RUN_SECONDS = 3600.0
RESERVED_ENV = {
    "BOM_TEST_OWNER_TOKEN", "BOM_TEST_RUN_ID", "BOM_TEST_UNIT",
    "BOM_TEST_BOOT_ID", "BOM_TEST_PID_NAMESPACE",
    "BOM_TEST_CGROUP_NAMESPACE", "BOM_TEST_RUNNER_HASH",
}
RESERVED_ENV_UPPER = {
    "ASPNETCORE_URLS", "CACHEDIRECTORY", "CACHEDDIRECTORY",
    "CACHE_DIRECTORY", "DOTNET_URLS", "URLS",
}
# Application settings may be overridden, ownership/resource settings may not.
# Only recorded serviceEnv values reach these compose interpolation variables.
CONTAINER_SETTING_ENV = {
    "ASPNETCORE_ENVIRONMENT": "BOM_TEST_ASPNETCORE_ENVIRONMENT",
    "CORS__ALLOWEDORIGINS": "BOM_TEST_CORS_ALLOWEDORIGINS",
    "CORS__ALLOWEDMETHODS": "BOM_TEST_CORS_ALLOWEDMETHODS",
    "CORS__ALLOWEDHEADERS": "BOM_TEST_CORS_ALLOWEDHEADERS",
    "CACHERETENTIONHOURS": "BOM_TEST_CACHE_RETENTION_HOURS",
    "CACHEEXPIRATIONMINUTES": "BOM_TEST_CACHE_EXPIRATION_MINUTES",
    "CACHEMANAGEMENT__CHECKINTERVALMINUTES": "BOM_TEST_CACHE_CHECK_INTERVAL_MINUTES",
    "CACHEMANAGEMENT__INITIALDELAYSECONDS": "BOM_TEST_CACHE_INITIAL_DELAY_SECONDS",
    "CACHEMANAGEMENT__LOCATIONSTAGGERSECONDS": "BOM_TEST_CACHE_LOCATION_STAGGER_SECONDS",
    "CACHECLEANUP__INTERVALHOURS": "BOM_TEST_CACHE_CLEANUP_INTERVAL_HOURS",
    "REFRESH__QUEUECAPACITY": "BOM_TEST_REFRESH_QUEUE_CAPACITY",
    "REFRESH__MAXCONCURRENTCAPTURES": "BOM_TEST_REFRESH_MAX_CONCURRENT_CAPTURES",
    "REFRESH__CAPTURETIMEOUTSECONDS": "BOM_TEST_REFRESH_CAPTURE_TIMEOUT_SECONDS",
    "REFRESH__BACKOFFBASESECONDS": "BOM_TEST_REFRESH_BACKOFF_BASE_SECONDS",
    "REFRESH__MAXBACKOFFSECONDS": "BOM_TEST_REFRESH_MAX_BACKOFF_SECONDS",
    "SCREENSHOT__DYNAMICCONTENTWAITMS": "BOM_TEST_SCREENSHOT_DYNAMIC_CONTENT_WAIT_MS",
    "SCREENSHOT__TILERENDERWAITMS": "BOM_TEST_SCREENSHOT_TILE_RENDER_WAIT_MS",
    "DEBUG__ENABLED": "BOM_TEST_DEBUG_ENABLED",
    "DEBUG__WAITMS": "BOM_TEST_DEBUG_WAIT_MS",
    "SCRAPING__BASEURL": "BOM_TEST_SCRAPING_BASE_URL",
    "TIMEZONE": "BOM_TEST_TIMEZONE",
}

DOCKER_ENV_KEYS = (
    "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG",
    "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH",
)


class RunnerError(Exception):
    """Base class for expected runner failures."""


class PreflightError(RunnerError):
    """The dedicated-unit safety contract cannot be satisfied."""


class InspectionError(RunnerError):
    """An inspection failed; the result must not be treated as empty."""


# ---------------------------------------------------------------------------
# Time, JSON, paths and small utilities
# ---------------------------------------------------------------------------


def utcnow() -> datetime.datetime:
    return datetime.datetime.now(datetime.timezone.utc)


def iso(value: datetime.datetime | None = None) -> str:
    value = value or utcnow()
    return value.isoformat(timespec="milliseconds").replace("+00:00", "Z")


def parse_duration(text: str) -> float:
    raw = str(text).strip().lower()
    match = re.fullmatch(r"([0-9]+(?:\.[0-9]+)?)\s*([smhd]?)", raw)
    if not match:
        raise argparse.ArgumentTypeError(f"invalid duration: {text!r}")
    amount = float(match.group(1))
    factor = {"": 1.0, "s": 1.0, "m": 60.0, "h": 3600.0, "d": 86400.0}[match.group(2)]
    seconds = amount * factor
    if seconds <= 0 or not math.isfinite(seconds):
        raise argparse.ArgumentTypeError("duration must be positive")
    return seconds


def human_duration(seconds: float) -> str:
    seconds = float(seconds)
    for suffix, factor in (("d", 86400.0), ("h", 3600.0), ("m", 60.0)):
        if seconds >= factor:
            return f"{seconds / factor:g}{suffix}"
    return f"{seconds:g}s"


def parse_kv(values: list[str] | None, option: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for value in values or []:
        if "=" not in value:
            raise argparse.ArgumentTypeError(f"{option} expects KEY=VALUE, got {value!r}")
        key, item = value.split("=", 1)
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            raise argparse.ArgumentTypeError(f"{option} has an invalid key: {key!r}")
        upper = key.upper()
        if (key in RESERVED_ENV or upper.startswith("BOM_TEST_")
                or upper in RESERVED_ENV_UPPER or upper.startswith("DOCKER_")):
            raise argparse.ArgumentTypeError(
                f"{option} may not override runner/config variable {key!r}")
        result[key] = item
    return result


def truncate(text: str | None, limit: int = MAX_CAPTURE_CHARS) -> str:
    if not text:
        return ""
    if len(text) <= limit:
        return text
    return text[:limit] + f"\n... truncated {len(text) - limit} chars ...\n"


def safe_json(path: pathlib.Path) -> dict | None:
    """Read an evidence JSON file, refusing symlinks."""
    if path.is_symlink():
        raise RunnerError(f"refusing symlink evidence path: {path}")
    if not path.exists():
        return None
    if not path.is_file():
        raise RunnerError(f"evidence path is not a regular file: {path}")
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as exc:
        raise RunnerError(f"unreadable evidence file {path}: {type(exc).__name__}: {exc}") from exc
    if not isinstance(data, dict):
        raise RunnerError(f"evidence file is not a JSON object: {path}")
    return data


def ensure_owned_logs_dir(run_dir: pathlib.Path) -> pathlib.Path:
    """Create and validate the owned log directory before systemd-run."""
    logs = run_dir / "logs"
    if logs.is_symlink():
        raise RunnerError(f"refusing symlink log directory: {logs}")
    if logs.exists():
        if not logs.is_dir():
            raise RunnerError(f"owned log path is not a directory: {logs}")
    else:
        logs.mkdir(mode=0o700)
    with contextlib.suppress(OSError):
        logs.chmod(0o700)
    if logs.is_symlink() or not logs.is_dir():
        raise RunnerError(f"owned log directory validation failed: {logs}")
    return logs


def atomic_write_json(path: pathlib.Path, data: object) -> None:
    if path.parent.is_symlink():
        raise RunnerError(f"refusing symlink evidence directory: {path.parent}")
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent))
    tmp = pathlib.Path(tmp_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(data, handle, indent=2, sort_keys=False, default=str)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp, path)
    finally:
        with contextlib.suppress(FileNotFoundError):
            tmp.unlink()


def append_event(run_dir: pathlib.Path, event: str, **fields: object) -> None:
    path = run_dir / EVENTS_NAME
    if path.is_symlink():
        raise RunnerError(f"refusing symlink evidence path: {path}")
    payload = (json.dumps({"ts": iso(), "event": event, **fields},
                          separators=(",", ":"), default=str) + "\n").encode("utf-8")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND | getattr(os, "O_NOFOLLOW", 0), 0o600)
    try:
        os.write(fd, payload)
    finally:
        os.close(fd)


def write_text(path: pathlib.Path, text: str, append: bool = False) -> None:
    if path.is_symlink():
        raise RunnerError(f"refusing symlink evidence path: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    mode = "a" if append else "w"
    with open(path, mode, encoding="utf-8") as handle:
        handle.write(text)


def new_run_id() -> str:
    return time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + secrets.token_hex(4)


def new_unit_name() -> str:
    return UNIT_PREFIX + secrets.token_hex(16) + ".service"


def new_owner_token() -> str:
    return secrets.token_hex(32)


def _runner_hash() -> str:
    return hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest()


RUNNER_PATH = pathlib.Path(__file__).resolve()
RUNNER_HASH = _runner_hash()


def boot_id() -> str:
    try:
        value = pathlib.Path("/proc/sys/kernel/random/boot_id").read_text(
            encoding="ascii").strip()
    except OSError as exc:
        raise RunnerError(f"host boot id is unavailable: {exc}") from exc
    if not re.fullmatch(r"[0-9a-fA-F-]{36}", value):
        raise RunnerError("host boot id is malformed")
    return value.lower()


def namespace_inode(name: str) -> int:
    try:
        return int(os.stat(f"/proc/self/ns/{name}").st_ino)
    except OSError as exc:
        raise RunnerError(f"{name} namespace identity is unavailable: {exc}") from exc


def pid_namespace_inode() -> int:
    return namespace_inode("pid")


def current_binding() -> dict:
    return {
        "bootId": boot_id(),
        "pidNamespace": pid_namespace_inode(),
        "cgroupNamespace": namespace_inode("cgroup"),
        "runnerHash": RUNNER_HASH,
        "runnerPath": str(RUNNER_PATH),
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
    }


def assert_current_binding(manifest: dict) -> dict:
    binding = manifest.get("binding") or {}
    current = current_binding()
    errors: list[str] = []
    for key in ("bootId", "pidNamespace", "cgroupNamespace", "runnerHash",
                "runnerPath", "schemaVersion", "runnerVersion"):
        if binding.get(key) != current.get(key):
            errors.append(f"ownership binding {key} does not match this boot/runner")
    if errors:
        raise RunnerError("; ".join(errors))
    return current


def ownership_digest(manifest: dict, identity: dict | None = None) -> str:
    payload = {
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "ownerToken": manifest["ownerToken"],
        "binding": manifest["binding"],
        "invocationId": (identity or {}).get("invocationId"),
    }
    raw = json.dumps(payload, separators=(",", ":"), sort_keys=True).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def validate_run_id(run_id: str) -> str:
    if not RUN_ID_RE.fullmatch(run_id or ""):
        raise PreflightError(
            "run id must be a generated id of the form YYYYMMDDTHHMMSSZ-<8 hex>")
    return run_id


def validate_unit_name(unit: str) -> str:
    if not UNIT_RE.fullmatch(unit or ""):
        raise RunnerError(f"refusing non-generated unit name: {unit!r}")
    return unit


def run_dir_for(run_id: str) -> pathlib.Path:
    validate_run_id(run_id)
    if RUN_ROOT.is_symlink():
        raise RunnerError(f"refusing symlink run root: {RUN_ROOT}")
    return RUN_ROOT / run_id


def resolve_run_dir(run_id: str) -> pathlib.Path:
    run_dir = run_dir_for(run_id)
    if run_dir.is_symlink():
        raise RunnerError(f"refusing symlink evidence directory: {run_dir}")
    if not run_dir.is_dir():
        raise RunnerError(f"run not found: {run_dir}")
    return run_dir


def exec_capture(
    argv: list[str],
    timeout: float = 20.0,
    env: dict[str, str] | None = None,
    cwd: pathlib.Path | None = None,
) -> dict:
    """Run one bounded inspection/command and preserve failures as data."""
    started = utcnow()
    try:
        completed = subprocess.run(
            argv,
            timeout=timeout,
            env=env,
            cwd=str(cwd) if cwd else None,
            text=True,
            capture_output=True,
            check=False,
        )
        return {
            "ok": completed.returncode == 0,
            "argv": argv,
            "returncode": completed.returncode,
            "stdout": truncate(completed.stdout),
            "stderr": truncate(completed.stderr),
            "timedOut": False,
            "startedAt": iso(started),
            "finishedAt": iso(),
        }
    except subprocess.TimeoutExpired as exc:
        stdout = exc.stdout.decode("utf-8", "replace") if isinstance(exc.stdout, bytes) else (exc.stdout or "")
        stderr = exc.stderr.decode("utf-8", "replace") if isinstance(exc.stderr, bytes) else (exc.stderr or "")
        return {
            "ok": False,
            "argv": argv,
            "returncode": None,
            "stdout": truncate(stdout),
            "stderr": truncate(stderr),
            "timedOut": True,
            "error": f"timed out after {timeout:g}s",
            "startedAt": iso(started),
            "finishedAt": iso(),
        }
    except OSError as exc:
        return {
            "ok": False,
            "argv": argv,
            "returncode": None,
            "stdout": "",
            "stderr": "",
            "timedOut": False,
            "error": f"{type(exc).__name__}: {exc}",
            "startedAt": iso(started),
            "finishedAt": iso(),
        }


# ---------------------------------------------------------------------------
# Manifest and unit identity
# ---------------------------------------------------------------------------


def make_manifest(
    *,
    run_id: str,
    unit: str,
    token: str,
    evidence_dir: str,
    mode: str,
    cache_dir: str = "",
    cache_root: str = "",
    cache_owned: bool = True,
    keep_cache: bool = False,
    network: dict | None = None,
    resources: dict | None = None,
    runtime_max_seconds: int = 2400,
    max_duration_seconds: float = 1800.0,
    outer_timeout_seconds: int | None = None,
    startup_timeout_seconds: float = 180.0,
    shutdown_grace_seconds: float = 20.0,
    service_command: list[str] | None = None,
    test_command: list[str] | None = None,
    service_env: dict[str, str] | None = None,
    test_env: dict[str, str] | None = None,
    readiness_url: str | None = None,
    compose_file: str | None = None,
    container: dict | None = None,
    created_at: str | None = None,
    binding: dict | None = None,
) -> dict:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "runId": run_id,
        "unit": unit,
        "ownerToken": token,
        "mode": mode,
        "binding": binding or current_binding(),
        "createdAt": created_at or iso(),
        "evidenceDir": evidence_dir,
        "cacheDir": cache_dir,
        "cacheRoot": cache_root,
        "cacheOwned": bool(cache_owned),
        "keepCache": bool(keep_cache),
        "network": network or {"host": "127.0.0.1", "port": 0},
        "resources": resources or {
            "cpuQuota": "200%", "memoryMax": "2G", "tasksMax": 512,
            "cpus": "2.0", "memLimit": "2g", "pidsLimit": 512,
        },
        "maxDurationSeconds": float(max_duration_seconds),
        "outerTimeoutSeconds": int(outer_timeout_seconds or runtime_max_seconds),
        "runtimeMaxSeconds": int(outer_timeout_seconds or runtime_max_seconds),
        "startupTimeoutSeconds": float(startup_timeout_seconds),
        "shutdownGraceSeconds": float(shutdown_grace_seconds),
        "commands": {
            "service": service_command,
            "test": test_command,
        },
        "serviceEnv": service_env or {},
        "testEnv": test_env or {},
        "readinessUrl": readiness_url,
        "composeFile": compose_file,
        "container": container,
    }


def validate_manifest(data: dict, run_dir: pathlib.Path) -> dict:
    if not isinstance(data, dict):
        raise RunnerError("ownership manifest is not a JSON object")
    if data.get("schemaVersion") != SCHEMA_VERSION:
        raise RunnerError(
            f"refusing manifest schema {data.get('schemaVersion')!r}; expected {SCHEMA_VERSION}")
    if data.get("runnerVersion") != RUNNER_VERSION:
        raise RunnerError(
            f"refusing manifest from runner version {data.get('runnerVersion')!r}; "
            f"expected {RUNNER_VERSION}")
    run_id = str(data.get("runId") or "")
    validate_run_id(run_id)
    if run_dir.name != run_id:
        raise RunnerError("manifest run id does not match its evidence directory")
    unit = str(data.get("unit") or "")
    validate_unit_name(unit)
    token = str(data.get("ownerToken") or "")
    if not TOKEN_RE.fullmatch(token):
        raise RunnerError("manifest ownership token is not a 64-character random token")
    binding = data.get("binding")
    if not isinstance(binding, dict):
        raise RunnerError("manifest is missing its immutable boot/namespace/runner binding")
    if not re.fullmatch(r"[0-9a-fA-F-]{36}", str(binding.get("bootId") or "")):
        raise RunnerError("manifest boot id is malformed")
    if not isinstance(binding.get("pidNamespace"), int) or binding["pidNamespace"] <= 0:
        raise RunnerError("manifest pid namespace identity is malformed")
    if not isinstance(binding.get("cgroupNamespace"), int) or binding["cgroupNamespace"] <= 0:
        raise RunnerError("manifest cgroup namespace identity is malformed")
    if not re.fullmatch(r"[0-9a-f]{64}", str(binding.get("runnerHash") or "")):
        raise RunnerError("manifest runner hash is malformed")
    if not str(binding.get("runnerPath") or ""):
        raise RunnerError("manifest runner path is missing")
    if binding.get("schemaVersion") != SCHEMA_VERSION:
        raise RunnerError("manifest binding schema does not match this runner")
    if binding.get("runnerVersion") != RUNNER_VERSION:
        raise RunnerError("manifest binding runner version does not match this runner")
    evidence = str(data.get("evidenceDir") or "")
    try:
        evidence_path = pathlib.Path(evidence).resolve()
    except OSError as exc:
        raise RunnerError(f"manifest evidence path is invalid: {exc}") from exc
    if evidence_path != run_dir.resolve():
        raise RunnerError("manifest evidence path does not match the requested run directory")
    if data.get("mode") not in {"command", "native", "container"}:
        raise RunnerError(f"unknown manifest mode: {data.get('mode')!r}")
    cache_dir = str(data.get("cacheDir") or "")
    cache_root = str(data.get("cacheRoot") or "")
    if data.get("cacheOwned"):
        if not cache_dir or not cache_root:
            raise RunnerError("owned cache is missing its immutable path/root")
        try:
            cache_path = pathlib.Path(cache_dir).resolve()
            root_path = pathlib.Path(cache_root).resolve()
        except OSError as exc:
            raise RunnerError(f"owned cache path is invalid: {exc}") from exc
        if cache_path.parent != root_path:
            raise RunnerError("owned cache path is not a direct child of its recorded root")
        expected_prefix = f"bom-local-test-{run_id}-cache-"
        if not cache_path.name.startswith(expected_prefix):
            raise RunnerError("owned cache path does not carry the run's generated prefix")
    if not isinstance(data.get("maxDurationSeconds"), (int, float)) or data["maxDurationSeconds"] <= 0:
        raise RunnerError("manifest maxDurationSeconds must be positive")
    if not isinstance(data.get("outerTimeoutSeconds"), int) or data["outerTimeoutSeconds"] <= 0:
        raise RunnerError("manifest outerTimeoutSeconds must be a positive integer")
    if not isinstance(data.get("runtimeMaxSeconds"), int) or data["runtimeMaxSeconds"] <= 0:
        raise RunnerError("manifest runtimeMaxSeconds must be a positive integer")
    if data["runtimeMaxSeconds"] != data["outerTimeoutSeconds"]:
        raise RunnerError("manifest systemd RuntimeMaxSec must equal the fixed outer timeout")
    if not isinstance(data.get("startupTimeoutSeconds"), (int, float)) or data["startupTimeoutSeconds"] <= 0:
        raise RunnerError("manifest startupTimeoutSeconds must be positive")
    if not isinstance(data.get("shutdownGraceSeconds"), (int, float)) or data["shutdownGraceSeconds"] <= 0:
        raise RunnerError("manifest shutdownGraceSeconds must be positive")
    minimum_outer = (float(data["maxDurationSeconds"]) + float(data["startupTimeoutSeconds"])
                     + float(data["shutdownGraceSeconds"]))
    if data["outerTimeoutSeconds"] < minimum_outer:
        raise RunnerError(
            "outer timeout is shorter than workload + startup + shutdown; "
            "refusing silent truncation")
    if data.get("mode") == "container":
        container = data.get("container")
        if not isinstance(container, dict):
            raise RunnerError("container manifest is missing its image identity")
        image_id = str(container.get("imageId") or "")
        if not IMAGE_ID_RE.fullmatch(image_id):
            raise RunnerError("container manifest does not record an exact image id")
        if not re.fullmatch(r"bom-local-test-[0-9a-f]{12}", str(container.get("project") or "")):
            raise RunnerError("container manifest does not record a generated compose project")
        if not isinstance(container.get("dockerEnv"), dict):
            raise RunnerError("container manifest does not pin the docker endpoint environment")
        if not str(container.get("endpoint") or ""):
            raise RunnerError("container manifest does not record a docker endpoint identity")
    return data


def create_manifest(run_dir: pathlib.Path, manifest: dict) -> pathlib.Path:
    validate_manifest(manifest, run_dir)
    path = run_dir / OWNERSHIP_NAME
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    try:
        fd = os.open(path, flags, 0o600)
    except FileExistsError as exc:
        raise RunnerError(f"refusing to overwrite existing ownership manifest: {path}") from exc
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(manifest, handle, indent=2, sort_keys=False, default=str)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
    except Exception:
        with contextlib.suppress(OSError):
            path.unlink()
        raise
    return path


def load_manifest(run_dir: pathlib.Path, require_binding: bool = False) -> dict:
    data = safe_json(run_dir / OWNERSHIP_NAME)
    if data is None:
        raise RunnerError(f"ownership manifest not found: {run_dir / OWNERSHIP_NAME}")
    manifest = validate_manifest(data, run_dir)
    if require_binding:
        assert_current_binding(manifest)
    return manifest


def validate_unit_identity(manifest: dict, identity: dict) -> dict:
    if identity.get("schemaVersion") != SCHEMA_VERSION:
        raise RunnerError("unit identity schema does not match this runner")
    if identity.get("runnerVersion") != RUNNER_VERSION:
        raise RunnerError("unit identity was written by another runner version")
    if identity.get("runId") != manifest["runId"]:
        raise RunnerError("unit identity run id does not match the manifest")
    if identity.get("unit") != manifest["unit"]:
        raise RunnerError("unit identity unit name does not match the manifest")
    if identity.get("ownerToken") != manifest["ownerToken"]:
        raise RunnerError("unit identity owner token does not match the manifest")
    if identity.get("binding") != manifest.get("binding"):
        raise RunnerError("unit identity boot/namespace/runner binding does not match the manifest")
    expected_digest = ownership_digest(manifest, identity)
    if identity.get("ownershipDigest") != expected_digest:
        raise RunnerError("unit identity ownership digest is missing or does not match")
    invocation = str(identity.get("invocationId") or "")
    if not INVOCATION_RE.fullmatch(invocation):
        raise RunnerError("unit identity InvocationID is missing or malformed")
    control_group = str(identity.get("controlGroup") or "")
    ownership = control_group_belongs_to_unit(manifest["unit"], control_group)
    if not ownership["ok"]:
        raise RunnerError("unit identity control group is not owned by the generated unit: "
                          + "; ".join(ownership["errors"]))
    if identity.get("transient") is not True:
        raise RunnerError("unit identity does not record Transient=yes")
    return identity


def persist_unit_identity(run_dir: pathlib.Path, manifest: dict, identity: dict) -> dict:
    validate_unit_identity(manifest, identity)
    path = run_dir / UNIT_IDENTITY_NAME
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    try:
        fd = os.open(path, flags, 0o600)
    except FileExistsError:
        existing = load_unit_identity(run_dir, manifest)
        for key in ("runId", "unit", "ownerToken", "invocationId", "controlGroup"):
            if existing.get(key) != identity.get(key):
                raise RunnerError(f"existing unit identity has a different {key}")
        return existing
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(identity, handle, indent=2, sort_keys=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
    except Exception:
        with contextlib.suppress(OSError):
            path.unlink()
        raise
    return identity


def create_unit_identity(run_dir: pathlib.Path, manifest: dict, metadata: dict) -> dict:
    verification = verify_unit_metadata(
        manifest["unit"], manifest["ownerToken"], metadata.get("InvocationID"),
        metadata, binding=manifest.get("binding"))
    if not verification["ok"]:
        raise RunnerError("refusing to record unverified unit identity: "
                          + "; ".join(verification["errors"]))
    identity = {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "ownerToken": manifest["ownerToken"],
        "binding": manifest["binding"],
        "invocationId": metadata["InvocationID"],
        "controlGroup": metadata["ControlGroup"],
        "transient": True,
        "verificationSource": "systemd-show",
        "recordedAt": iso(),
    }
    identity["ownershipDigest"] = ownership_digest(manifest, identity)
    return persist_unit_identity(run_dir, manifest, identity)


def record_supervisor_identity(run_dir: pathlib.Path, manifest: dict) -> dict:
    invocation = str(os.environ.get("INVOCATION_ID") or "")
    if not INVOCATION_RE.fullmatch(invocation):
        raise RunnerError("supervisor INVOCATION_ID is missing or malformed")
    control_group = proc_self_cgroup_path()
    ownership = control_group_belongs_to_unit(manifest["unit"], control_group)
    if not ownership["ok"]:
        raise RunnerError("supervisor cgroup is not owned by the generated unit: "
                          + "; ".join(ownership["errors"]))
    identity = {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "ownerToken": manifest["ownerToken"],
        "binding": manifest["binding"],
        "invocationId": invocation,
        "controlGroup": control_group,
        "transient": True,
        "verificationSource": "supervisor-env-self-cgroup",
        "recordedAt": iso(),
    }
    identity["ownershipDigest"] = ownership_digest(manifest, identity)
    return persist_unit_identity(run_dir, manifest, identity)


def load_unit_identity(run_dir: pathlib.Path, manifest: dict) -> dict:
    data = safe_json(run_dir / UNIT_IDENTITY_NAME)
    if data is None:
        raise RunnerError(f"unit identity not found: {run_dir / UNIT_IDENTITY_NAME}")
    return validate_unit_identity(manifest, data)


# ---------------------------------------------------------------------------
# Read-only systemd identity checks
# ---------------------------------------------------------------------------


def control_group_belongs_to_unit(unit: str, control_group: str | None,
                                  uid: int | None = None) -> dict:
    """Return whether a canonical systemd ControlGroup is exactly this unit's."""
    uid = os.getuid() if uid is None else int(uid)
    errors: list[str] = []
    if not UNIT_RE.fullmatch(unit or ""):
        errors.append("unit name is not a generated bom-local-test unit")
    path = control_group or ""
    if not path.startswith("/"):
        errors.append("control group is not an absolute canonical path")
    else:
        if "//" in path or "/./" in path or "/../" in path or path.endswith("/"):
            errors.append("control group path is not canonical")
        else:
            normalized = posixpath.normpath(path)
            if normalized != path:
                errors.append("control group path is not canonical")
            parts = [part for part in normalized.split("/") if part]
            if not parts:
                errors.append("control group is empty")
            else:
                if parts[-1] != unit:
                    errors.append(f"control group does not end with exact unit {unit!r}")
                expected_user_slice = f"/user.slice/user-{uid}.slice/"
                if not normalized.startswith(expected_user_slice):
                    errors.append("control group is outside this user's manager slice")
                manager = f"user@{uid}.service"
                for part in parts[:-1]:
                    if part.endswith(".service") and part != manager:
                        errors.append(f"control group has an inherited service ancestor: {part}")
                    if part.endswith(".scope"):
                        errors.append(f"control group has an inherited scope ancestor: {part}")
    return {"ok": not errors, "errors": errors, "unit": unit, "controlGroup": path}


def parse_systemd_show(stdout: str) -> dict:
    properties: dict[str, str] = {}
    for line in (stdout or "").splitlines():
        if "=" in line:
            key, value = line.split("=", 1)
            properties[key] = value
    return properties


def systemd_show(unit: str, exec_fn=None, timeout: float = 10.0) -> dict:
    validate_unit_name(unit)
    exec_fn = exec_fn or exec_capture
    result = exec_fn([
        "systemctl", "--user", "show",
        "-p", "Id",
        "-p", "LoadState",
        "-p", "ActiveState",
        "-p", "SubState",
        "-p", "Result",
        "-p", "MainPID",
        "-p", "InvocationID",
        "-p", "Transient",
        "-p", "ControlGroup",
        "-p", "Description",
        "-p", "Environment",
        "-p", "FragmentPath",
        unit,
    ], timeout=timeout)
    props = parse_systemd_show(result.get("stdout") or "")
    props["_ok"] = result.get("ok", False)
    props["_error"] = result.get("error") or (result.get("stderr") or "").strip()
    return props


def verify_unit_metadata(unit: str, token: str, invocation_id: str | None,
                         metadata: dict, uid: int | None = None,
                         binding: dict | None = None) -> dict:
    """Pure read-only verification of exact generated unit ownership."""
    errors: list[str] = []
    if not UNIT_RE.fullmatch(unit or ""):
        errors.append(f"unit name {unit!r} is not a generated bom-local-test unit")
    if metadata.get("_ok") is not True:
        errors.append("systemctl show failed; unit metadata is not trustworthy")
    if not TOKEN_RE.fullmatch(token or ""):
        errors.append("expected ownership token is malformed")
    if metadata.get("Id") != unit:
        errors.append(f"systemd Id {metadata.get('Id')!r} is not the exact generated unit")
    if metadata.get("Transient") != "yes":
        errors.append("unit is not Transient=yes")
    actual_invocation = str(metadata.get("InvocationID") or "")
    if not INVOCATION_RE.fullmatch(actual_invocation):
        errors.append("systemd InvocationID is missing or malformed")
    elif invocation_id is not None and actual_invocation != invocation_id:
        errors.append("systemd InvocationID does not match the recorded run identity")
    load_state = metadata.get("LoadState")
    if load_state != "loaded":
        errors.append(f"unit LoadState is not 'loaded': {load_state!r}")
    active_state = metadata.get("ActiveState")
    if active_state not in {"active", "activating", "deactivating", "inactive", "failed"}:
        errors.append(f"unit ActiveState is missing or unknown: {active_state!r}")
    description = metadata.get("Description") or ""
    if f"owner={token}" not in description:
        errors.append("unit description does not carry the expected ownership token")
    environment = metadata.get("Environment") or ""
    if f"BOM_TEST_OWNER_TOKEN={token}" not in environment:
        errors.append("unit environment does not carry the expected ownership token")
    if binding:
        expected_env = {
            "BOM_TEST_BOOT_ID": str(binding.get("bootId")),
            "BOM_TEST_PID_NAMESPACE": str(binding.get("pidNamespace")),
            "BOM_TEST_CGROUP_NAMESPACE": str(binding.get("cgroupNamespace")),
            "BOM_TEST_RUNNER_HASH": str(binding.get("runnerHash")),
        }
        for key, value in expected_env.items():
            if f"{key}={value}" not in environment:
                errors.append(f"unit environment does not carry {key} binding")
    ownership = control_group_belongs_to_unit(unit, metadata.get("ControlGroup"), uid=uid)
    if not ownership["ok"]:
        errors.extend(ownership["errors"])
    return {
        "ok": not errors,
        "errors": errors,
        "unit": unit,
        "invocationId": actual_invocation or None,
        "controlGroup": metadata.get("ControlGroup"),
        "transient": metadata.get("Transient"),
        "activeState": active_state,
        "loadState": load_state,
    }


def verify_live_unit(manifest: dict, identity: dict, exec_fn=None) -> dict:
    validate_unit_identity(manifest, identity)
    metadata = systemd_show(manifest["unit"], exec_fn=exec_fn)
    verification = verify_unit_metadata(
        manifest["unit"], manifest["ownerToken"], identity["invocationId"],
        metadata, binding=manifest.get("binding"))
    if metadata.get("ControlGroup") != identity.get("controlGroup"):
        verification["errors"].append("live ControlGroup does not match the recorded identity")
        verification["ok"] = False
    verification["metadata"] = metadata
    return verification


# ---------------------------------------------------------------------------
# Unit launch and read-only status
# ---------------------------------------------------------------------------


def systemd_quote(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def systemd_command(argv: list[str]) -> str:
    return " ".join(systemd_quote(item) for item in argv)


def systemd_run_command(manifest: dict, run_dir: pathlib.Path) -> list[str]:
    unit = validate_unit_name(manifest["unit"])
    log_path = run_dir / "logs" / "supervisor.log"
    finalizer = [
        sys.executable,
        str(pathlib.Path(__file__).resolve()),
        "__finalize",
        "--run-id", manifest["runId"],
        "--source", "systemd-exec-stop-post",
    ]
    timeout_stop = max(20, int(manifest["shutdownGraceSeconds"]) + 10)
    resources = manifest["resources"]
    return [
        "systemd-run", "--user",
        "--unit", unit,
        "--description",
        (f"BOM local test run={manifest['runId']} owner={manifest['ownerToken']} "
         f"boot={manifest['binding']['bootId']} "
         f"runner={manifest['binding']['runnerHash'][:16]} mode={manifest['mode']}"),
        "--collect",
        "--property=Type=exec",
        "--property=Restart=no",
        "--property=KillMode=control-group",
        f"--property=TimeoutStopSec={timeout_stop}",
        f"--property=RuntimeMaxSec={int(manifest['runtimeMaxSeconds'])}",
        f"--property=CPUQuota={resources['cpuQuota']}",
        f"--property=MemoryMax={resources['memoryMax']}",
        "--property=MemorySwapMax=0",
        f"--property=TasksMax={int(resources['tasksMax'])}",
        f"--property=WorkingDirectory={ROOT}",
        f"--property=StandardOutput=append:{log_path}",
        f"--property=StandardError=append:{log_path}",
        f"--property=ExecStopPost={systemd_command(finalizer)}",
        f"--setenv=BOM_TEST_OWNER_TOKEN={manifest['ownerToken']}",
        f"--setenv=BOM_TEST_RUN_ID={manifest['runId']}",
        f"--setenv=BOM_TEST_UNIT={unit}",
        f"--setenv=BOM_TEST_BOOT_ID={manifest['binding']['bootId']}",
        f"--setenv=BOM_TEST_PID_NAMESPACE={manifest['binding']['pidNamespace']}",
        f"--setenv=BOM_TEST_CGROUP_NAMESPACE={manifest['binding']['cgroupNamespace']}",
        f"--setenv=BOM_TEST_RUNNER_HASH={manifest['binding']['runnerHash']}",
        "--setenv=PYTHONUNBUFFERED=1",
        "--",
        sys.executable,
        str(pathlib.Path(__file__).resolve()),
        "__supervise",
        "--run-id", manifest["runId"],
    ]


def wait_for_persisted_identity(run_dir: pathlib.Path, manifest: dict,
                                timeout: float = 25.0) -> dict | None:
    """Launcher consumes the supervisor's persisted proof; no systemd race."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            return load_unit_identity(run_dir, manifest)
        except RunnerError:
            time.sleep(0.1)
    append_event(run_dir, "unit_identity_failed",
                 error="supervisor identity was not persisted before timeout")
    return None


# ---------------------------------------------------------------------------
# Capabilities, network and cache
# ---------------------------------------------------------------------------


def port_available(host: str, port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind((host, port))
            return True
        except OSError:
            return False


def allocate_port(host: str) -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind((host, 0))
        return int(sock.getsockname()[1])


def detect_capabilities(exec_fn=None) -> dict:
    exec_fn = exec_fn or exec_capture
    version = exec_fn(["systemd-run", "--version"], timeout=10)
    user_manager = exec_fn(["systemctl", "--user", "show-environment"], timeout=10)
    running = exec_fn(["systemctl", "--user", "is-system-running"], timeout=10)
    running_text = ((running.get("stdout") or "") + (running.get("stderr") or "")).strip()
    user = os.environ.get("USER") or str(os.getuid())
    linger = exec_fn(["loginctl", "show-user", user, "-p", "Linger"], timeout=10)
    docker = exec_fn(["docker", "info", "--format", "{{.ServerVersion}}"], timeout=12)
    compose = exec_fn(["docker", "compose", "version", "--short"], timeout=12)
    systemd_ok = bool(version.get("ok") and user_manager.get("ok"))
    return {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "generatedAt": iso(),
        "systemd": {
            "ok": systemd_ok,
            "version": (version.get("stdout") or "").splitlines()[0] if version.get("stdout") else None,
            "userManager": bool(user_manager.get("ok")),
            "isSystemRunning": running_text or None,
            "linger": "Linger=yes" in (linger.get("stdout") or ""),
        },
        "cgroup2": (CGROUP_ROOT / "cgroup.controllers").exists(),
        "docker": {
            "ok": bool(docker.get("ok") and compose.get("ok")),
            "serverVersion": (docker.get("stdout") or "").strip() or None,
            "composeVersion": (compose.get("stdout") or "").strip() or None,
        },
    }


def require_capabilities(caps: dict, mode: str, duration: float) -> None:
    if not caps.get("systemd", {}).get("ok"):
        raise PreflightError("dedicated transient user systemd units are unavailable")
    if not caps.get("cgroup2"):
        raise PreflightError("cgroup v2 is required for final cgroup emptiness verification")
    if duration > LONG_RUN_SECONDS and not caps.get("systemd", {}).get("linger"):
        raise PreflightError(
            "runs longer than one hour require systemd user linger for unattended supervision")
    if mode == "container" and not caps.get("docker", {}).get("ok"):
        raise PreflightError("docker daemon and docker compose are required for container mode")


def resolve_image_id(image: str, exec_fn=None,
                     env: dict[str, str] | None = None) -> str:
    exec_fn = exec_fn or exec_capture
    result = exec_fn(["docker", "image", "inspect", "--format", "{{.Id}}", image],
                     timeout=20, env=env)
    image_id = (result.get("stdout") or "").strip()
    if not result.get("ok") or not IMAGE_ID_RE.fullmatch(image_id):
        raise PreflightError(
            f"exact docker image id is unavailable for {image!r}; build/pull it before running")
    return image_id


def capture_docker_env() -> dict[str, str]:
    return {key: os.environ[key] for key in DOCKER_ENV_KEYS if key in os.environ}


def docker_env_from_manifest(manifest: dict) -> dict[str, str]:
    env = os.environ.copy()
    for key in list(env):
        if key.upper().startswith("DOCKER_"):
            env.pop(key, None)
    env.update((manifest.get("container") or {}).get("dockerEnv") or {})
    return env


def probe_docker_endpoint(docker_env: dict[str, str], exec_fn=None) -> str:
    exec_fn = exec_fn or exec_capture
    result = exec_fn(
        ["docker", "info", "--format", "{{.ID}}|{{.Name}}|{{.ServerVersion}}"],
        timeout=15, env=docker_env)
    identity = (result.get("stdout") or "").strip()
    if not result.get("ok") or not identity:
        raise PreflightError(
            "docker endpoint identity is unavailable: "
            + (result.get("error") or (result.get("stderr") or "").strip()
               or "docker info failed"))
    return identity


def docker_endpoint_matches(manifest: dict, exec_fn=None) -> dict:
    expected = str((manifest.get("container") or {}).get("endpoint") or "")
    if not expected:
        return {"ok": False, "error": "manifest has no docker endpoint identity"}
    try:
        actual = probe_docker_endpoint(docker_env_from_manifest(manifest), exec_fn=exec_fn)
    except PreflightError as exc:
        return {"ok": False, "error": str(exc)}
    if actual != expected:
        return {"ok": False, "error": "docker endpoint identity changed",
                "expected": expected, "actual": actual}
    return {"ok": True, "endpoint": actual}


# ---------------------------------------------------------------------------
# Container inspection (read-only) and exact cleanup
# ---------------------------------------------------------------------------


def container_env(manifest: dict) -> dict[str, str]:
    env = docker_env_from_manifest(manifest)
    for key in list(env):
        if key.upper().startswith("BOM_TEST_"):
            env.pop(key, None)
    container = manifest.get("container") or {}
    env.update({
        "BOM_TEST_PROJECT": str(container.get("project") or ""),
        "BOM_TEST_RUN_ID": manifest["runId"],
        "BOM_TEST_OWNER_TOKEN": manifest["ownerToken"],
        "BOM_TEST_RUNNER_VERSION": RUNNER_VERSION,
        "BOM_TEST_HOST_PORT": str(manifest["network"]["port"]),
        "BOM_TEST_CACHE_DIR": manifest["cacheDir"],
        "BOM_TEST_UID": str(os.getuid()),
        "BOM_TEST_GID": str(os.getgid()),
        "BOM_TEST_CPUS": str(manifest["resources"].get("cpus", "2.0")),
        "BOM_TEST_MEMORY_LIMIT": str(manifest["resources"].get("memLimit", "2g")),
        "BOM_TEST_PIDS_LIMIT": str(manifest["resources"].get("pidsLimit", 512)),
        "BOM_TEST_IMAGE": str(container.get("imageId") or ""),
    })
    service_env = manifest.get("serviceEnv") or {}
    env.update(service_env)
    for key, value in service_env.items():
        compose_key = CONTAINER_SETTING_ENV.get(key.upper())
        if compose_key is not None:
            env[compose_key] = value
    # Docker endpoint stays pinned even if a tampered manifest smuggled a
    # serviceEnv override past normal argument validation.
    env.update(container.get("dockerEnv") or {})
    return env


def compose_base(manifest: dict) -> list[str]:
    container = manifest.get("container") or {}
    return ["docker", "compose", "-f", str(manifest.get("composeFile") or DEFAULT_COMPOSE_FILE),
            "-p", str(container.get("project") or "")]


def _docker_list(manifest: dict, kind: str, exec_fn=None) -> dict:
    exec_fn = exec_fn or exec_capture
    env = docker_env_from_manifest(manifest)
    if kind == "container":
        argv = ["docker", "ps", "-aq", "--no-trunc",
                "--filter", f"label={TOKEN_LABEL}={manifest['ownerToken']}",
                "--filter", f"label={RUN_ID_LABEL}={manifest['runId']}"]
    else:
        argv = ["docker", "network", "ls", "-q", "--no-trunc",
                "--filter", f"label={TOKEN_LABEL}={manifest['ownerToken']}",
                "--filter", f"label={RUN_ID_LABEL}={manifest['runId']}"]
    result = exec_fn(argv, timeout=20, env=env)
    if not result.get("ok"):
        return {"ok": False, "ids": [],
                "error": result.get("error") or (result.get("stderr") or "").strip()
                or f"docker {kind} listing failed"}
    ids = [line.strip() for line in (result.get("stdout") or "").splitlines() if line.strip()]
    return {"ok": True, "ids": ids, "command": result}


def _docker_inspect(kind: str, object_id: str, exec_fn=None,
                    env: dict[str, str] | None = None) -> dict:
    exec_fn = exec_fn or exec_capture
    result = exec_fn(["docker", kind, "inspect", object_id], timeout=20, env=env)
    if not result.get("ok"):
        return {"ok": False, "id": object_id,
                "error": result.get("error") or (result.get("stderr") or "").strip()
                or f"docker {kind} inspect failed"}
    try:
        parsed = json.loads(result.get("stdout") or "[]")
    except ValueError as exc:
        return {"ok": False, "id": object_id, "error": f"docker inspect JSON is invalid: {exc}"}
    if not isinstance(parsed, list) or len(parsed) != 1 or not isinstance(parsed[0], dict):
        return {"ok": False, "id": object_id, "error": "docker inspect did not return one object"}
    return {"ok": True, "id": object_id, "data": parsed[0]}


def verify_container_inventory(manifest: dict, exec_fn=None) -> dict:
    """Read-only inventory of resources carrying the exact run/token labels."""
    endpoint = docker_endpoint_matches(manifest, exec_fn=exec_fn)
    if not endpoint.get("ok"):
        return {"ok": False, "empty": False, "errors": [endpoint.get("error")],
                "containerIds": None, "networkIds": None,
                "containers": [], "networks": [], "endpoint": endpoint}
    env = docker_env_from_manifest(manifest)
    containers = _docker_list(manifest, "container", exec_fn=exec_fn)
    if not containers["ok"]:
        return {"ok": False, "empty": False, "errors": [containers["error"]],
                "containerIds": None, "networkIds": None,
                "containers": [], "networks": [], "endpoint": endpoint}
    errors: list[str] = []
    inspected_containers: list[dict] = []
    container_ids: list[str] = []
    for container_id in containers["ids"]:
        inspected = _docker_inspect("container", container_id, exec_fn=exec_fn, env=env)
        if not inspected["ok"]:
            errors.append(inspected["error"])
            continue
        data = inspected["data"]
        actual_id = str(data.get("Id") or "")
        labels = (data.get("Config") or {}).get("Labels") or {}
        if actual_id != container_id:
            errors.append(f"container id {container_id!r} does not match inspect id {actual_id!r}")
        if labels.get(TOKEN_LABEL) != manifest["ownerToken"]:
            errors.append(f"container {container_id} ownership token label mismatch")
        if labels.get(RUN_ID_LABEL) != manifest["runId"]:
            errors.append(f"container {container_id} run id label mismatch")
        if labels.get(RUNNER_LABEL) != RUNNER_VERSION:
            errors.append(f"container {container_id} runner version label mismatch")
        expected_project = (manifest.get("container") or {}).get("project")
        if labels.get(PROJECT_LABEL) != expected_project:
            errors.append(f"container {container_id} compose project label mismatch")
        expected_image = (manifest.get("container") or {}).get("imageId")
        if data.get("Image") != expected_image:
            errors.append(f"container {container_id} image id does not match the manifest")
        state = data.get("State") or {}
        container_ids.append(actual_id or container_id)
        inspected_containers.append({
            "id": actual_id or container_id,
            "role": labels.get(ROLE_LABEL),
            "status": state.get("Status"),
            "exitCode": state.get("ExitCode"),
            "health": (state.get("Health") or {}).get("Status"),
        })

    networks = _docker_list(manifest, "network", exec_fn=exec_fn)
    inspected_networks: list[dict] = []
    network_ids: list[str] = []
    if not networks["ok"]:
        errors.append(networks["error"])
    else:
        for network_id in networks["ids"]:
            inspected = _docker_inspect("network", network_id, exec_fn=exec_fn, env=env)
            if not inspected["ok"]:
                errors.append(inspected["error"])
                continue
            data = inspected["data"]
            actual_id = str(data.get("Id") or "")
            labels = data.get("Labels") or {}
            if actual_id != network_id:
                errors.append(f"network id {network_id!r} does not match inspect id {actual_id!r}")
            if labels.get(TOKEN_LABEL) != manifest["ownerToken"]:
                errors.append(f"network {network_id} ownership token label mismatch")
            if labels.get(RUN_ID_LABEL) != manifest["runId"]:
                errors.append(f"network {network_id} run id label mismatch")
            if labels.get(RUNNER_LABEL) != RUNNER_VERSION:
                errors.append(f"network {network_id} runner version label mismatch")
            expected_project = (manifest.get("container") or {}).get("project")
            if labels.get(PROJECT_LABEL) != expected_project:
                errors.append(f"network {network_id} compose project label mismatch")
            network_ids.append(actual_id or network_id)
            inspected_networks.append({"id": actual_id or network_id})

    return {
        "ok": not errors,
        "empty": not container_ids and not network_ids,
        "errors": errors,
        "containerIds": container_ids,
        "networkIds": network_ids,
        "containers": inspected_containers,
        "networks": inspected_networks,
        "endpoint": endpoint,
    }


def verify_container_id(manifest: dict, kind: str, object_id: str,
                        exec_fn=None) -> dict:
    exec_fn = exec_fn or exec_capture
    inspected = _docker_inspect(kind, object_id, exec_fn=exec_fn,
                                env=docker_env_from_manifest(manifest))
    if not inspected["ok"]:
        return {"ok": False, "id": object_id, "errors": [inspected["error"]]}
    data = inspected["data"]
    labels = ((data.get("Config") or {}).get("Labels")
              if kind == "container" else data.get("Labels")) or {}
    errors: list[str] = []
    if str(data.get("Id") or "") != object_id:
        errors.append(f"{kind} id changed before mutation")
    if labels.get(TOKEN_LABEL) != manifest["ownerToken"]:
        errors.append(f"{kind} ownership token label mismatch before mutation")
    if labels.get(RUN_ID_LABEL) != manifest["runId"]:
        errors.append(f"{kind} run id label mismatch before mutation")
    if labels.get(RUNNER_LABEL) != RUNNER_VERSION:
        errors.append(f"{kind} runner version label mismatch before mutation")
    expected_project = (manifest.get("container") or {}).get("project")
    if labels.get(PROJECT_LABEL) != expected_project:
        errors.append(f"{kind} compose project label mismatch before mutation")
    if kind == "container":
        expected_image = (manifest.get("container") or {}).get("imageId")
        if data.get("Image") != expected_image:
            errors.append("container image id changed before mutation")
    return {"ok": not errors, "id": object_id, "errors": errors, "data": data}


def container_cleanup(manifest: dict, exec_fn=None, source: str = "finalizer") -> dict:
    """Stop/remove only fully inspected exact container and network ids."""
    started = utcnow()
    exec_fn = exec_fn or exec_capture
    endpoint = docker_endpoint_matches(manifest, exec_fn=exec_fn)
    if not endpoint.get("ok"):
        return {
            "ok": False, "verifiedEmpty": False,
            "inspection": {"ok": False, "errors": [endpoint.get("error")]},
            "containerIdsBefore": None, "containerIdsAfter": None,
            "networkIdsBefore": None, "networkIdsAfter": None,
            "actions": [], "errors": [endpoint.get("error")],
            "attemptedAt": iso(started), "finishedAt": iso(), "source": source,
        }
    env = docker_env_from_manifest(manifest)
    inventory = verify_container_inventory(manifest, exec_fn=exec_fn)
    # A second read-only inventory immediately before the first mutation.
    if inventory["ok"]:
        inventory = verify_container_inventory(manifest, exec_fn=exec_fn)
    if not inventory["ok"]:
        return {
            "ok": False,
            "verifiedEmpty": False,
            "inspection": {"ok": False, "errors": inventory["errors"]},
            "containerIdsBefore": None,
            "containerIdsAfter": None,
            "networkIdsBefore": None,
            "networkIdsAfter": None,
            "actions": [],
            "errors": inventory["errors"],
            "attemptedAt": iso(started),
            "finishedAt": iso(),
            "source": source,
        }

    errors: list[str] = []
    actions: list[dict] = []
    containers_before = list(inventory["containerIds"])
    networks_before = list(inventory["networkIds"])
    grace = max(1, int(manifest["shutdownGraceSeconds"]))

    mutation_aborted = False
    for container_id in containers_before:
        current = verify_container_id(manifest, "container", container_id, exec_fn=exec_fn)
        if not current["ok"]:
            errors.extend(current["errors"])
            errors.append("container cleanup aborted before uncertain mutation")
            mutation_aborted = True
            break
        stop = exec_fn(["docker", "stop", "-t", str(grace), container_id],
                       timeout=grace + 30, env=env)
        actions.append({"action": "stop", "id": container_id,
                        "ok": stop.get("ok"),
                        "error": stop.get("error") or (stop.get("stderr") or "").strip()})
        current = verify_container_id(manifest, "container", container_id, exec_fn=exec_fn)
        if not current["ok"]:
            errors.extend(current["errors"])
            errors.append("container removal aborted before uncertain mutation")
            mutation_aborted = True
            break
        remove = exec_fn(["docker", "rm", "-f", container_id], timeout=45, env=env)
        actions.append({"action": "remove", "id": container_id,
                        "ok": remove.get("ok"),
                        "error": remove.get("error") or (remove.get("stderr") or "").strip()})
        if not remove.get("ok"):
            errors.append(f"exact container removal failed for {container_id}")

    if not mutation_aborted:
        for network_id in networks_before:
            current = verify_container_id(manifest, "network", network_id, exec_fn=exec_fn)
            if not current["ok"]:
                errors.extend(current["errors"])
                errors.append("network cleanup aborted before uncertain mutation")
                mutation_aborted = True
                break
            remove = exec_fn(["docker", "network", "rm", network_id], timeout=45, env=env)
            actions.append({"action": "network-remove", "id": network_id,
                            "ok": remove.get("ok"),
                            "error": remove.get("error") or (remove.get("stderr") or "").strip()})
            if not remove.get("ok"):
                errors.append(f"exact network removal failed for {network_id}")

    after = verify_container_inventory(manifest, exec_fn=exec_fn)
    if not after["ok"]:
        errors.extend(after["errors"])
    if after.get("containerIds"):
        errors.append("owned containers remain after exact cleanup")
    if after.get("networkIds"):
        errors.append("owned networks remain after exact cleanup")
    inspection_ok = bool(inventory["ok"] and after["ok"])
    verified_empty = bool(inspection_ok and not after.get("containerIds") and not after.get("networkIds"))
    return {
        "ok": bool(verified_empty and not errors),
        "verifiedEmpty": verified_empty,
        "inspection": {
            "ok": inspection_ok,
            "before": inventory,
            "after": after,
            "errors": errors,
        },
        "containerIdsBefore": containers_before,
        "containerIdsAfter": after.get("containerIds", []) if after["ok"] else None,
        "networkIdsBefore": networks_before,
        "networkIdsAfter": after.get("networkIds", []) if after["ok"] else None,
        "actions": actions,
        "mutationAborted": mutation_aborted,
        "errors": errors,
        "attemptedAt": iso(started),
        "finishedAt": iso(),
        "source": source,
    }


# ---------------------------------------------------------------------------
# Unit-owned cgroup emptiness and cache removal
# ---------------------------------------------------------------------------


def read_cgroup_observation(control_group: str, self_pid: int | None = None,
                            cgroup_root: pathlib.Path | None = None) -> dict:
    """Read the exact unit cgroup subtree for a positive emptiness observation.

    This is read-only.  A vanished path is explicitly *unverified*, not empty:
    the kernel removes a cgroup only when it has no live processes or child
    cgroups, but path absence observed in isolation does not prove what the
    boundary contained at stop time.  The observation therefore records whether
    the path existed while it was read, every non-finalizer PID in the exact
    subtree, per-directory ``cgroup.events`` values, and the finalizer's own PID
    that was excluded (ExecStopPost runs inside the unit cgroup).
    """
    root = cgroup_root or CGROUP_ROOT
    self_pid = os.getpid() if self_pid is None else int(self_pid)
    if (not control_group.startswith("/") or "/../" in control_group
            or "//" in control_group or control_group.endswith("/")):
        return {"ok": False, "exists": False, "empty": False, "verified": False,
                "remainingPids": [], "pids": [], "selfPids": [],
                "error": "control group path is not canonical"}
    base = root / control_group.lstrip("/")
    try:
        base_stat = base.lstat()
    except FileNotFoundError:
        return {
            "ok": True,
            "exists": False,
            "empty": False,
            "verified": False,
            "remainingPids": [],
            "pids": [],
            "selfPids": [],
            "path": str(base),
            "observedAt": iso(),
            "reason": "cgroup path vanished before a positive observation",
        }
    except OSError as exc:
        return {"ok": False, "exists": None, "empty": False, "verified": False,
                "remainingPids": [], "pids": [], "selfPids": [],
                "path": str(base), "error": f"{type(exc).__name__}: {exc}"}
    if not base.is_dir() or base.is_symlink():
        return {"ok": False, "exists": True, "empty": False, "verified": False,
                "remainingPids": [], "pids": [], "selfPids": [],
                "path": str(base), "error": "cgroup path is not a real directory"}
    directories: list[dict] = []
    remaining: set[int] = set()
    self_pids: set[int] = set()
    seen: set[int] = set()
    try:
        for current, dirnames, filenames in os.walk(base, topdown=True, followlinks=False):
            current_path = pathlib.Path(current)
            for name in list(dirnames):
                child = current_path / name
                if child.is_symlink():
                    return {"ok": False, "exists": True, "empty": False,
                            "verified": False, "remainingPids": sorted(remaining),
                            "pids": [], "selfPids": sorted(self_pids),
                            "path": str(base),
                            "error": f"refusing symlinked child cgroup: {child}"}
            procs_path = current_path / "cgroup.procs"
            events_path = current_path / "cgroup.events"
            try:
                procs_text = procs_path.read_text(encoding="utf-8")
            except OSError as exc:
                return {"ok": False, "exists": True, "empty": False,
                        "verified": False, "remainingPids": sorted(remaining),
                        "pids": [], "selfPids": sorted(self_pids),
                        "path": str(base),
                        "error": f"unreadable {procs_path}: {type(exc).__name__}: {exc}"}
            directory_pids: list[int] = []
            for line in procs_text.splitlines():
                line = line.strip()
                if not line:
                    continue
                try:
                    pid = int(line)
                except ValueError:
                    return {"ok": False, "exists": True, "empty": False,
                            "verified": False, "remainingPids": sorted(remaining),
                            "pids": [], "selfPids": sorted(self_pids),
                            "path": str(base),
                            "error": f"non-numeric pid in {procs_path}: {line!r}"}
                directory_pids.append(pid)
                if pid == self_pid:
                    self_pids.add(pid)
                else:
                    remaining.add(pid)
                seen.add(pid)
            populated = None
            try:
                events_text = events_path.read_text(encoding="utf-8")
                for line in events_text.splitlines():
                    if line.startswith("populated "):
                        populated = line.split(maxsplit=1)[1].strip()
            except OSError as exc:
                return {"ok": False, "exists": True, "empty": False,
                        "verified": False, "remainingPids": sorted(remaining),
                        "pids": sorted(seen), "selfPids": sorted(self_pids),
                        "path": str(base),
                        "error": f"unreadable {events_path}: {type(exc).__name__}: {exc}"}
            directories.append({
                "path": str(current_path.relative_to(base)) or ".",
                "pids": directory_pids,
                "populated": populated,
            })
    except OSError as exc:
        return {"ok": False, "exists": True, "empty": False, "verified": False,
                "remainingPids": sorted(remaining), "pids": sorted(seen),
                "selfPids": sorted(self_pids), "path": str(base),
                "error": f"cgroup subtree walk failed: {type(exc).__name__}: {exc}"}
    empty = not remaining
    all_populated_zero = bool(directories) and all(
        item.get("populated") == "0" for item in directories)
    return {
        "ok": True,
        "exists": True,
        "empty": empty,
        "allPopulatedZero": all_populated_zero,
        "verified": bool(empty and all_populated_zero),
        "remainingPids": sorted(remaining),
        "pids": sorted(seen),
        "selfPids": sorted(self_pids),
        "selfExcluded": bool(self_pids),
        "directories": directories,
        "path": str(base),
        "observedAt": iso(),
    }


def wait_cgroup_empty(control_group: str, timeout: float,
                      cgroup_root: pathlib.Path | None = None) -> dict:
    deadline = time.monotonic() + max(0.0, timeout)
    last = read_cgroup_observation(control_group, cgroup_root=cgroup_root)
    while (last.get("ok") and last.get("exists") and not last.get("empty")
           and time.monotonic() < deadline):
        time.sleep(0.25)
        last = read_cgroup_observation(control_group, cgroup_root=cgroup_root)
    return last


def cgroup2_mount_identity() -> dict:
    """Read the exact cgroup2 mount identity from this process's mountinfo."""
    try:
        lines = pathlib.Path("/proc/self/mountinfo").read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise RunnerError(f"mountinfo is unavailable: {exc}") from exc
    for line in lines:
        fields = line.split()
        if "-" not in fields:
            continue
        separator = fields.index("-")
        if separator < 6:
            continue
        mount_point = fields[4]
        fstype = fields[separator + 1]
        if mount_point == "/sys/fs/cgroup" and fstype == "cgroup2":
            root_stat = os.stat("/sys/fs/cgroup")
            return {
                "mountId": int(fields[0]),
                "parentId": int(fields[1]),
                "majorMinor": fields[2],
                "root": fields[3],
                "mountPoint": mount_point,
                "fstype": fstype,
                "dev": int(root_stat.st_dev),
                "ino": int(root_stat.st_ino),
            }
    raise RunnerError("the cgroup2 mount at /sys/fs/cgroup is unavailable")


def path_stat_identity(path: pathlib.Path) -> dict:
    try:
        value = path.lstat()
    except FileNotFoundError:
        return {"exists": False, "path": str(path), "errno": "ENOENT"}
    except OSError as exc:
        return {"exists": None, "path": str(path),
                "error": f"{type(exc).__name__}: {exc}"}
    return {
        "exists": True,
        "path": str(path),
        "dev": int(value.st_dev),
        "ino": int(value.st_ino),
        "mode": int(value.st_mode),
        "isDir": bool(value.st_mode & 0o040000),
        "isSymlink": bool(value.st_mode & 0o120000),
    }


def parent_identity(path: pathlib.Path) -> dict:
    parent = path.parent
    identity = path_stat_identity(parent)
    identity["listable"] = False
    if identity.get("exists") and identity.get("isDir") and not identity.get("isSymlink"):
        try:
            with os.scandir(parent) as entries:
                next(entries, None)
            identity["listable"] = True
        except OSError as exc:
            identity["error"] = f"{type(exc).__name__}: {exc}"
    return identity


def enoent_boundary_stable(control_group: str, boundary: dict,
                           cgroup_root: pathlib.Path | None = None) -> dict:
    """Settled ENOENT proof: exact directory gone, mount/parent unchanged."""
    try:
        mount_now = cgroup2_mount_identity()
    except RunnerError as exc:
        return {"ok": False, "error": str(exc)}
    if mount_now != boundary.get("cgroup2Mount"):
        return {"ok": False, "error": "cgroup2 mount identity changed"}
    path = (cgroup_root or CGROUP_ROOT) / control_group.lstrip("/")
    current = path_stat_identity(path)
    if current.get("exists") is not False:
        return {"ok": False, "error": "exact unit directory is not ENOENT",
                "path": current}
    parent_now = parent_identity(path)
    original_unit = boundary.get("unitCgroupStat") or {}
    original_parent = boundary.get("parentStat") or {}
    stable = bool(
        original_unit.get("exists") is True
        and int(original_unit.get("dev", -1)) == int(mount_now.get("dev", -2))
        and parent_now.get("exists") is True
        and parent_now.get("isDir") is True
        and parent_now.get("listable") is True
        and parent_now.get("dev") == original_parent.get("dev")
        and parent_now.get("ino") == original_parent.get("ino"))
    return {"ok": stable, "path": current, "parent": parent_now,
            "mount": mount_now,
            "error": None if stable else "parent/mount identity is not stable"}


def proc_self_cgroup_path() -> str:
    """Read *this* process's own cgroup for a positive equality check only."""
    try:
        lines = pathlib.Path("/proc/self/cgroup").read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise RunnerError(f"/proc/self/cgroup is unavailable: {exc}") from exc
    for line in lines:
        parts = line.split(":", 2)
        if len(parts) == 3 and parts[0] == "0" and parts[1] == "":
            path = parts[2].strip()
            if path.startswith("/") and "/../" not in path and "//" not in path:
                return path
    raise RunnerError("a canonical unified cgroup path is unavailable")


def make_boundary(manifest: dict, identity: dict, metadata: dict) -> dict:
    validate_unit_identity(manifest, identity)
    if metadata.get("_ok") is not True or metadata.get("LoadState") != "loaded":
        raise RunnerError("live unit metadata is not loaded and inspectable")
    main_pid = metadata.get("MainPID")
    try:
        main_pid_int = int(main_pid)
    except (TypeError, ValueError) as exc:
        raise RunnerError("unit MainPID is missing") from exc
    if main_pid_int != os.getpid():
        raise RunnerError("unit MainPID is not this supervisor process")
    own_cgroup = proc_self_cgroup_path()
    if own_cgroup != identity["controlGroup"]:
        raise RunnerError(
            f"supervisor cgroup {own_cgroup!r} is not the exact recorded unit cgroup")
    observation = read_cgroup_observation(own_cgroup, self_pid=os.getpid())
    if not observation.get("ok") or not observation.get("exists"):
        raise RunnerError("unit cgroup could not be observed from the supervisor")
    if os.getpid() not in (observation.get("pids") or []):
        raise RunnerError("supervisor PID is not a member of the recorded unit cgroup")
    mount = cgroup2_mount_identity()
    unit_stat = path_stat_identity(CGROUP_ROOT / own_cgroup.lstrip("/"))
    if not unit_stat.get("exists") or not unit_stat.get("isDir"):
        raise RunnerError("unit cgroup directory identity is unavailable")
    if int(unit_stat.get("dev", -1)) != int(mount["dev"]):
        raise RunnerError("unit cgroup is not on the recorded cgroup2 mount")
    boundary = {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "ownerToken": manifest["ownerToken"],
        "binding": manifest["binding"],
        "invocationId": identity["invocationId"],
        "controlGroup": identity["controlGroup"],
        "ownershipDigest": identity["ownershipDigest"],
        "supervisorPid": os.getpid(),
        "mainPid": main_pid_int,
        "procSelfCgroup": own_cgroup,
        "mainPidInCgroup": True,
        "selfInCgroup": True,
        "observedAt": iso(),
        "pidNamespace": manifest["binding"]["pidNamespace"],
        "cgroupNamespace": manifest["binding"]["cgroupNamespace"],
        "bootId": manifest["binding"]["bootId"],
        "cgroup2Mount": mount,
        "unitCgroupStat": unit_stat,
        "parentStat": parent_identity(CGROUP_ROOT / own_cgroup.lstrip("/")),
    }
    validate_boundary(manifest, identity, boundary)
    return boundary


def validate_boundary(manifest: dict, identity: dict, boundary: dict) -> dict:
    validate_unit_identity(manifest, identity)
    errors: list[str] = []
    if boundary.get("schemaVersion") != SCHEMA_VERSION:
        errors.append("boundary schema does not match")
    if boundary.get("runnerVersion") != RUNNER_VERSION:
        errors.append("boundary runner version does not match")
    if boundary.get("runId") != manifest["runId"]:
        errors.append("boundary run id does not match")
    if boundary.get("unit") != manifest["unit"]:
        errors.append("boundary unit does not match")
    if boundary.get("ownerToken") != manifest["ownerToken"]:
        errors.append("boundary owner token does not match")
    if boundary.get("binding") != manifest["binding"]:
        errors.append("boundary boot/namespace/runner binding does not match")
    if boundary.get("invocationId") != identity["invocationId"]:
        errors.append("boundary InvocationID does not match")
    if boundary.get("controlGroup") != identity["controlGroup"]:
        errors.append("boundary ControlGroup does not match")
    if boundary.get("ownershipDigest") != identity["ownershipDigest"]:
        errors.append("boundary ownership digest does not match")
    if boundary.get("procSelfCgroup") != identity["controlGroup"]:
        errors.append("supervisor self cgroup equality was not recorded")
    if boundary.get("mainPid") != boundary.get("supervisorPid"):
        errors.append("MainPID is not the supervisor process")
    if boundary.get("mainPidInCgroup") is not True:
        errors.append("MainPID cgroup membership was not recorded")
    if boundary.get("selfInCgroup") is not True:
        errors.append("supervisor cgroup membership was not recorded")
    if boundary.get("pidNamespace") != manifest["binding"]["pidNamespace"]:
        errors.append("boundary pid namespace does not match")
    if boundary.get("cgroupNamespace") != manifest["binding"]["cgroupNamespace"]:
        errors.append("boundary cgroup namespace does not match")
    if boundary.get("bootId") != manifest["binding"]["bootId"]:
        errors.append("boundary boot id does not match")
    mount = boundary.get("cgroup2Mount") or {}
    if mount.get("mountPoint") != "/sys/fs/cgroup" or mount.get("fstype") != "cgroup2":
        errors.append("cgroup2 mount identity is missing")
    unit_stat = boundary.get("unitCgroupStat") or {}
    if not unit_stat.get("exists") or not unit_stat.get("isDir"):
        errors.append("unit cgroup directory identity is missing")
    elif int(unit_stat.get("dev", -1)) != int(mount.get("dev", -2)):
        errors.append("unit cgroup directory is not on the recorded cgroup2 mount")
    if errors:
        raise RunnerError("; ".join(errors))
    return boundary


def create_boundary(run_dir: pathlib.Path, boundary: dict, *,
                    manifest: dict | None = None,
                    identity: dict | None = None) -> dict:
    path = run_dir / BOUNDARY_NAME
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    try:
        fd = os.open(path, flags, 0o600)
    except FileExistsError:
        if manifest is None or identity is None:
            raise RunnerError("existing boundary requires manifest/identity validation")
        existing = load_boundary(run_dir, manifest, identity)
        if existing != boundary:
            raise RunnerError("existing boundary differs from the new proof")
        return existing
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(boundary, handle, indent=2, sort_keys=False)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
    except Exception:
        with contextlib.suppress(OSError):
            path.unlink()
        raise
    return boundary


def load_boundary(run_dir: pathlib.Path, manifest: dict, identity: dict) -> dict:
    data = safe_json(run_dir / BOUNDARY_NAME)
    if data is None:
        raise RunnerError(f"supervisor boundary proof not found: {run_dir / BOUNDARY_NAME}")
    return validate_boundary(manifest, identity, data)


def cache_ancestry(manifest: dict) -> dict:
    cache = pathlib.Path(manifest.get("cacheDir") or "")
    root = pathlib.Path(manifest.get("cacheRoot") or "")
    if not manifest.get("cacheOwned"):
        return {"ok": True, "owned": False, "path": str(cache)}
    if not str(cache) or not str(root):
        return {"ok": False, "owned": True, "path": str(cache),
                "error": "owned cache path/root is missing"}
    try:
        resolved_cache = cache.resolve()
        resolved_root = root.resolve()
    except OSError as exc:
        return {"ok": False, "owned": True, "path": str(cache),
                "error": f"cache path resolution failed: {exc}"}
    if resolved_cache.parent != resolved_root:
        return {"ok": False, "owned": True, "path": str(cache),
                "error": "cache path is not a direct child of its immutable root"}
    expected_prefix = f"bom-local-test-{manifest['runId']}-cache-"
    if not resolved_cache.name.startswith(expected_prefix):
        return {"ok": False, "owned": True, "path": str(cache),
                "error": "cache path does not carry the run's generated prefix"}
    if cache.is_symlink():
        return {"ok": False, "owned": True, "path": str(cache),
                "error": "cache path is a symlink"}
    return {"ok": True, "owned": True, "path": str(resolved_cache),
            "root": str(resolved_root)}


def cleanup_lock(run_dir: pathlib.Path, name: str = ".cleanup.lock",
                 timeout: float = 5.0):
    """Kernel flock plus a boot/PID/namespace holder identity record.

    No PID liveness probe is performed.  A live holder is detected by flock;
    a stale file without a holder is safe to reuse.  The identity is evidence,
    never a signalling target.
    """
    @contextlib.contextmanager
    def _lock():
        path = run_dir / name
        flags = os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0)
        try:
            fd = os.open(path, flags, 0o600)
        except OSError:
            yield False
            return
        handle = os.fdopen(fd, "r+", encoding="utf-8")
        acquired = False
        deadline = time.monotonic() + timeout
        try:
            while time.monotonic() < deadline:
                try:
                    fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                    acquired = True
                    break
                except BlockingIOError:
                    time.sleep(0.1)
            if acquired:
                holder = {
                    "bootId": boot_id(),
                    "pid": os.getpid(),
                    "pidNamespace": pid_namespace_inode(),
                    "cgroupNamespace": namespace_inode("cgroup"),
                    "runnerHash": RUNNER_HASH,
                    "acquiredAt": iso(),
                }
                handle.seek(0)
                handle.truncate()
                json.dump(holder, handle, sort_keys=True)
                handle.write("\n")
                handle.flush()
                os.fsync(handle.fileno())
            yield acquired
        finally:
            if acquired:
                with contextlib.suppress(Exception):
                    fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
            handle.close()
    return _lock()


def remove_cache(manifest: dict) -> dict:
    cache = pathlib.Path(manifest.get("cacheDir") or "")
    if not manifest.get("cacheOwned") or manifest.get("keepCache"):
        return {"removed": False, "reason": "not owned or keep-cache requested",
                "path": str(cache)}
    ancestry = cache_ancestry(manifest)
    if not ancestry.get("ok"):
        return {"removed": False, "path": str(cache), "error": ancestry.get("error"),
                "ancestry": ancestry}
    try:
        if cache.is_symlink():
            return {"removed": False, "path": str(cache),
                    "error": "refusing to remove symlinked cache path"}
        if not cache.exists():
            return {"removed": False, "alreadyAbsent": True, "path": str(cache)}
        if not cache.is_dir():
            return {"removed": False, "path": str(cache), "error": "cache path is not a directory"}
        shutil.rmtree(cache)
        return {"removed": True, "path": str(cache), "existsAfter": cache.exists(),
                "ancestry": ancestry}
    except OSError as exc:
        return {"removed": False, "path": str(cache),
                "error": f"{type(exc).__name__}: {exc}", "ancestry": ancestry}


# ---------------------------------------------------------------------------
# Evidence schema, summary and state
# ---------------------------------------------------------------------------


def evidence_schema() -> dict:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "files": {
            OWNERSHIP_NAME: "exclusive, immutable run ownership and configuration manifest",
            UNIT_IDENTITY_NAME: "verified systemd InvocationID and canonical ControlGroup",
            BOUNDARY_NAME: "pre-command supervisor self-cgroup, MainPID, namespace and mount proof",
            SUMMARY_NAME: "command/test result plus provisional cleanup state",
            STATE_NAME: "read-only progress heartbeat",
            FINALIZATION_NAME: "post-stop cgroup/container/cache finalization",
            REFUSED_NAME: "present only when cleanup refused an unverified identity",
            EVENTS_NAME: "newline-delimited runner events",
        },
        "cleanupContract": {
            "unitStop": "systemctl --user stop after read-only exact-unit verification",
            "noPidSignals": True,
            "noAmbientCgroup": True,
            "noPrune": True,
            "failedInspectionIsEmpty": False,
        },
    }


def summary_base(manifest: dict, identity: dict | None = None) -> dict:
    unit = {
        "name": manifest["unit"],
        "invocationId": (identity or {}).get("invocationId"),
        "controlGroup": (identity or {}).get("controlGroup"),
        "ownershipDigest": (identity or {}).get("ownershipDigest"),
        "bootId": manifest["binding"]["bootId"],
        "pidNamespace": manifest["binding"]["pidNamespace"],
        "runnerHash": manifest["binding"]["runnerHash"],
        "transient": True,
        "runtimeMaxSeconds": manifest["runtimeMaxSeconds"],
        "timeoutStopSeconds": max(20, int(manifest["shutdownGraceSeconds"]) + 10),
    }
    return {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "runId": manifest["runId"],
        "mode": manifest["mode"],
        "createdAt": manifest["createdAt"],
        "startedAt": None,
        "finishedAt": None,
        "actualDurationSeconds": None,
        "status": "incomplete",
        "unit": unit,
        "lifetime": {
            "workloadSeconds": manifest["maxDurationSeconds"],
            "outerTimeoutSeconds": manifest["outerTimeoutSeconds"],
            "runtimeMaxSeconds": manifest["runtimeMaxSeconds"],
            "startupTimeoutSeconds": manifest["startupTimeoutSeconds"],
            "shutdownGraceSeconds": manifest["shutdownGraceSeconds"],
        },
        "resources": manifest["resources"],
        "network": manifest["network"],
        "cache": {"path": manifest["cacheDir"], "owned": manifest["cacheOwned"]},
        "commands": manifest["commands"],
        "readinessUrl": manifest["readinessUrl"],
        "container": manifest.get("container"),
        "exit": {
            "reason": "supervisor_error",
            "code": 5,
            "runnerCode": 5,
            "signal": None,
            "service": None,
            "test": None,
        },
        "cleanup": {
            "ok": False,
            "verifiedEmpty": False,
            "source": "pending",
            "inspection": {"ok": False, "errors": ["finalizer has not run"]},
            "errors": ["finalizer has not run"],
        },
        "checks": [],
    }


def write_state(run_dir: pathlib.Path, manifest: dict, phase: str, **extra: object) -> dict:
    state = {
        "schemaVersion": SCHEMA_VERSION,
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "phase": phase,
        "updatedAt": iso(),
        **extra,
    }
    atomic_write_json(run_dir / STATE_NAME, state)
    append_event(run_dir, "phase", phase=phase)
    return state


def summary_status(exit_info: dict, cleanup: dict) -> str:
    if cleanup.get("pending"):
        return "cleanup_pending"
    if not cleanup.get("ok"):
        return "cleanup_failed"
    reason = exit_info.get("reason")
    if reason == "deadline":
        return "timed_out"
    if reason == "interrupted":
        return "interrupted"
    if reason == "supervisor_lost":
        return "incomplete"
    if int(exit_info.get("code") or 0) == 0:
        return "passed"
    return "failed"


def classify_systemd_timeout(exit_info: dict, systemd_outcome: dict) -> dict:
    """RuntimeMaxSec/service timeout wins over an interrupt-like summary."""
    if systemd_outcome.get("SERVICE_RESULT") == "timeout":
        if exit_info.get("reason") in {None, "interrupted", "supervisor_lost"}:
            exit_info.update({"reason": "deadline", "code": 124,
                              "runnerCode": 124, "signal": None})
    return exit_info


def final_runner_code(summary: dict) -> int:
    if summary.get("status") == "cleanup_failed":
        return 4
    code = int((summary.get("exit") or {}).get("runnerCode") or 0)
    return code if 0 <= code <= 255 else 5


def effective_status(summary: dict, completion: dict | None,
                     finalization: dict | None) -> str:
    """External observer is authoritative for terminal run status."""
    if not completion or not completion.get("verified"):
        return str(summary.get("status") or "incomplete")
    cleanup = (finalization or {}).get("cleanup") or {}
    if not cleanup.get("destructiveOk"):
        return "cleanup_failed"
    exit_info = summary.get("exit") or {}
    reason = exit_info.get("reason")
    if reason == "deadline":
        return "timed_out"
    if reason == "interrupted":
        return "interrupted"
    if reason == "supervisor_lost":
        return "incomplete"
    code = exit_info.get("runnerCode")
    return "passed" if code == 0 else "failed"


# ---------------------------------------------------------------------------
# Supervisor payload (inside the dedicated unit)
# ---------------------------------------------------------------------------


def process_result(proc: subprocess.Popen) -> dict:
    code = proc.poll()
    if code is None:
        return {"code": None, "signal": None, "reason": "running"}
    if code < 0:
        number = -code
        try:
            name = signal_name(number)
        except ValueError:
            name = str(number)
        return {"code": 128 + number, "signal": name, "rawReturnCode": code,
                "reason": "signal"}
    return {"code": code, "signal": None, "rawReturnCode": code, "reason": "exit"}


def signal_name(number: int) -> str:
    import signal as signal_module
    return signal_module.Signals(number).name


def normalize_runner_code(exit_info: dict, stop_state: dict) -> dict:
    """Final normalization must preserve a classified signal/exit code."""
    reason = exit_info.get("reason")
    code = exit_info.get("code")
    if reason == "command_exit":
        runner_code = code if isinstance(code, int) and not isinstance(code, bool) else 0
    elif reason == "service_exit":
        runner_code = (code if isinstance(code, int)
                       and not isinstance(code, bool) and code != 0 else 5)
    elif reason == "deadline":
        runner_code = 124
    elif reason == "interrupted":
        if isinstance(code, int) and not isinstance(code, bool):
            runner_code = code
        else:
            signum = stop_state.get("signal")
            if signum is not None:
                runner_code = 128 + int(signum)
            else:
                runner_code = 128 + SIGNAL_NUMBERS.get(exit_info.get("signal"), 15)
    else:
        runner_code = exit_info.get("runnerCode")
        if not isinstance(runner_code, int) or isinstance(runner_code, bool):
            runner_code = code if isinstance(code, int) and not isinstance(code, bool) else 5
    exit_info["runnerCode"] = runner_code
    return exit_info


def start_process(role: str, argv: list[str], log_dir: pathlib.Path,
                  env: dict[str, str]) -> dict:
    log_dir.mkdir(parents=True, exist_ok=True)
    stdout_path = log_dir / f"{role}.stdout.log"
    stderr_path = log_dir / f"{role}.stderr.log"
    for path in (stdout_path, stderr_path):
        if path.is_symlink():
            raise RunnerError(f"refusing symlink log path: {path}")
    stdout = open(stdout_path, "ab", buffering=0)
    stderr = open(stderr_path, "ab", buffering=0)
    try:
        proc = subprocess.Popen(
            argv,
            stdin=subprocess.DEVNULL,
            stdout=stdout,
            stderr=stderr,
            env=env,
            cwd=str(ROOT),
            close_fds=True,
        )
    except OSError as exc:
        stdout.close()
        stderr.close()
        return {"ok": False, "role": role, "argv": argv,
                "error": f"{type(exc).__name__}: {exc}"}
    return {
        "ok": True,
        "role": role,
        "argv": argv,
        "proc": proc,
        "stdout": str(stdout_path),
        "stderr": str(stderr_path),
        "logHandles": (stdout, stderr),
        "startedAt": iso(),
    }


def close_process_logs(started: dict) -> None:
    for handle in started.get("logHandles") or ():
        with contextlib.suppress(Exception):
            handle.close()


SIGNAL_NUMBERS = {"SIGTERM": 15, "SIGINT": 2}


def external_stop_overrides(exit_info: dict, stop_state: dict) -> dict:
    """Explicit external stop wins over a racing child return."""
    signum = stop_state.get("signal")
    if signum is not None:
        exit_info.update({
            "reason": "interrupted",
            "code": 128 + int(signum),
            "runnerCode": 128 + int(signum),
            "signal": signal_name(int(signum)),
        })
    return exit_info


def classify_racing_signal_exit(exit_info: dict, stop_state: dict,
                                unit_metadata: dict | None) -> dict:
    """Distinguish an external stop from a command's own SIGTERM/SIGINT.

    A self-signalled command leaves the generated unit active while the
    supervisor is still handling its exit.  An external stop has already moved
    the exact unit to ``deactivating``.
    """
    if stop_state.get("signal") is not None:
        return external_stop_overrides(exit_info, stop_state)
    if (exit_info.get("reason") in {"command_exit", "service_exit"}
            and exit_info.get("signal") in SIGNAL_NUMBERS):
        metadata = unit_metadata or {}
        if metadata.get("ActiveState") == "deactivating":
            return external_stop_overrides(
                exit_info, {"signal": SIGNAL_NUMBERS[exit_info["signal"]]})
    return exit_info


def wait_process(proc: subprocess.Popen, deadline: float,
                 stop_state: dict) -> dict:
    while True:
        # Check the external stop first: systemd signals the whole control
        # group, so the child can return SIGTERM before this loop polls it.
        if stop_state.get("signal"):
            return {"kind": "interrupted", "reason": "interrupted",
                    "code": 128 + int(stop_state["signal"]),
                    "signal": signal_name(int(stop_state["signal"]))}
        result = process_result(proc)
        if result["reason"] != "running":
            result["kind"] = "exit"
            return result
        if time.monotonic() >= deadline:
            return {"kind": "deadline", "reason": "deadline", "code": 124, "signal": None}
        time.sleep(0.2)


def wait_test_with_service(test_proc: subprocess.Popen, service_proc: subprocess.Popen,
                           deadline: float, stop_state: dict) -> dict:
    while True:
        if stop_state.get("signal"):
            return {"kind": "interrupted", "reason": "interrupted",
                    "code": 128 + int(stop_state["signal"]),
                    "signal": signal_name(int(stop_state["signal"]))}
        test_result = process_result(test_proc)
        if test_result["reason"] != "running":
            test_result["kind"] = "exit"
            return test_result
        service_result = process_result(service_proc)
        if service_result["reason"] != "running":
            service_result["kind"] = "service_exit"
            return service_result
        if time.monotonic() >= deadline:
            return {"kind": "deadline", "reason": "deadline", "code": 124, "signal": None}
        time.sleep(0.2)


def probe_http(url: str) -> dict:
    request = urllib.request.Request(url, headers={"Cache-Control": "no-cache"})
    try:
        with urllib.request.urlopen(request, timeout=2) as response:
            body = response.read(65536)
            parsed = None
            with contextlib.suppress(Exception):
                parsed = json.loads(body.decode("utf-8", "replace"))
            return {"ok": response.status == 200, "status": response.status,
                    "body": parsed, "url": url, "checkedAt": iso()}
    except urllib.error.HTTPError as exc:
        return {"ok": False, "status": exc.code, "url": url, "checkedAt": iso()}
    except Exception as exc:
        return {"ok": False, "url": url, "error": f"{type(exc).__name__}: {exc}",
                "checkedAt": iso()}


def wait_native_ready(proc: subprocess.Popen, url: str | None, startup_deadline: float,
                      stop_state: dict) -> dict:
    if stop_state.get("signal"):
        return {"ok": False, "reason": "interrupted"}
    if not url:
        time.sleep(min(1.0, max(0.0, startup_deadline - time.monotonic())))
        result = process_result(proc)
        if result["reason"] != "running":
            return {"ok": False, "reason": "service_exit", "exit": result}
        return {"ok": True, "skipped": "no readiness URL configured"}
    last: dict = {}
    while time.monotonic() < startup_deadline:
        if stop_state.get("signal"):
            return {"ok": False, "reason": "interrupted"}
        result = process_result(proc)
        if result["reason"] != "running":
            return {"ok": False, "reason": "service_exit", "exit": result}
        last = probe_http(url)
        if last.get("ok"):
            return last
        time.sleep(0.5)
    return {"ok": False, "reason": "readiness_timeout", "url": url,
            "error": last.get("error") or f"HTTP {last.get('status')}"}


def container_start(manifest: dict, exec_fn=None) -> dict:
    exec_fn = exec_fn or exec_capture
    endpoint = docker_endpoint_matches(manifest, exec_fn=exec_fn)
    if not endpoint.get("ok"):
        return {"ok": False, "error": endpoint.get("error")}
    container = manifest.get("container") or {}
    try:
        image_id = resolve_image_id(str(container.get("image") or ""),
                                    exec_fn=exec_fn,
                                    env=docker_env_from_manifest(manifest))
    except PreflightError as exc:
        return {"ok": False, "error": str(exc)}
    if image_id != container.get("imageId"):
        return {"ok": False, "error": "image tag moved before container start",
                "expectedImageId": container.get("imageId"), "actualImageId": image_id}
    env = container_env(manifest)
    argv = compose_base(manifest) + ["up", "-d", "--no-build"]
    result = exec_fn(argv, timeout=max(120, manifest["startupTimeoutSeconds"] + 60),
                     env=env, cwd=ROOT)
    append_event(pathlib.Path(manifest["evidenceDir"]), "container_up",
                 ok=result.get("ok"), returncode=result.get("returncode"))
    if not result.get("ok"):
        return {"ok": False,
                "error": result.get("error") or (result.get("stderr") or "").strip()
                or "docker compose up failed",
                "result": result}
    inventory = verify_container_inventory(manifest, exec_fn=exec_fn)
    if not inventory["ok"]:
        return {"ok": False, "error": "container inventory verification failed",
                "inventory": inventory}
    if not inventory["containerIds"]:
        return {"ok": False, "error": "docker compose up created no verifiable container",
                "inventory": inventory}
    return {"ok": True, "inventory": inventory, "result": result}


def container_status(manifest: dict, exec_fn=None) -> dict:
    inventory = verify_container_inventory(manifest, exec_fn=exec_fn)
    if not inventory["ok"]:
        return {"ok": False, "error": "; ".join(inventory["errors"]),
                "inventory": inventory}
    return {"ok": True, "inventory": inventory,
            "empty": not inventory["containerIds"],
            "containers": inventory["containers"]}


def wait_container_ready(manifest: dict, startup_deadline: float,
                         stop_state: dict, exec_fn=None) -> dict:
    last: dict = {}
    while time.monotonic() < startup_deadline:
        if stop_state.get("signal"):
            return {"ok": False, "reason": "interrupted"}
        status = container_status(manifest, exec_fn=exec_fn)
        last = status
        if not status.get("ok"):
            time.sleep(1)
            continue
        containers = [item for item in status.get("containers") or []
                      if item.get("role") == "service"]
        if not containers:
            last = {"ok": False, "reason": "container_not_ready",
                    "error": "service container has not appeared"}
            time.sleep(1)
            continue
        if any(item.get("status") in {"exited", "dead"} for item in containers):
            return {"ok": False, "reason": "container_unhealthy", "status": status}
        if any(item.get("health") == "unhealthy" for item in containers):
            return {"ok": False, "reason": "container_unhealthy", "status": status}
        if all(item.get("status") == "running"
               and item.get("health") in {None, "healthy"} for item in containers):
            return {"ok": True, "status": status}
        time.sleep(1)
    last.setdefault("reason", "container_readiness_timeout")
    return {"ok": False, **last}


def wait_container_service(manifest: dict, deadline: float, stop_state: dict,
                           exec_fn=None) -> dict:
    last: dict = {"ok": False, "reason": "container_not_started"}
    while time.monotonic() < deadline:
        if stop_state.get("signal"):
            return {"kind": "interrupted", "reason": "interrupted",
                    "code": 128 + int(stop_state["signal"]),
                    "signal": signal_name(int(stop_state["signal"]))}
        status = container_status(manifest, exec_fn=exec_fn)
        last = status
        if not status.get("ok"):
            time.sleep(2)
            continue
        if status.get("empty"):
            return {"kind": "exit", "reason": "service_exit", "code": 5,
                    "detail": "no owned container is running"}
        for item in status.get("containers") or []:
            if item.get("status") in {"exited", "dead"}:
                code = item.get("exitCode")
                return {"kind": "exit", "reason": "service_exit",
                        "code": code if code else 5, "detail": status}
        time.sleep(2)
    return {"kind": "deadline", "reason": "deadline", "code": 124,
            "detail": last}


def base_env(manifest: dict, for_test: bool = False) -> dict[str, str]:
    env = os.environ.copy()
    env.update(manifest.get("testEnv") if for_test else manifest.get("serviceEnv") or {})
    for key in list(env):
        upper = key.upper()
        if (upper.startswith("BOM_TEST_") or upper in RESERVED_ENV_UPPER):
            env.pop(key, None)
    base_url = f"http://{manifest['network']['host']}:{manifest['network']['port']}"
    env.update({
        "PYTHONUNBUFFERED": "1",
        "BOM_TEST_RUN_ID": manifest["runId"],
        "BOM_TEST_OWNER_TOKEN": manifest["ownerToken"],
        "BOM_TEST_PORT": str(manifest["network"]["port"]),
        "BOM_TEST_BASE_URL": base_url,
        "BOM_TEST_URL": base_url,
        "BOM_TEST_CACHE_DIR": manifest["cacheDir"],
        "BOM_BASE_URL": base_url,
    })
    if manifest["mode"] == "native":
        # Forced after all caller overrides, including case aliases.
        env["ASPNETCORE_URLS"] = base_url
        env["CACHEDIRECTORY"] = manifest["cacheDir"]
    return env


def cmd_supervise(run_id: str) -> int:
    run_dir = resolve_run_dir(run_id)
    manifest = load_manifest(run_dir)
    if os.environ.get("BOM_TEST_OWNER_TOKEN") != manifest["ownerToken"]:
        summary = summary_base(manifest)
        summary["startedAt"] = iso()
        summary["finishedAt"] = iso()
        summary["actualDurationSeconds"] = 0.0
        summary["status"] = "failed"
        summary["exit"].update({"reason": "startup_failed", "code": 5, "runnerCode": 5,
                                "error": "unit environment token does not match manifest"})
        atomic_write_json(run_dir / SUMMARY_NAME, summary)
        return 5
    expected_env = {
        "BOM_TEST_UNIT": manifest["unit"],
        "BOM_TEST_BOOT_ID": str(manifest["binding"]["bootId"]),
        "BOM_TEST_PID_NAMESPACE": str(manifest["binding"]["pidNamespace"]),
        "BOM_TEST_CGROUP_NAMESPACE": str(manifest["binding"]["cgroupNamespace"]),
        "BOM_TEST_RUNNER_HASH": str(manifest["binding"]["runnerHash"]),
    }
    for key, expected in expected_env.items():
        if os.environ.get(key) != expected:
            summary = summary_base(manifest)
            summary["startedAt"] = iso()
            summary["finishedAt"] = iso()
            summary["actualDurationSeconds"] = 0.0
            summary["status"] = "failed"
            summary["exit"].update({"reason": "startup_failed", "code": 5, "runnerCode": 5,
                                    "error": f"unit environment {key} does not match manifest"})
            atomic_write_json(run_dir / SUMMARY_NAME, summary)
            return 5

    try:
        identity = record_supervisor_identity(run_dir, manifest)
    except RunnerError as exc:
        summary = summary_base(manifest)
        summary["startedAt"] = iso()
        summary["finishedAt"] = iso()
        summary["actualDurationSeconds"] = 0.0
        summary["status"] = "failed"
        summary["exit"].update({"reason": "startup_failed", "code": 5, "runnerCode": 5,
                                "error": f"supervisor identity was not persisted: {exc}"})
        atomic_write_json(run_dir / SUMMARY_NAME, summary)
        return 5
    verification = verify_live_unit(manifest, identity)
    if not verification["ok"]:
        summary = summary_base(manifest, identity)
        summary["startedAt"] = iso()
        summary["finishedAt"] = iso()
        summary["actualDurationSeconds"] = 0.0
        summary["status"] = "failed"
        summary["exit"].update({"reason": "startup_failed", "code": 5, "runnerCode": 5,
                                "error": "live unit identity verification failed",
                                "detail": verification})
        atomic_write_json(run_dir / SUMMARY_NAME, summary)
        return 5

    # The boundary proof is written before any service/test command is spawned.
    try:
        boundary = make_boundary(manifest, identity, verification.get("metadata") or {})
        create_boundary(run_dir, boundary, manifest=manifest, identity=identity)
    except RunnerError as exc:
        summary = summary_base(manifest, identity)
        summary["startedAt"] = iso()
        summary["finishedAt"] = iso()
        summary["actualDurationSeconds"] = 0.0
        summary["status"] = "failed"
        summary["exit"].update({"reason": "startup_failed", "code": 5, "runnerCode": 5,
                                "error": f"boundary proof failed: {exc}"})
        atomic_write_json(run_dir / SUMMARY_NAME, summary)
        return 5

    summary = summary_base(manifest, identity)
    summary["startedAt"] = iso()
    started_monotonic = time.monotonic()
    outer_deadline = started_monotonic + float(manifest["outerTimeoutSeconds"])
    startup_deadline = min(outer_deadline,
                           started_monotonic + float(manifest["startupTimeoutSeconds"]))
    workload_deadline = outer_deadline
    stop_state: dict = {"signal": None}

    def request_stop(signum, _frame):
        if stop_state["signal"] is None:
            stop_state["signal"] = signum
            append_event(run_dir, "stop_requested", signal=signal_name(signum))

    import signal as signal_module
    signal_module.signal(signal_module.SIGTERM, request_stop)
    signal_module.signal(signal_module.SIGINT, request_stop)
    signal_module.signal(signal_module.SIGHUP, signal_module.SIG_IGN)

    log_dir = run_dir / "logs"
    state = write_state(run_dir, manifest, "starting", startedAt=summary["startedAt"])
    service: dict | None = None
    test: dict | None = None
    exit_info = summary["exit"]

    def set_phase(phase: str, **extra: object) -> None:
        nonlocal state
        state = write_state(run_dir, manifest, phase, **extra)

    try:
        set_phase("starting_service")
        if manifest["mode"] in {"command", "native"}:
            argv = (manifest["commands"].get("service")
                    if manifest["mode"] == "native" else manifest["commands"].get("service"))
            if not argv:
                raise RunnerError("no command was provided")
            service = start_process("service" if manifest["mode"] == "native" else "command",
                                    argv, log_dir, base_env(manifest))
            if not service.get("ok"):
                exit_info.update({"reason": "spawn_failed", "code": 5, "runnerCode": 5,
                                  "error": service.get("error")})
                set_phase("spawn_failed", service=service.get("error"))
            else:
                set_phase("running_service", servicePid=service["proc"].pid)
                if manifest["mode"] == "command":
                    workload_deadline = min(
                        outer_deadline,
                        time.monotonic() + float(manifest["maxDurationSeconds"]))
                    result = wait_process(service["proc"], workload_deadline, stop_state)
                    exit_info["service"] = process_result(service["proc"])
                    exit_info.update({
                        "reason": "command_exit",
                        "code": result.get("code") if result.get("kind") == "exit" else result.get("code"),
                        "signal": result.get("signal"),
                    })
                    if result.get("kind") == "deadline":
                        exit_info.update({"reason": "deadline", "code": 124, "runnerCode": 124,
                                          "signal": None})
                    elif result.get("kind") == "interrupted":
                        exit_info.update({"reason": "interrupted",
                                          "code": 128 + int(stop_state["signal"]),
                                          "runnerCode": 128 + int(stop_state["signal"]),
                                          "signal": signal_name(int(stop_state["signal"]))})
                else:
                    ready = wait_native_ready(service["proc"], manifest.get("readinessUrl"),
                                              startup_deadline, stop_state)
                    set_phase("readiness", readiness=ready)
                    if not ready.get("ok"):
                        if ready.get("reason") == "service_exit":
                            exit_info.update({"reason": "service_exit", "code": 5,
                                              "runnerCode": 5,
                                              "service": ready.get("exit")})
                        elif ready.get("reason") == "interrupted":
                            exit_info.update({"reason": "interrupted",
                                              "code": 128 + int(stop_state["signal"]),
                                              "runnerCode": 128 + int(stop_state["signal"]),
                                              "signal": signal_name(int(stop_state["signal"]))})
                        else:
                            exit_info.update({"reason": "readiness_timeout", "code": 5,
                                              "runnerCode": 5, "detail": ready})
                    else:
                        workload_deadline = min(
                            outer_deadline,
                            time.monotonic() + float(manifest["maxDurationSeconds"]))
                        test_argv = manifest["commands"].get("test")
                        if test_argv:
                            test = start_process("test", test_argv, log_dir,
                                                 base_env(manifest, for_test=True))
                            if not test.get("ok"):
                                exit_info.update({"reason": "spawn_failed", "code": 5,
                                                  "runnerCode": 5,
                                                  "error": test.get("error")})
                            else:
                                set_phase("running_test", testPid=test["proc"].pid)
                                result = wait_test_with_service(
                                    test["proc"], service["proc"], workload_deadline,
                                    stop_state)
                                exit_info["test"] = process_result(test["proc"])
                                exit_info["service"] = process_result(service["proc"])
                                if result.get("kind") == "exit":
                                    exit_info.update({"reason": "command_exit",
                                                      "code": result.get("code"),
                                                      "signal": result.get("signal")})
                                elif result.get("kind") == "service_exit":
                                    exit_info.update({"reason": "service_exit", "code": 5,
                                                      "service": result})
                                elif result.get("kind") == "deadline":
                                    exit_info.update({"reason": "deadline", "code": 124,
                                                      "runnerCode": 124})
                                else:
                                    exit_info.update({
                                        "reason": "interrupted",
                                        "code": 128 + int(stop_state["signal"]),
                                        "runnerCode": 128 + int(stop_state["signal"]),
                                        "signal": signal_name(int(stop_state["signal"])),
                                    })
                        else:
                            result = wait_process(service["proc"], workload_deadline,
                                                  stop_state)
                            exit_info["service"] = process_result(service["proc"])
                            if result.get("kind") == "exit":
                                exit_info.update({
                                    "reason": "service_exit",
                                    "code": result.get("code") if result.get("code") else 5,
                                    "signal": result.get("signal"),
                                })
                            elif result.get("kind") == "deadline":
                                exit_info.update({"reason": "deadline", "code": 124,
                                                  "runnerCode": 124})
                            else:
                                exit_info.update({
                                    "reason": "interrupted",
                                    "code": 128 + int(stop_state["signal"]),
                                    "runnerCode": 128 + int(stop_state["signal"]),
                                    "signal": signal_name(int(stop_state["signal"])),
                                })

        elif manifest["mode"] == "container":
            up = container_start(manifest)
            set_phase("container_started", ok=up.get("ok"))
            if not up.get("ok"):
                exit_info.update({"reason": "container_failed", "code": 5, "runnerCode": 5,
                                  "error": up.get("error"), "detail": up})
            else:
                ready = wait_container_ready(manifest, startup_deadline, stop_state)
                set_phase("readiness", readiness=ready)
                if not ready.get("ok"):
                    reason = ("interrupted" if ready.get("reason") == "interrupted"
                              else "container_failed")
                    exit_info.update({"reason": reason,
                                      "code": 128 + int(stop_state["signal"])
                                      if reason == "interrupted" else 5,
                                      "runnerCode": 128 + int(stop_state["signal"])
                                      if reason == "interrupted" else 5,
                                      "signal": signal_name(int(stop_state["signal"]))
                                      if reason == "interrupted" else None,
                                      "detail": ready})
                else:
                    workload_deadline = min(
                        outer_deadline,
                        time.monotonic() + float(manifest["maxDurationSeconds"]))
                    test_argv = manifest["commands"].get("test")
                    if test_argv:
                        test = start_process("test", test_argv, log_dir,
                                             base_env(manifest, for_test=True))
                        if not test.get("ok"):
                            exit_info.update({"reason": "spawn_failed", "code": 5,
                                              "runnerCode": 5, "error": test.get("error")})
                        else:
                            set_phase("running_test", testPid=test["proc"].pid)
                            result = wait_process(test["proc"], workload_deadline,
                                                  stop_state)
                            exit_info["test"] = process_result(test["proc"])
                            if result.get("kind") == "exit":
                                exit_info.update({"reason": "command_exit",
                                                  "code": result.get("code"),
                                                  "signal": result.get("signal")})
                            elif result.get("kind") == "deadline":
                                exit_info.update({"reason": "deadline", "code": 124,
                                                  "runnerCode": 124})
                            else:
                                exit_info.update({
                                    "reason": "interrupted",
                                    "code": 128 + int(stop_state["signal"]),
                                    "runnerCode": 128 + int(stop_state["signal"]),
                                    "signal": signal_name(int(stop_state["signal"])),
                                })
                    else:
                        result = wait_container_service(manifest, workload_deadline,
                                                        stop_state)
                        if result.get("kind") == "exit":
                            exit_info.update({"reason": "service_exit",
                                              "code": result.get("code") or 5,
                                              "detail": result.get("detail")})
                        elif result.get("kind") == "deadline":
                            exit_info.update({"reason": "deadline", "code": 124,
                                              "runnerCode": 124})
                        else:
                            exit_info.update({"reason": "interrupted",
                                              "code": 128 + int(stop_state["signal"]),
                                              "runnerCode": 128 + int(stop_state["signal"]),
                                              "signal": signal_name(int(stop_state["signal"]))})
        # A systemd stop signals the supervisor and its children as one
        # control group; the child may return SIGTERM before the branch above
        # observes stop_state.  External stop is authoritative, but a command's
        # own SIGTERM must stay command_exit when the unit is still active.
        if stop_state.get("signal") is not None:
            external_stop_overrides(exit_info, stop_state)
        elif (exit_info.get("reason") in {"command_exit", "service_exit"}
              and exit_info.get("signal") in SIGNAL_NUMBERS):
            classify_racing_signal_exit(
                exit_info, stop_state,
                systemd_show(manifest["unit"]))
    except Exception as exc:
        exit_info.update({"reason": "supervisor_error", "code": 5, "runnerCode": 5,
                          "error": f"{type(exc).__name__}: {exc}"})
        append_event(run_dir, "supervisor_error", error=exit_info["error"])
    finally:
        for started in (service, test):
            if started:
                close_process_logs(started)

    normalize_runner_code(exit_info, stop_state)

    finished = utcnow()
    summary["exit"] = exit_info
    summary["finishedAt"] = iso(finished)
    summary["actualDurationSeconds"] = round(time.monotonic() - started_monotonic, 3)
    summary["status"] = "cleanup_pending"
    summary["checks"] = [
        {"name": "unitIdentityVerified", "ok": bool(verification["ok"])},
        {"name": "dedicatedUnit", "ok": True},
    ]
    atomic_write_json(run_dir / SUMMARY_NAME, summary)
    set_phase("finished", status=summary["status"],
              runnerCode=exit_info["runnerCode"])
    print(json.dumps({"runId": manifest["runId"], "status": summary["status"],
                      "reason": exit_info.get("reason"),
                      "code": exit_info.get("code"),
                      "cleanup": "pending-exec-stop-post"}))
    return final_runner_code(summary)


# ---------------------------------------------------------------------------
# Post-stop finalization
# ---------------------------------------------------------------------------


def reusable_finalization(manifest: dict, identity: dict, existing: dict | None) -> bool:
    if not isinstance(existing, dict):
        return False
    if existing.get("schemaVersion") != SCHEMA_VERSION:
        return False
    if existing.get("runnerVersion") != RUNNER_VERSION:
        return False
    if existing.get("runId") != manifest["runId"]:
        return False
    if existing.get("unit") != manifest["unit"]:
        return False
    if existing.get("invocationId") != identity["invocationId"]:
        return False
    if existing.get("controlGroup") != identity["controlGroup"]:
        return False
    provisional = existing.get("provisional") or {}
    if provisional.get("binding") != manifest["binding"]:
        return False
    if provisional.get("invocationId") != identity["invocationId"]:
        return False
    cleanup = existing.get("cleanup") or {}
    terminal = (provisional.get("terminalEvidence") or {}).get("ok")
    return bool(cleanup.get("destructiveOk") and terminal)


def finalize_run(run_dir: pathlib.Path, source: str, *,
                 verification: dict | None = None,
                 exec_fn=None, cgroup_root: pathlib.Path | None = None,
                 cgroup_timeout: float | None = None) -> dict:
    """Serialize destructive finalization, then write its evidence."""
    manifest = load_manifest(run_dir, require_binding=True)
    identity = load_unit_identity(run_dir, manifest)
    existing = safe_json(run_dir / FINALIZATION_NAME)
    if reusable_finalization(manifest, identity, existing):
        return existing
    with cleanup_lock(run_dir, ".finalize.lock") as acquired:
        if not acquired:
            return {
                "schemaVersion": SCHEMA_VERSION,
                "runnerVersion": RUNNER_VERSION,
                "runId": manifest["runId"],
                "unit": manifest["unit"],
                "invocationId": identity["invocationId"],
                "controlGroup": identity["controlGroup"],
                "source": source,
                "cleanup": {
                    "ok": False, "pending": False, "destructiveOk": False,
                    "verifiedEmpty": False,
                    "source": source,
                    "inspection": {"ok": False,
                                   "errors": ["another finalizer holds the evidence lock"]},
                    "errors": ["another finalizer holds the evidence lock"],
                },
            }
        existing = safe_json(run_dir / FINALIZATION_NAME)
        if reusable_finalization(manifest, identity, existing):
            return existing
        return _finalize_run_locked(
            run_dir, source, verification=verification, exec_fn=exec_fn,
            cgroup_root=cgroup_root, cgroup_timeout=cgroup_timeout)


def _finalize_run_locked(run_dir: pathlib.Path, source: str, *,
                         verification: dict | None = None,
                         exec_fn=None, cgroup_root: pathlib.Path | None = None,
                         cgroup_timeout: float | None = None) -> dict:
    started = utcnow()
    manifest = load_manifest(run_dir, require_binding=True)
    identity = load_unit_identity(run_dir, manifest)
    boundary = load_boundary(run_dir, manifest, identity)
    existing = safe_json(run_dir / FINALIZATION_NAME)
    if reusable_finalization(manifest, identity, existing):
        return existing
    if verification is None:
        verification = verify_live_unit(manifest, identity, exec_fn=exec_fn)
    errors: list[str] = []
    provisional: dict = {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "invocationId": identity["invocationId"],
        "controlGroup": identity["controlGroup"],
        "ownerToken": manifest["ownerToken"],
        "binding": manifest["binding"],
        "ownershipDigest": identity["ownershipDigest"],
        "boundaryDigest": hashlib.sha256(
            json.dumps(boundary, separators=(",", ":"), sort_keys=True).encode("utf-8")
        ).hexdigest(),
        "finalizerPid": os.getpid(),
        "recordedAt": iso(),
        "systemdOutcome": {
            "SERVICE_RESULT": os.environ.get("SERVICE_RESULT"),
            "EXIT_CODE": os.environ.get("EXIT_CODE"),
            "EXIT_STATUS": os.environ.get("EXIT_STATUS"),
        },
        "terminalEvidence": None,
        "cgroupObservation": None,
    }
    if not verification.get("ok") or not boundary:
        errors = list(verification.get("errors") or ["unit identity verification failed"])
        cleanup = {
            "ok": False,
            "pending": False,
            "destructiveOk": False,
            "verifiedEmpty": False,
            "provisionalVerifiedEmpty": False,
            "source": source,
            "inspection": {"ok": False, "errors": errors},
            "errors": errors,
            "unitVerification": verification,
        }
    else:
        terminal_metadata = systemd_show(manifest["unit"], exec_fn=exec_fn)
        terminal_verification = verify_unit_metadata(
            manifest["unit"], manifest["ownerToken"], identity["invocationId"],
            terminal_metadata, binding=manifest["binding"])
        terminal_state = terminal_metadata.get("ActiveState")
        second_terminal_ok = bool(
            terminal_verification.get("ok")
            and terminal_metadata.get("ControlGroup") == identity["controlGroup"]
            and terminal_state in {"deactivating", "inactive", "failed"})
        # The first finalizer inspection is also exact evidence.  If systemd
        # has already collected the unit by the second inspection, keep the
        # earlier terminal observation rather than claiming a pass from a
        # not-found unit.
        first_terminal_ok = bool(
            verification.get("ok")
            and verification.get("activeState") in {"deactivating", "inactive", "failed"}
            and verification.get("controlGroup") == identity["controlGroup"])
        pre_stop_verified = bool(
            verification.get("ok")
            and verification.get("controlGroup") == identity["controlGroup"])
        post_stop_unloaded = bool(
            terminal_metadata.get("_ok") is True
            and terminal_metadata.get("LoadState") == "not-found")
        settled_unloaded_ok = bool(pre_stop_verified and post_stop_unloaded)
        terminal_ok = bool(second_terminal_ok or first_terminal_ok
                           or settled_unloaded_ok)
        provisional["terminalEvidence"] = {
            "ok": terminal_ok,
            "source": ("second-inspection" if second_terminal_ok else
                       "first-inspection" if first_terminal_ok else
                       "pre-stop-verified-post-stop-unloaded"
                       if settled_unloaded_ok else "none"),
            "activeState": terminal_state if second_terminal_ok else verification.get("activeState"),
            "loadState": terminal_metadata.get("LoadState"),
            "invocationId": (terminal_metadata.get("InvocationID")
                             if second_terminal_ok else identity["invocationId"]),
            "controlGroup": (terminal_metadata.get("ControlGroup")
                             if second_terminal_ok else identity["controlGroup"]),
            "preStopVerified": pre_stop_verified,
            "postStopUnloaded": post_stop_unloaded,
            "result": terminal_metadata.get("Result"),
            "verified": terminal_verification,
            "observedAt": iso(),
        }

        # Destructive container cleanup only after exact unit identity and a
        # terminal lifecycle observation.  Any uncertainty leaves containers
        # untouched; no compose down, prune, name wildcard or PID sweep exists.
        container = None
        destructive_ok = True
        if manifest["mode"] == "container":
            if terminal_ok:
                container = container_cleanup(manifest, exec_fn=exec_fn, source=source)
                destructive_ok = bool(container.get("ok"))
            else:
                container = {"ok": False, "skipped": "terminal unit state unverified",
                             "verifiedEmpty": False, "errors": []}
                destructive_ok = False
        if container and container.get("errors"):
            errors.extend(container["errors"])

        timeout = (manifest["shutdownGraceSeconds"] + 5
                   if cgroup_timeout is None else cgroup_timeout)
        if destructive_ok:
            cgroup = wait_cgroup_empty(identity["controlGroup"], timeout,
                                       cgroup_root=cgroup_root)
        else:
            cgroup = {"ok": False, "exists": None, "empty": False, "verified": False,
                      "remainingPids": [], "skipped": "destructive cleanup not verified"}
        enoent = {"ok": False, "error": None}
        if cgroup.get("ok") and not cgroup.get("exists"):
            enoent = enoent_boundary_stable(identity["controlGroup"], boundary,
                                            cgroup_root=cgroup_root)
        provisional["cgroupObservation"] = cgroup
        provisional["cgroupEnoentProof"] = enoent
        if not cgroup.get("ok"):
            errors.append(cgroup.get("error") or "unit cgroup inspection failed")
        elif not cgroup.get("exists") and not enoent.get("ok"):
            errors.append("cgroup path vanished without the settled boundary proof")
        elif cgroup.get("exists") and not cgroup.get("empty"):
            errors.append("unit-owned cgroup has live non-finalizer processes")

        # ExecStopPost itself is inside the boundary, so this observation is
        # provisional.  A later read-only observer must see cgroup.events
        # populated=0 (or the settled exact-ENOENT proof) after this process
        # exits.  Never claim final verifiedEmpty here.
        cgroup_boundary_ok = bool(
            cgroup.get("ok")
            and ((cgroup.get("exists") and cgroup.get("empty"))
                 or (not cgroup.get("exists") and enoent.get("ok"))))
        provisional_ok = bool(
            terminal_ok and destructive_ok and cgroup_boundary_ok
            and not [e for e in errors if "container" in e.lower()])
        cache: dict
        if provisional_ok:
            cache = remove_cache(manifest)
            if cache.get("error"):
                errors.append(cache["error"])
        else:
            cache = {"removed": False, "skipped": "boundary unverified; no destructive cache action",
                     "path": manifest.get("cacheDir")}
        destructive_ok = bool(destructive_ok and not cache.get("error"))
        cleanup = {
            "ok": bool(provisional_ok and destructive_ok and not cache.get("error")),
            "pending": True,
            "destructiveOk": destructive_ok,
            "verifiedEmpty": False,
            "provisionalVerifiedEmpty": provisional_ok,
            "source": source,
            "inspection": {
                "ok": bool(cgroup.get("ok") and terminal_ok),
                "errors": errors,
                "container": container,
            },
            "unitVerification": verification,
            "cgroup": cgroup,
            "container": container,
            "cache": cache,
            "errors": errors,
        }
    cleanup["attemptedAt"] = iso(started)
    cleanup["finishedAt"] = iso()
    result = {
        "schemaVersion": SCHEMA_VERSION,
        "runnerVersion": RUNNER_VERSION,
        "runId": manifest["runId"],
        "unit": identity["unit"],
        "invocationId": identity["invocationId"],
        "controlGroup": identity["controlGroup"],
        "controlGroupStat": boundary.get("unitCgroupStat"),
        "source": source,
        "startedAt": iso(started),
        "finishedAt": cleanup["finishedAt"],
        "provisional": provisional,
        "cleanup": cleanup,
    }
    atomic_write_json(run_dir / FINALIZATION_NAME, result)
    append_event(run_dir, "finalized", source=source, ok=cleanup["ok"],
                 provisional=cleanup.get("provisionalVerifiedEmpty"),
                 verifiedEmpty=False)

    summary = safe_json(run_dir / SUMMARY_NAME)
    if summary is None:
        summary = summary_base(manifest, identity)
        summary["startedAt"] = manifest["createdAt"]
        summary["finishedAt"] = iso()
        summary["status"] = "incomplete"
        summary["exit"] = {
            "reason": "supervisor_lost",
            "code": 5,
            "runnerCode": 5,
            "signal": None,
            "service": None,
            "test": None,
            "detail": "summary was not written before finalization; systemd outcome: "
                      + json.dumps(provisional["systemdOutcome"]),
        }
    summary["exit"] = classify_systemd_timeout(
        dict(summary.get("exit") or {}), provisional["systemdOutcome"])
    summary["cleanup"] = cleanup
    summary["checks"] = list(summary.get("checks") or [])
    summary["checks"].extend([
        {"name": "finalizerVerifiedUnit", "ok": bool(verification.get("ok"))},
        {"name": "provisionalBoundary", "ok": bool(cleanup.get("provisionalVerifiedEmpty"))},
        {"name": "cleanupFinalObservation", "ok": False},
    ])
    if not cleanup["ok"]:
        summary["status"] = "cleanup_failed"
    elif summary.get("status") in {"incomplete", "cleanup_failed", "cleanup_pending"}:
        summary["status"] = "cleanup_pending"
    if not summary.get("finishedAt"):
        summary["finishedAt"] = iso()
    atomic_write_json(run_dir / SUMMARY_NAME, summary)
    return result


def observe_completion(run_dir: pathlib.Path, cgroup_root: pathlib.Path | None = None) -> dict:
    """Read-only terminal/empty observation after the finalizer has exited."""
    manifest = load_manifest(run_dir)
    try:
        assert_current_binding(manifest)
    except RunnerError as exc:
        return {"ok": False, "verified": False, "proofBasis": "binding-mismatch",
                "noLiveOwnedProcesses": False, "error": str(exc)}
    identity = load_unit_identity(run_dir, manifest)
    boundary = load_boundary(run_dir, manifest, identity)
    finalization = safe_json(run_dir / FINALIZATION_NAME)
    if not finalization:
        return {"ok": False, "verified": False, "proofBasis": "finalizer-evidence-missing",
                "noLiveOwnedProcesses": False}
    provisional = finalization.get("provisional") or {}
    if (provisional.get("binding") != manifest["binding"]
            or provisional.get("invocationId") != identity["invocationId"]
            or provisional.get("controlGroup") != identity["controlGroup"]):
        return {"ok": False, "verified": False, "proofBasis": "finalizer-identity-mismatch",
                "noLiveOwnedProcesses": False}
    cleanup = finalization.get("cleanup") or {}
    if not cleanup.get("destructiveOk"):
        return {"ok": False, "verified": False, "proofBasis": "destructive-cleanup-not-ok",
                "noLiveOwnedProcesses": False}

    try:
        mount_now = cgroup2_mount_identity()
    except RunnerError as exc:
        return {"ok": False, "verified": False, "proofBasis": "cgroup2-mount-unknown",
                "noLiveOwnedProcesses": False, "error": str(exc)}
    if mount_now != boundary.get("cgroup2Mount"):
        return {"ok": False, "verified": False, "proofBasis": "cgroup2-mount-changed",
                "noLiveOwnedProcesses": False}
    try:
        if (namespace_inode("pid") != manifest["binding"]["pidNamespace"]
                or namespace_inode("cgroup") != manifest["binding"]["cgroupNamespace"]):
            return {"ok": False, "verified": False, "proofBasis": "namespace-mismatch",
                    "noLiveOwnedProcesses": False}
    except RunnerError as exc:
        return {"ok": False, "verified": False, "proofBasis": "namespace-unknown",
                "noLiveOwnedProcesses": False, "error": str(exc)}

    metadata = systemd_show(manifest["unit"])
    terminal_evidence = provisional.get("terminalEvidence") or {}
    known_active = bool(
        metadata.get("_ok") is True
        and metadata.get("LoadState") == "loaded"
        and metadata.get("ActiveState") in {"active", "activating", "deactivating"})
    terminal_now = bool(
        metadata.get("_ok") is True
        and metadata.get("LoadState") == "loaded"
        and metadata.get("InvocationID") == identity["invocationId"]
        and metadata.get("ControlGroup") == identity["controlGroup"]
        and metadata.get("ActiveState") in {"inactive", "failed"})
    terminal = bool(terminal_now or (not known_active and terminal_evidence.get("ok")))
    if not terminal:
        return {"ok": False, "verified": False, "proofBasis": "unit-not-terminal",
                "noLiveOwnedProcesses": False, "unitState": metadata,
                "terminalEvidence": terminal_evidence}

    path = (cgroup_root or CGROUP_ROOT) / identity["controlGroup"].lstrip("/")
    current = path_stat_identity(path)
    if current.get("exists") is True:
        # The observer is outside the unit boundary; do not exclude any PID.
        observation = read_cgroup_observation(identity["controlGroup"],
                                              self_pid=-1,
                                              cgroup_root=cgroup_root)
        verified = bool(observation.get("ok") and observation.get("exists")
                        and observation.get("empty") and observation.get("allPopulatedZero"))
        return {
            "ok": verified,
            "verified": verified,
            "proofBasis": ("cgroup.events.populated=0" if verified
                           else "cgroup-positive-observation-failed"),
            "noLiveOwnedProcesses": verified,
            "observedAt": observation.get("observedAt"),
            "cgroup": observation,
            "terminal": terminal_now,
            "terminalEvidence": terminal_evidence,
        }

    if current.get("exists") is not False:
        return {"ok": False, "verified": False, "proofBasis": "cgroup-stat-unknown",
                "noLiveOwnedProcesses": False, "error": current.get("error")}

    # Exact directory ENOENT is only usable with the same cgroup2 mount, a
    # stable readable/listable parent, and the supervisor's original directory
    # identity.  Permission, I/O, namespace or mount mismatch remains unknown.
    parent_now = parent_identity(path)
    original_unit = boundary.get("unitCgroupStat") or {}
    original_parent = boundary.get("parentStat") or {}
    prior = provisional.get("cgroupObservation") or {}
    stable = bool(
        original_unit.get("exists") is True
        and int(original_unit.get("dev", -1)) == int(mount_now.get("dev", -2))
        and parent_now.get("exists") is True
        and parent_now.get("isDir") is True
        and parent_now.get("listable") is True
        and parent_now.get("dev") == original_parent.get("dev")
        and parent_now.get("ino") == original_parent.get("ino"))
    return {
        "ok": stable,
        "verified": stable,
        "proofBasis": ("exact-directory-enoent-with-stable-mount-and-parent"
                       if stable else "enoent-without-stable-mount-or-parent"),
        "noLiveOwnedProcesses": stable,
        "path": current,
        "parent": parent_now,
        "priorProvisional": prior,
        "terminal": terminal_now,
        "terminalEvidence": terminal_evidence,
        "mount": mount_now,
    }


def finalize_env_allowed(manifest: dict) -> bool:
    return os.environ.get("BOM_TEST_OWNER_TOKEN") == manifest.get("ownerToken")


def cmd_finalize(run_id: str, source: str) -> int:
    try:
        run_dir = resolve_run_dir(run_id)
        manifest = load_manifest(run_dir, require_binding=True)
        if not finalize_env_allowed(manifest):
            raise RunnerError(
                "finalize requires the unit environment token; explicit cleanup "
                "must use the verified cleanup command")
        result = finalize_run(run_dir, source)
        print(json.dumps(result, indent=2))
        return 0 if result["cleanup"].get("ok") else 4
    except Exception as exc:
        print(json.dumps({"ok": False, "error": f"{type(exc).__name__}: {exc}"}),
              file=sys.stderr)
        return 4


def plan_cleanup(manifest: dict, identity: dict, boundary: dict,
                 unit_metadata: dict) -> dict:
    """Pure, pre-mutation cleanup plan.  Any uncertainty returns refuse."""
    errors: list[str] = []
    try:
        assert_current_binding(manifest)
    except RunnerError as exc:
        errors.append(str(exc))
    try:
        validate_unit_identity(manifest, identity)
    except RunnerError as exc:
        errors.append(str(exc))
    try:
        validate_boundary(manifest, identity, boundary)
    except RunnerError as exc:
        errors.append(str(exc))
    verification = verify_unit_metadata(
        manifest["unit"], manifest["ownerToken"], identity["invocationId"],
        unit_metadata, binding=manifest.get("binding"))
    if not verification["ok"]:
        errors.extend(verification["errors"])
    if unit_metadata.get("ControlGroup") != identity.get("controlGroup"):
        errors.append("live ControlGroup does not match the recorded identity")
    if errors:
        return {"action": "refuse", "verification": verification, "errors": errors}
    active = verification.get("activeState")
    if active in {"active", "activating", "deactivating"}:
        return {"action": "stop_unit", "verification": verification, "errors": []}
    if active in {"inactive", "failed"}:
        return {"action": "verify", "verification": verification, "errors": []}
    return {"action": "refuse", "verification": verification,
            "errors": ["unit active state is unknown"]}


def stop_verified_unit(manifest: dict, identity: dict, verification: dict,
                       exec_fn=None, stop_timeout: float = 120.0) -> dict:
    """Stop only the exact unit after caller-supplied verification succeeded."""
    exec_fn = exec_fn or exec_capture
    validate_unit_identity(manifest, identity)
    if not verification.get("ok"):
        return {"ok": False, "stopped": False,
                "errors": list(verification.get("errors") or ["unit verification failed"])}
    active = verification.get("activeState")
    if active not in {"active", "activating", "deactivating", "inactive", "failed"}:
        return {"ok": False, "stopped": False,
                "errors": ["active state is missing or unknown"]}
    if active in {"inactive", "failed"}:
        return {"ok": True, "stopped": False, "errors": []}
    result = exec_fn(["systemctl", "--user", "stop", manifest["unit"]],
                     timeout=stop_timeout)
    return {
        "ok": bool(result.get("ok")),
        "stopped": bool(result.get("ok")),
        "result": result,
        "errors": [] if result.get("ok") else [
            result.get("error") or (result.get("stderr") or "").strip()
            or "systemctl stop failed"],
    }


# ---------------------------------------------------------------------------
# Public commands
# ---------------------------------------------------------------------------


def build_manifest_from_args(args: argparse.Namespace, run_id: str, run_dir: pathlib.Path,
                             cache_dir: pathlib.Path, cache_root: pathlib.Path,
                             port: int, image_id: str | None,
                             docker_env: dict[str, str] | None = None,
                             docker_endpoint: str | None = None,
                             exec_fn=None) -> dict:
    mode = args.mode
    host = args.host
    if host != "127.0.0.1":
        raise PreflightError("only the loopback host 127.0.0.1 is supported")
    remainder = list(args.remainder)
    if remainder and remainder[0] == "--":
        remainder = remainder[1:]
    service_command = args.service_command
    test_command = args.test_command
    if mode == "command":
        if test_command:
            raise PreflightError("--test-command is not valid in command mode; pass the command after --")
        if not remainder:
            raise PreflightError("command mode requires a command after --")
        command_argv = remainder
    else:
        command_argv = shlex_split(service_command)
    if mode == "native" and not command_argv:
        raise PreflightError("native mode requires --service-command")
    if mode == "container" and service_command:
        raise PreflightError("container mode uses docker-compose.test.yml; do not pass --service-command")

    readiness_url = args.readiness_url
    if readiness_url is None and mode == "container":
        readiness_url = f"http://{host}:{port}/api/ready"
    if readiness_url:
        readiness_url = readiness_url.replace("{host}", host).replace("{port}", str(port))
    if args.no_readiness:
        readiness_url = None

    resources = {
        "cpuQuota": args.cpu_quota,
        "memoryMax": args.memory_max,
        "tasksMax": args.tasks_max,
        "cpus": args.cpus,
        "memLimit": args.memory_limit,
        "pidsLimit": args.pids_limit,
    }
    if not CPU_QUOTA_RE.fullmatch(str(resources["cpuQuota"])):
        raise PreflightError("--cpu-quota must be a positive percentage such as 200%")
    if not MEMORY_RE.fullmatch(str(resources["memoryMax"])):
        raise PreflightError("--memory-max must be a systemd memory value such as 2G")
    if not 1 <= int(resources["tasksMax"]) <= 4096:
        raise PreflightError("--tasks-max must be between 1 and 4096")
    if not 1 <= int(resources["pidsLimit"]) <= 4096:
        raise PreflightError("--pids-limit must be between 1 and 4096")
    if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)?", str(resources["cpus"])):
        raise PreflightError("--cpus must be a positive number")
    if not re.fullmatch(r"[1-9][0-9]*[mg]?", str(resources["memLimit"])):
        raise PreflightError("--memory-limit must be a docker memory value such as 2g")

    compose_file = None
    container = None
    if mode == "container":
        if not image_id:
            raise PreflightError("container mode requires an existing exact image id")
        compose_file = str(pathlib.Path(args.compose_file).expanduser().resolve())
        if not pathlib.Path(compose_file).is_file():
            raise PreflightError(f"compose file not found: {compose_file}")
        project = "bom-local-test-" + secrets.token_hex(6)
        # An empty captured environment is the normal default local daemon.
        # Its positive endpoint identity is still mandatory and rechecked.
        if docker_env is None or not docker_endpoint:
            raise PreflightError("container mode requires a pinned docker endpoint")
        container = {
            "project": project,
            "composeFile": compose_file,
            "image": args.image,
            "imageId": image_id,
            "dockerEnv": dict(docker_env),
            "endpoint": docker_endpoint,
        }

    minimum_outer = args.max_duration + args.startup_timeout + args.shutdown_grace
    outer_timeout = (args.outer_timeout if args.outer_timeout is not None
                     else minimum_outer + 120)
    if outer_timeout < minimum_outer:
        raise PreflightError(
            "--outer-timeout must cover the workload plus startup and shutdown")
    if outer_timeout > 7 * 86400:
        raise PreflightError("--outer-timeout must be at most 7 days")
    outer_seconds = math.ceil(outer_timeout)
    return make_manifest(
        run_id=run_id,
        unit=args.unit,
        token=args.token,
        evidence_dir=str(run_dir),
        mode=mode,
        cache_dir=str(cache_dir),
        cache_root=str(cache_root),
        cache_owned=True,
        keep_cache=bool(args.keep_cache),
        network={"host": host, "port": port},
        resources=resources,
        runtime_max_seconds=outer_seconds,
        max_duration_seconds=args.max_duration,
        outer_timeout_seconds=outer_seconds,
        startup_timeout_seconds=args.startup_timeout,
        shutdown_grace_seconds=args.shutdown_grace,
        service_command=command_argv,
        test_command=shlex_split(test_command) if test_command else None,
        service_env=parse_kv(args.service_env, "--service-env"),
        test_env=parse_kv(args.test_env, "--test-env"),
        readiness_url=readiness_url,
        compose_file=compose_file,
        container=container,
    )


def shlex_split(value: str | list[str] | None) -> list[str] | None:
    if value is None:
        return None
    if isinstance(value, list):
        return value
    import shlex
    return shlex.split(value)


def write_launcher_failure(run_dir: pathlib.Path, manifest: dict, reason: str,
                           detail: object, code: int) -> dict:
    summary = summary_base(manifest)
    summary["startedAt"] = manifest["createdAt"]
    summary["finishedAt"] = iso()
    summary["actualDurationSeconds"] = 0.0
    summary["status"] = "failed"
    summary["exit"] = {
        "reason": reason,
        "code": code,
        "runnerCode": code,
        "signal": None,
        "service": None,
        "test": None,
        "detail": detail,
    }
    summary["cleanup"] = {
        "ok": False,
        "verifiedEmpty": False,
        "source": "launcher",
        "inspection": {"ok": False,
                       "errors": ["launch failed before verified cleanup"]},
        "errors": ["launch failed before verified cleanup"],
    }
    atomic_write_json(run_dir / SUMMARY_NAME, summary)
    return summary


def cmd_run(args: argparse.Namespace) -> int:
    run_id = validate_run_id(args.run_id) if args.run_id else new_run_id()
    run_dir = run_dir_for(run_id)
    if run_dir.exists() or run_dir.is_symlink():
        raise PreflightError(f"run directory already exists: {run_dir}")
    caps = detect_capabilities()
    require_capabilities(caps, args.mode, args.max_duration)
    host = args.host
    if host != "127.0.0.1":
        raise PreflightError("only the loopback host 127.0.0.1 is supported")
    if args.port:
        if not 1 <= int(args.port) <= 65535:
            raise PreflightError("--port is out of range")
        if not port_available(host, int(args.port)):
            raise PreflightError(f"requested port {args.port} is already in use on {host}")
        port = int(args.port)
    else:
        port = allocate_port(host)
    image_id = None
    docker_env = None
    docker_endpoint = None
    if args.mode == "container":
        docker_env = capture_docker_env()
        docker_endpoint = probe_docker_endpoint(docker_env)
        image_id = resolve_image_id(args.image, env=docker_env)

    RUN_ROOT.mkdir(parents=True, exist_ok=True)
    if RUN_ROOT.is_symlink():
        raise PreflightError(f"refusing symlink run root: {RUN_ROOT}")
    try:
        run_dir.mkdir(mode=0o700)
    except FileExistsError as exc:
        raise PreflightError(f"run directory already exists: {run_dir}") from exc
    with contextlib.suppress(OSError):
        run_dir.chmod(0o700)
    temp_root = (pathlib.Path(args.temp_root).expanduser().resolve()
                 if args.temp_root else pathlib.Path(tempfile.gettempdir()).resolve())
    temp_root.mkdir(parents=True, exist_ok=True)
    cache_dir = pathlib.Path(tempfile.mkdtemp(
        prefix=f"bom-local-test-{run_id}-cache-", dir=str(temp_root)))
    manifest_created = False
    try:
        ensure_owned_logs_dir(run_dir)
        args.unit = new_unit_name()
        args.token = new_owner_token()
        manifest = build_manifest_from_args(args, run_id, run_dir, cache_dir,
                                            temp_root, port, image_id,
                                            docker_env=docker_env,
                                            docker_endpoint=docker_endpoint)
        create_manifest(run_dir, manifest)
        manifest_created = True
        atomic_write_json(run_dir / SCHEMA_NAME, evidence_schema())
        atomic_write_json(run_dir / "capabilities.json", caps)
        append_event(run_dir, "manifest_created", unit=manifest["unit"],
                     mode=manifest["mode"])
    except Exception:
        if not manifest_created:
            with contextlib.suppress(OSError):
                shutil.rmtree(cache_dir)
            with contextlib.suppress(OSError):
                shutil.rmtree(run_dir)
        raise

    command = systemd_run_command(manifest, run_dir)
    result = exec_capture(command, timeout=45)
    append_event(run_dir, "systemd_launch", command=command,
                 returncode=result.get("returncode"), ok=result.get("ok"),
                 stderr=result.get("stderr"))
    if not result.get("ok"):
        summary = write_launcher_failure(run_dir, manifest, "startup_failed", result, 3)
        print(json.dumps(summary, indent=2))
        return 3
    identity = wait_for_persisted_identity(run_dir, manifest)
    if not identity:
        summary = write_launcher_failure(
            run_dir, manifest, "startup_failed",
            {"error": "systemd unit identity could not be verified"}, 3)
        print(json.dumps(summary, indent=2))
        return 3

    if args.detach:
        print(json.dumps({
            "runId": manifest["runId"],
            "status": "started",
            "evidenceDir": str(run_dir),
            "unit": manifest["unit"],
            "invocationId": identity["invocationId"],
            "controlGroup": identity["controlGroup"],
            "runtimeMaxSeconds": manifest["runtimeMaxSeconds"],
            "clientDisconnectedSafe": True,
        }, indent=2))
        return 0
    return wait_for_completion(run_dir, timeout=None)


def wait_for_completion(run_dir: pathlib.Path, timeout: float | None = None) -> int:
    manifest = load_manifest(run_dir)
    deadline = time.monotonic() + (timeout or 172800.0)
    evidence_deadline: float | None = None
    try:
        while time.monotonic() < deadline:
            summary = safe_json(run_dir / SUMMARY_NAME)
            finalization = safe_json(run_dir / FINALIZATION_NAME)
            if summary is not None and finalization is not None:
                completion = observe_completion(run_dir)
                if completion.get("verified"):
                    effective = effective_status(summary, completion, finalization)
                    print(json.dumps(summary, indent=2))
                    print(json.dumps({"completion": completion,
                                      "effectiveStatus": effective}, indent=2))
                    cleanup = finalization.get("cleanup") or {}
                    if not cleanup.get("destructiveOk"):
                        return 4
                    code = (summary.get("exit") or {}).get("runnerCode")
                    if not isinstance(code, int) or isinstance(code, bool):
                        return 5
                    return code if 0 <= code <= 255 else 5
                if evidence_deadline is None:
                    evidence_deadline = (time.monotonic()
                                         + manifest["shutdownGraceSeconds"] + 30)
                if time.monotonic() >= evidence_deadline:
                    print(json.dumps(summary, indent=2))
                    print(json.dumps({"completion": completion}, indent=2))
                    print(json.dumps({
                        "runId": manifest["runId"],
                        "status": "incomplete",
                        "error": "final observation is not verified; no destructive fallback",
                        "evidenceDir": str(run_dir),
                    }), file=sys.stderr)
                    return 5
            elif summary is not None:
                if evidence_deadline is None:
                    evidence_deadline = (time.monotonic()
                                         + manifest["shutdownGraceSeconds"] + 30)
                if time.monotonic() >= evidence_deadline:
                    print(json.dumps(summary, indent=2))
                    print(json.dumps({
                        "runId": manifest["runId"],
                        "status": "incomplete",
                        "error": "summary exists but finalization has not completed",
                        "evidenceDir": str(run_dir),
                    }), file=sys.stderr)
                    return 5
            else:
                metadata = systemd_show(manifest["unit"])
                active_state = metadata.get("ActiveState")
                if active_state in {"inactive", "failed"} or metadata.get("LoadState") == "not-found":
                    if evidence_deadline is None:
                        evidence_deadline = time.monotonic() + 20
                    if time.monotonic() >= evidence_deadline:
                        print(json.dumps({
                            "runId": manifest["runId"],
                            "status": "incomplete",
                            "error": "unit ended without a final summary; absent summary cannot imply pass",
                            "evidenceDir": str(run_dir),
                        }), file=sys.stderr)
                        return 5
            time.sleep(0.5)
    except KeyboardInterrupt:
        print(json.dumps({
            "runId": manifest["runId"],
            "status": "still_running",
            "evidenceDir": str(run_dir),
            "unit": manifest["unit"],
            "note": "waiting stopped without signalling the unit; use cleanup explicitly",
        }), file=sys.stderr)
        return 130
    print(json.dumps({
        "runId": manifest["runId"],
        "status": "incomplete",
        "error": "wait timed out without a destructive fallback",
        "evidenceDir": str(run_dir),
    }), file=sys.stderr)
    return 5


def cmd_status(args: argparse.Namespace) -> int:
    run_dir = resolve_run_dir(args.run_id)
    manifest = load_manifest(run_dir)
    identity = None
    boundary = None
    with contextlib.suppress(RunnerError):
        identity = load_unit_identity(run_dir, manifest)
    if identity:
        with contextlib.suppress(RunnerError):
            boundary = load_boundary(run_dir, manifest, identity)
    summary = safe_json(run_dir / SUMMARY_NAME)
    finalization = safe_json(run_dir / FINALIZATION_NAME)
    metadata = systemd_show(manifest["unit"])
    active_state = metadata.get("ActiveState")
    active = active_state in {"active", "activating", "deactivating"}
    completion = None
    if summary and finalization:
        with contextlib.suppress(RunnerError):
            completion = observe_completion(run_dir)
    effective = effective_status(summary or {}, completion, finalization)
    try:
        assert_current_binding(manifest)
        binding_current = True
    except RunnerError:
        binding_current = False
    output = {
        "schemaVersion": SCHEMA_VERSION,
        "runId": manifest["runId"],
        "mode": manifest["mode"],
        "evidenceDir": str(run_dir),
        "active": active,
        "unit": manifest["unit"],
        "unitState": {
            "activeState": active_state,
            "subState": metadata.get("SubState"),
            "result": metadata.get("Result"),
            "mainPid": metadata.get("MainPID"),
            "invocationId": metadata.get("InvocationID"),
            "transient": metadata.get("Transient"),
            "controlGroup": metadata.get("ControlGroup"),
        },
        "bindingCurrent": binding_current,
        "identityVerified": bool(identity and boundary),
        "completion": completion,
        "effectiveStatus": effective,
        "statusAuthority": "external-read-only-observer",
        "summary": summary,
        "finalization": finalization,
        "logs": {
            "dir": str(run_dir / "logs"),
            "supervisor": str(run_dir / "logs" / "supervisor.log"),
            "events": str(run_dir / EVENTS_NAME),
        },
    }
    print(json.dumps(output, indent=2))
    if completion and completion.get("verified"):
        cleanup = (finalization or {}).get("cleanup") or {}
        if not cleanup.get("destructiveOk"):
            return 4
        code = (summary.get("exit") or {}).get("runnerCode")
        if not isinstance(code, int) or isinstance(code, bool):
            return 5
        return code if 0 <= code <= 255 else 5
    if summary:
        return 4 if effective == "cleanup_failed" else 5
    return 0 if active else 5


def cmd_wait(args: argparse.Namespace) -> int:
    run_dir = resolve_run_dir(args.run_id)
    load_manifest(run_dir)
    return wait_for_completion(run_dir, timeout=args.timeout)


def cmd_cleanup(args: argparse.Namespace) -> int:
    run_dir = resolve_run_dir(args.run_id)
    refused: dict = {}
    verification = None
    with cleanup_lock(run_dir, ".cleanup.lock", timeout=2) as acquired:
        # This stop lock is released before finalization.  ExecStopPost uses a
        # separate .finalize.lock, so it cannot be starved by this command.
        if not acquired:
            refused = {"ok": False, "source": "explicit-cleanup",
                       "error": "another cleanup holds the stop lock; zero destructive actions"}
        else:
            try:
                manifest = load_manifest(run_dir, require_binding=True)
                identity = load_unit_identity(run_dir, manifest)
                boundary = load_boundary(run_dir, manifest, identity)
                plan = plan_cleanup(manifest, identity, boundary,
                                    systemd_show(manifest["unit"]))
                if plan["action"] == "refuse":
                    refused = {"ok": False, "source": "explicit-cleanup",
                               "error": "cleanup plan refused; zero destructive actions",
                               "plan": plan}
                else:
                    stop = stop_verified_unit(manifest, identity, plan["verification"])
                    if not stop.get("ok"):
                        refused = {"ok": False, "source": "explicit-cleanup",
                                   "error": "verified unit stop failed; no fallback cleanup",
                                   "stop": stop, "plan": plan}
                    else:
                        verification = plan["verification"]
            except RunnerError as exc:
                refused = {"ok": False, "source": "explicit-cleanup",
                           "error": f"{type(exc).__name__}: {exc}",
                           "detail": "ownership evidence is missing, stale or unverified"}
    if refused:
        atomic_write_json(run_dir / REFUSED_NAME, refused)
        print(json.dumps(refused, indent=2))
        return 4
    result = finalize_run(run_dir, "explicit-cleanup", verification=verification)
    completion = observe_completion(run_dir)
    print(json.dumps({"finalization": result, "completion": completion}, indent=2))
    cleanup = (result or {}).get("cleanup") or {}
    if not cleanup.get("destructiveOk"):
        return 4
    return 0 if completion.get("verified") else 4


def cmd_logs(args: argparse.Namespace) -> int:
    run_dir = resolve_run_dir(args.run_id)
    manifest = load_manifest(run_dir)
    files: list[str] = []
    logs_dir = run_dir / "logs"
    if logs_dir.is_symlink():
        raise RunnerError(f"refusing symlink log directory: {logs_dir}")
    if logs_dir.is_dir():
        for path in sorted(logs_dir.rglob("*.log")):
            if path.is_symlink():
                raise RunnerError(f"refusing symlink log path: {path}")
            files.append(str(path))
    output: dict = {"runId": manifest["runId"], "evidenceDir": str(run_dir),
                    "files": files}
    if args.tail:
        output["tails"] = {}
        for path in files:
            try:
                lines = pathlib.Path(path).read_text(
                    encoding="utf-8", errors="replace").splitlines()
                output["tails"][path] = lines[-args.tail:]
            except OSError as exc:
                output["tails"][path] = [f"<unreadable: {exc}>"]
    print(json.dumps(output, indent=2))
    return 0


def cmd_preflight(args: argparse.Namespace) -> int:
    caps = detect_capabilities()
    caps["required"] = {
        "dedicatedUnit": True,
        "cgroup2": True,
        "lingerForLongRuns": True,
        "localFallback": False,
    }
    print(json.dumps(caps, indent=2))
    if not caps["systemd"]["ok"] or not caps["cgroup2"]:
        return 3
    return 0


# ---------------------------------------------------------------------------
# Argument parsing and entry point
# ---------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="service_test_runner.py",
        description="Dedicated-unit runner for bounded BOM service/test runs.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("--version", action="version", version=f"%(prog)s {RUNNER_VERSION}")
    sub = parser.add_subparsers(dest="subcommand", required=True)

    run = sub.add_parser("run", help="start a dedicated-unit run",
                         formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    run.add_argument("--mode", choices=["command", "native", "container"], default="command")
    run.add_argument("--run-id", help="generated run id (a new one is used when omitted)")
    run.add_argument("--max-duration", type=parse_duration, default=parse_duration("30m"),
                     help="workload/sampling lifetime after readiness (command mode: from command start); excludes startup and teardown")
    run.add_argument("--outer-timeout", type=parse_duration, default=None,
                     help="fixed whole-run outer lifetime and systemd RuntimeMaxSec (default: workload + startup + shutdown + 120s)")
    run.add_argument("--startup-timeout", type=parse_duration, default=parse_duration("3m"))
    run.add_argument("--shutdown-grace", type=parse_duration, default=parse_duration("20s"))
    run.add_argument("--cpu-quota", default="200%", help="systemd CPUQuota")
    run.add_argument("--memory-max", default="2G", help="systemd MemoryMax")
    run.add_argument("--tasks-max", type=int, default=512, help="systemd TasksMax")
    run.add_argument("--cpus", default="2.0", help="compose cpus limit")
    run.add_argument("--memory-limit", default="2g", help="compose mem_limit")
    run.add_argument("--pids-limit", type=int, default=512, help="compose pids_limit")
    run.add_argument("--host", default="127.0.0.1", help="loopback host only")
    run.add_argument("--port", type=int, help="host port (allocated when omitted)")
    run.add_argument("--temp-root", help="parent directory for the unique cache")
    run.add_argument("--keep-cache", action="store_true",
                     help="do not delete the run-owned temporary cache")
    run.add_argument("--service-command", help="service command for native mode")
    run.add_argument("--test-command", help="test command run after readiness")
    run.add_argument("--service-env", action="append", default=[], metavar="KEY=VALUE")
    run.add_argument("--test-env", action="append", default=[], metavar="KEY=VALUE")
    run.add_argument("--readiness-url", help="HTTP readiness URL")
    run.add_argument("--no-readiness", action="store_true", help="skip HTTP readiness")
    run.add_argument("--compose-file", default=str(DEFAULT_COMPOSE_FILE))
    run.add_argument("--image", help="existing container image for compose")
    run.add_argument("--detach", action="store_true",
                     help="return after the verified unit identity is recorded")
    run.add_argument("remainder", nargs=argparse.REMAINDER,
                     help="command mode: command after --")

    pre = sub.add_parser("preflight", help="show dedicated-unit capabilities")
    pre.add_argument("--json", action="store_true", help="JSON output (always used)")

    for name in ("status", "wait", "cleanup", "logs"):
        cmd = sub.add_parser(name)
        cmd.add_argument("--run-id", required=True)
        if name == "wait":
            cmd.add_argument("--timeout", type=float, default=172800.0)
        if name == "logs":
            cmd.add_argument("--tail", type=int, default=0)

    hidden = sub.add_parser("__supervise")
    hidden.add_argument("--run-id", required=True)
    hidden = sub.add_parser("__finalize")
    hidden.add_argument("--run-id", required=True)
    hidden.add_argument("--source", default="manual-finalize")
    return parser


def main(argv: list[str] | None = None) -> int:
    if SAFETY_HOLD.exists():
        print("Runner disabled by coordinator safety hold; no live or cleanup actions permitted.",
              file=sys.stderr)
        return 3
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        if args.subcommand == "run":
            return cmd_run(args)
        if args.subcommand == "preflight":
            return cmd_preflight(args)
        if args.subcommand == "status":
            return cmd_status(args)
        if args.subcommand == "wait":
            return cmd_wait(args)
        if args.subcommand == "cleanup":
            return cmd_cleanup(args)
        if args.subcommand == "logs":
            return cmd_logs(args)
        if args.subcommand == "__supervise":
            return cmd_supervise(args.run_id)
        if args.subcommand == "__finalize":
            return cmd_finalize(args.run_id, args.source)
    except PreflightError as exc:
        print(json.dumps({"status": "preflight_failed", "error": str(exc)}), file=sys.stderr)
        return 3
    except RunnerError as exc:
        print(json.dumps({"status": "error", "error": str(exc)}), file=sys.stderr)
        return 2
    except Exception as exc:
        print(json.dumps({"status": "error",
                          "error": f"{type(exc).__name__}: {exc}"}), file=sys.stderr)
        return 5
    parser.error("unknown subcommand")
    return 2


if __name__ == "__main__":
    sys.exit(main())
