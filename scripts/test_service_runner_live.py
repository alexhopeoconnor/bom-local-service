#!/usr/bin/env python3
"""Authorized live fixtures for the dedicated-unit BOM test runner.

This file is intentionally separate from the pure suite.  It is not executed
while ``artifacts/execution/runner/SAFETY_HOLD`` exists and refuses to start
without an explicit ``--execute``.

Every fixture and the unrelated sentinel is launched by the real runner as a
fresh ``bom-local-test-<32 hex>.service`` unit with a short workload and outer
timeout.  The harness never starts an unmanaged fixture child, never uses
``os.kill``/``killpg``/``pkill`` and never sweeps processes or cgroups.

The only abrupt mutation is ``systemctl --user kill --kill-whom=main`` after a
read-only manifest/token/current-InvocationID/ControlGroup verification of the
exact generated test unit.  It is recorded as an audited mutation.  Normal
interruptions use the runner's explicit verified ``cleanup`` command.

This harness does not enumerate processes.  It records fixture child identities
(pid + start time + boot id) and later asserts absence read-only.  The external
runner ``status`` completion proof is the cleanup authority.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import secrets
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
RUNNER = ROOT / "scripts" / "service_test_runner.py"
SAFETY_HOLD = ROOT / "artifacts" / "execution" / "runner" / "SAFETY_HOLD"
EVIDENCE_ROOT = ROOT / "artifacts" / "execution" / "runner" / "live-fixtures"
RUN_ROOT = ROOT / "artifacts" / "execution" / "runner" / "runs"
MAX_HARNESS_SECONDS = 300.0
CLEANUP_RESERVE_SECONDS = 90.0
FINALIZER_AWAIT_SECONDS = 80.0
SENTINEL_MAX_DURATION = "10m"
SENTINEL_OUTER_TIMEOUT = "11m"
UNIT_RE = re.compile(r"^bom-local-test-[0-9a-f]{32}\.service$")

CASES = (
    "sentinel-survival",
    "success-daemon-child",
    "nonzero",
    "explicit-interruption",
    "workload-timeout",
    "abrupt-supervisor-death",
    "outer-stop-limit",
    "native-readiness",
)
CONTAINER_CASES = (
    "container-success", "container-nonzero", "container-interruption",
    "container-timeout",
)

MUTATION_ALLOWLIST = {
    ("systemctl", "--user", "kill", "--kill-whom=main", "--signal=KILL"),
    ("systemctl", "--user", "kill", "--kill-whom=main", "--signal=STOP"),
}


def mutation_allowed(argv: list[str]) -> bool:
    return (len(argv) == 6
            and tuple(argv[:5]) in MUTATION_ALLOWLIST
            and bool(UNIT_RE.fullmatch(argv[5])))


FIXTURE_SOURCE = r'''
import json, os, subprocess, sys, time, urllib.request

mode, evidence = sys.argv[1], sys.argv[2]
os.makedirs(evidence, exist_ok=True)

def start_time(pid):
    try:
        raw = open(f"/proc/{pid}/stat", encoding="utf-8").read()
        return int(raw.rsplit(")", 1)[1].split()[19])
    except (OSError, ValueError, IndexError):
        return None

def record(name):
    value = {
        "pid": os.getpid(),
        "startTime": start_time(os.getpid()),
        "bootId": open("/proc/sys/kernel/random/boot_id", encoding="ascii").read().strip(),
        "name": name,
        "recordedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    path = os.path.join(evidence, name + ".json")
    tmp = path + f".{os.getpid()}.tmp"
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(value, handle)
    os.replace(tmp, path)
    return value

def ready():
    path = os.path.join(evidence, "ready")
    tmp = path + f".{os.getpid()}.tmp"
    with open(tmp, "w", encoding="utf-8") as handle:
        handle.write("ready")
    os.replace(tmp, path)

record("fixture")
if mode == "success-daemon-child":
    child = subprocess.Popen(
        [sys.executable, os.path.abspath(__file__), "daemon-child", evidence],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL, start_new_session=True)
    marker = os.path.join(evidence, "child.json")
    deadline = time.monotonic() + 5
    while not os.path.exists(marker) and time.monotonic() < deadline:
        time.sleep(0.05)
    ready()
    raise SystemExit(0 if os.path.exists(marker) else 3)
if mode == "nonzero":
    ready()
    raise SystemExit(7)
if mode == "success":
    ready()
    raise SystemExit(0)
if mode in {"hold", "sentinel"}:
    ready()
    while True:
        time.sleep(1)
if mode == "daemon-child":
    record("child")
    ready()
    while True:
        time.sleep(1)
if mode == "http-server":
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path == "/api/ready":
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'{"status":"ready"}')
            else:
                self.send_response(404)
                self.end_headers()
        def log_message(self, *_args):
            pass
    server = ThreadingHTTPServer(("127.0.0.1", int(os.environ["BOM_TEST_PORT"])), Handler)
    server.daemon_threads = True
    ready()
    server.serve_forever()
if mode == "http-check":
    url = os.environ["BOM_TEST_BASE_URL"].rstrip("/") + "/api/ready"
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=1) as response:
                raise SystemExit(0 if response.status == 200 else 1)
        except Exception:
            time.sleep(0.2)
    raise SystemExit(1)
raise SystemExit(2)
'''


def utc_stamp() -> str:
    return time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())


def generate_run_id() -> str:
    return time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + secrets.token_hex(4)


def load_runner():
    spec = importlib.util.spec_from_file_location("bom_live_runner", RUNNER)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def read_json(path: pathlib.Path) -> dict | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def write_json(path: pathlib.Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(tmp, path)


def local_identity(name: str) -> dict:
    try:
        raw = pathlib.Path(f"/proc/{os.getpid()}/stat").read_text(encoding="utf-8")
        start = int(raw.rsplit(")", 1)[1].split()[19])
        boot = pathlib.Path("/proc/sys/kernel/random/boot_id").read_text(
            encoding="ascii").strip()
    except (OSError, ValueError, IndexError):
        return {"name": name, "pid": os.getpid(), "startTime": None,
                "bootId": None}
    return {"name": name, "pid": os.getpid(), "startTime": start,
            "bootId": boot}


def identity_state(identity: dict | None) -> str:
    """Read-only recorded-PID check.  No enumeration or signalling."""
    if not identity:
        return "unknown"
    try:
        boot = pathlib.Path("/proc/sys/kernel/random/boot_id").read_text(
            encoding="ascii").strip()
    except OSError:
        return "unknown"
    if boot != identity.get("bootId"):
        return "absent"
    pid = identity.get("pid")
    expected = identity.get("startTime")
    if not isinstance(pid, int) or not isinstance(expected, int):
        return "unknown"
    try:
        raw = pathlib.Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
        tail = raw.rsplit(")", 1)[1].split()
        state = tail[0]
        actual = int(tail[19])
    except FileNotFoundError:
        return "absent"
    except (OSError, ValueError, IndexError):
        return "unknown"
    if state in {"Z", "X", "x"}:
        return "absent"
    return "alive" if actual == expected else "absent"


def parse_stdout(text: str) -> dict | None:
    try:
        value = json.loads(text)
        return value if isinstance(value, dict) else None
    except ValueError:
        return None


class LiveHarness:
    def __init__(self, evidence: pathlib.Path, container_image: str | None = None):
        self.evidence = evidence
        self.fixture = evidence / "fixture.py"
        self.owned: list[dict] = []
        self.cases: list[dict] = []
        self.cleanup: list[dict] = []
        self.sentinel: dict | None = None
        self.harness_identity: dict | None = None
        self.runner_module = None
        self.container_image = container_image
        self.started_at = utc_stamp()
        self.started_monotonic = time.monotonic()
        self.deadline = self.started_monotonic + MAX_HARNESS_SECONDS
        self.case_deadline = (self.started_monotonic + MAX_HARNESS_SECONDS
                              - CLEANUP_RESERVE_SECONDS)

    def prepare(self) -> None:
        self.evidence.mkdir(parents=True, exist_ok=False, mode=0o700)
        self.harness_identity = local_identity("harness-ambient-witness")
        write_json(self.evidence / "harness-witness.json", self.harness_identity)
        self.fixture.write_text(FIXTURE_SOURCE, encoding="utf-8")
        self.runner_module = load_runner()

    def remaining(self, deadline: float) -> float:
        return max(0.0, deadline - time.monotonic())

    def finite_timeout(self, timeout: float, deadline: float) -> float | None:
        remaining = self.remaining(deadline)
        if remaining <= 0.05:
            return None
        return max(0.1, min(float(timeout), remaining))

    def runner(self, args: list[str], timeout: float = 30.0,
               deadline: float | None = None) -> dict:
        command = [sys.executable, str(RUNNER), *args]
        bound = self.finite_timeout(timeout, deadline or self.case_deadline)
        if bound is None:
            return {"command": command, "returncode": None, "stdout": "",
                    "stderr": "", "json": None,
                    "error": "harness case budget exhausted before runner call"}
        try:
            completed = subprocess.run(
                command, text=True, capture_output=True, timeout=bound,
                stdin=subprocess.DEVNULL, cwd=str(ROOT), check=False)
            return {
                "command": command,
                "returncode": completed.returncode,
                "stdout": completed.stdout,
                "stderr": completed.stderr,
                "json": parse_stdout(completed.stdout),
                "timeoutBoundSeconds": bound,
            }
        except subprocess.TimeoutExpired as exc:
            return {"command": command, "returncode": None,
                    "stdout": exc.stdout or "", "stderr": exc.stderr or "",
                    "json": None, "timeoutBoundSeconds": bound,
                    "error": f"client timeout after {bound:g}s"}

    def system(self, args: list[str], timeout: float = 20.0,
               deadline: float | None = None) -> dict:
        """Run one allowlisted system mutation after exact unit verification."""
        if not mutation_allowed(args):
            return {"command": args, "returncode": None, "stdout": "",
                    "stderr": "", "error": "mutation argv is not allowlisted"}
        bound = self.finite_timeout(timeout, deadline or self.case_deadline)
        if bound is None:
            return {"command": args, "returncode": None, "stdout": "",
                    "stderr": "", "error": "harness case budget exhausted"}
        try:
            completed = subprocess.run(
                args, text=True, capture_output=True, timeout=bound,
                stdin=subprocess.DEVNULL, cwd=str(ROOT), check=False)
            return {"command": args, "returncode": completed.returncode,
                    "stdout": completed.stdout, "stderr": completed.stderr,
                    "timeoutBoundSeconds": bound}
        except subprocess.TimeoutExpired as exc:
            return {"command": args, "returncode": None,
                    "stdout": exc.stdout or "", "stderr": exc.stderr or "",
                    "timeoutBoundSeconds": bound,
                    "error": f"system command timed out after {bound:g}s"}

    def status(self, run_id: str) -> dict | None:
        result = self.runner(["status", "--run-id", run_id], timeout=20)
        return result.get("json")

    def wait_ready(self, case_dir: pathlib.Path, names=("ready",), timeout: float = 20.0) -> bool:
        deadline = min(self.case_deadline, time.monotonic() + timeout)
        while time.monotonic() < deadline:
            if all((case_dir / name).exists() for name in names):
                return True
            time.sleep(0.1)
        return False

    def poll_completion(self, run_id: str, timeout: float = 25.0) -> dict | None:
        deadline = min(self.case_deadline, time.monotonic() + timeout)
        last = None
        while time.monotonic() < deadline:
            last = self.status(run_id)
            completion = (last or {}).get("completion") or {}
            if completion.get("verified"):
                return last
            time.sleep(0.5)
        return last

    def start(self, label: str, args: list[str], *, case_dir: pathlib.Path,
              ready_names=("ready",), startup_timeout: str = "3s") -> tuple[str | None, dict]:
        run_id = generate_run_id()
        self.owned.append({"label": label, "runId": run_id})
        result = self.runner(
            ["run", "--run-id", run_id, "--detach",
             "--startup-timeout", startup_timeout,
             "--shutdown-grace", "3s", *args], timeout=30)
        payload = result.get("json") or {}
        launch_record = dict(result)
        for key in ("stdout", "stderr"):
            if isinstance(launch_record.get(key), bytes):
                launch_record[key] = launch_record[key].decode("utf-8", errors="replace")
        write_json(case_dir / "run-launch.json", launch_record)
        if result.get("returncode") != 0 or payload.get("status") != "started":
            raise RuntimeError(f"{label}: runner launch failed; see {case_dir / 'run-launch.json'}")
        if payload.get("clientDisconnectedSafe") is not True:
            raise RuntimeError(f"{label}: detached lifetime guarantee was not confirmed")
        if run_id != payload.get("runId"):
            raise RuntimeError(f"{label}: runner returned a different run id: {result}")
        if not self.wait_ready(case_dir, ready_names):
            raise RuntimeError(f"{label}: fixture readiness marker did not appear")
        return run_id, result

    def command_args(self, mode: str, case_dir: pathlib.Path, *,
                     max_duration: str, outer_timeout: str) -> list[str]:
        return ["--mode", "command", "--max-duration", max_duration,
                "--outer-timeout", outer_timeout, "--",
                sys.executable, str(self.fixture), mode, str(case_dir)]

    def case(self, name: str) -> None:
        if time.monotonic() >= self.case_deadline:
            self.cases.append({"name": name, "ok": False, "error": "harness deadline"})
            return
        case_dir = self.evidence / "cases" / name
        case_dir.mkdir(parents=True, mode=0o700)
        try:
            result = self._run_case(name, case_dir)
            result["name"] = name
            self.cases.append(result)
        except Exception as exc:
            self.cases.append({"name": name, "ok": False,
                               "error": f"{type(exc).__name__}: {exc}"})

    def _run_case(self, name: str, case_dir: pathlib.Path) -> dict:
        if name in CONTAINER_CASES:
            return self._container_case(name, case_dir)
        if name == "sentinel-survival":
            return self._start_sentinel(case_dir)
        if name == "success-daemon-child":
            run_id, _ = self.start(
                name, self.command_args("success-daemon-child", case_dir,
                                        max_duration="8s", outer_timeout="16s"),
                case_dir=case_dir, ready_names=("ready", "child.json"))
            status = self.poll_completion(run_id)
            child = read_json(case_dir / "child.json")
            return self._assert(name, status, expected_code=0,
                                expected_reason="command_exit",
                                absent=[child], sentinel_survives=True)
        if name == "nonzero":
            run_id, _ = self.start(
                name, self.command_args("nonzero", case_dir,
                                        max_duration="8s", outer_timeout="16s"),
                case_dir=case_dir)
            status = self.poll_completion(run_id)
            return self._assert(name, status, expected_code=7,
                                expected_reason="command_exit",
                                absent=[read_json(case_dir / "fixture.json")],
                                sentinel_survives=True)
        if name == "explicit-interruption":
            run_id, _ = self.start(
                name, self.command_args("hold", case_dir,
                                        max_duration="30s", outer_timeout="45s"),
                case_dir=case_dir)
            cleanup = self.runner(["cleanup", "--run-id", run_id], timeout=45)
            status = self.poll_completion(run_id)
            return self._assert(name, status, expected_code=None,
                                expected_reason="interrupted",
                                absent=[read_json(case_dir / "fixture.json")],
                                sentinel_survives=True,
                                require_cleanup_ok=True,
                                extra={"cleanup": cleanup.get("json") or cleanup,
                                       "cleanupReturncode": cleanup.get("returncode")})
        if name == "workload-timeout":
            run_id, _ = self.start(
                name, self.command_args("hold", case_dir,
                                        max_duration="4s", outer_timeout="12s"),
                case_dir=case_dir)
            status = self.poll_completion(run_id, timeout=20)
            return self._assert(name, status, expected_code=124,
                                expected_reason="deadline",
                                absent=[read_json(case_dir / "fixture.json")],
                                sentinel_survives=True)
        if name == "abrupt-supervisor-death":
            return self._abrupt(case_dir)
        if name == "outer-stop-limit":
            return self._outer_stop(case_dir)
        if name == "native-readiness":
            service = shlex.join([sys.executable, str(self.fixture),
                                  "http-server", str(case_dir)])
            test = shlex.join([sys.executable, str(self.fixture),
                               "http-check", str(case_dir)])
            run_id, _ = self.start(
                name, ["--mode", "native", "--max-duration", "8s",
                       "--outer-timeout", "24s", "--service-command", service,
                       "--test-command", test,
                       "--readiness-url", "http://127.0.0.1:{port}/api/ready"],
                case_dir=case_dir, startup_timeout="8s")
            status = self.poll_completion(run_id)
            return self._assert(name, status, expected_code=0,
                                expected_reason="command_exit",
                                absent=[read_json(case_dir / "fixture.json")],
                                sentinel_survives=True)
        raise RuntimeError(f"unimplemented case: {name}")

    def _container_case(self, name: str, case_dir: pathlib.Path) -> dict:
        """Synthetic container workload; all mutations stay in the audited runner."""
        if not self.container_image or not re.fullmatch(r"sha256:[0-9a-f]{64}", self.container_image):
            raise RuntimeError("container fixture requires an existing exact --container-image sha256 ID")
        import yaml
        compose = yaml.safe_load((ROOT / "docker-compose.test.yml").read_text())
        service = compose["services"]["service"]
        # Keep the production ownership labels, isolation and resource bounds.
        # Override only the workload: no BOM, browser, network fetch or build.
        service["entrypoint"] = ["/bin/sh", "-c", "sleep 600 & wait"]
        service["command"] = []
        service["healthcheck"] = {"disable": True}
        compose_path = case_dir / "compose.yml"
        compose_path.write_text(yaml.safe_dump(compose, sort_keys=False))
        mode = {"container-success": "success", "container-nonzero": "nonzero"}.get(name, "hold")
        command = shlex.join([sys.executable, str(self.fixture), mode, str(case_dir)])
        timeout_case = name == "container-timeout"
        run_id, _ = self.start(
            name, ["--mode", "container", "--image", self.container_image,
                   "--compose-file", str(compose_path), "--no-readiness",
                   "--test-command", command,
                   "--max-duration", "4s" if timeout_case else "30s",
                   "--outer-timeout", "35s" if timeout_case else "60s"],
            case_dir=case_dir, startup_timeout="20s")
        extra = {"fixtureImageId": self.container_image,
                 "composeSha256": hashlib.sha256(compose_path.read_bytes()).hexdigest()}
        interrupted = name == "container-interruption"
        if interrupted:
            cleanup = self.runner(["cleanup", "--run-id", run_id], timeout=45)
            extra.update(cleanup=cleanup.get("json") or cleanup,
                         cleanupReturncode=cleanup.get("returncode"))
        status = self.poll_completion(run_id, timeout=45)
        result = self._assert(
            name, status,
            expected_code=None if interrupted else (124 if timeout_case else (7 if mode == "nonzero" else 0)),
            expected_reason="interrupted" if interrupted else ("deadline" if timeout_case else "command_exit"),
            absent=[read_json(case_dir / "fixture.json")], sentinel_survives=True,
            require_cleanup_ok=interrupted, extra=extra)
        cleanup = ((status or {}).get("finalization") or {}).get("cleanup") or {}
        container = cleanup.get("container") or {}
        if container.get("verifiedEmpty") is not True:
            result["errors"].append("owned container/network teardown was not verified")
        if not container.get("containerIdsBefore"):
            result["errors"].append("fixture did not establish an owned container existed")
        if container.get("containerIdsAfter") != [] or container.get("networkIdsAfter") != []:
            result["errors"].append("owned container/network inventory is not empty")
        result["extra"]["containerCleanup"] = container
        result["ok"] = not result["errors"]
        return result

    def ensure_sentinel(self) -> None:
        if self.sentinel is not None:
            return
        case_dir = self.evidence / "cases" / "sentinel-survival"
        case_dir.mkdir(parents=True, mode=0o700, exist_ok=True)
        self._start_sentinel(case_dir)

    def _start_sentinel(self, case_dir: pathlib.Path) -> dict:
        if self.sentinel is not None:
            state = identity_state(self.sentinel)
            witness = identity_state(self.harness_identity)
            return {"ok": state == "alive" and witness == "alive",
                    "sentinel": state, "harnessWitness": witness,
                    "identity": self.sentinel}
        run_id, _ = self.start(
            "sentinel", self.command_args("sentinel", case_dir,
                                          max_duration=SENTINEL_MAX_DURATION,
                                          outer_timeout=SENTINEL_OUTER_TIMEOUT),
            case_dir=case_dir)
        self.sentinel = read_json(case_dir / "fixture.json")
        state = identity_state(self.sentinel)
        witness = identity_state(self.harness_identity)
        return {"ok": state == "alive" and witness == "alive",
                "sentinel": state, "harnessWitness": witness,
                "runId": run_id, "identity": self.sentinel}

    def _abrupt(self, case_dir: pathlib.Path) -> dict:
        run_id, _ = self.start(
            "abrupt", self.command_args("hold", case_dir,
                                        max_duration="20s", outer_timeout="35s"),
            case_dir=case_dir)
        status = self.status(run_id) or {}
        manifest = self.runner_module.load_manifest(
            pathlib.Path(status["evidenceDir"]), require_binding=True)
        identity = self.runner_module.load_unit_identity(
            pathlib.Path(status["evidenceDir"]), manifest)
        boundary = self.runner_module.load_boundary(
            pathlib.Path(status["evidenceDir"]), manifest, identity)
        verification = self.runner_module.verify_live_unit(manifest, identity)
        if not verification.get("ok"):
            raise RuntimeError(f"abrupt: exact unit verification refused: {verification}")
        if verification.get("activeState") not in {"active", "activating", "deactivating"}:
            raise RuntimeError(f"abrupt: unit is not active: {verification.get('activeState')}")
        metadata = verification.get("metadata") or {}
        main_pid = metadata.get("MainPID")
        if (str(main_pid) != str(boundary.get("mainPid"))
                or str(main_pid) != str(boundary.get("supervisorPid"))):
            raise RuntimeError(f"abrupt: live MainPID does not match boundary: {metadata}")
        if metadata.get("SubState") != "running":
            raise RuntimeError(f"abrupt: SubState is not running: {metadata.get('SubState')}")
        mutation = {
            "kind": "systemctl-kill-main",
            "reason": "abrupt dedicated-supervisor death fixture; audited mutation",
            "unit": manifest["unit"],
            "invocationId": identity["invocationId"],
            "controlGroup": identity["controlGroup"],
            "boundaryDigest": hashlib.sha256(
                json.dumps(boundary, sort_keys=True, separators=(",", ":")).encode()
            ).hexdigest(),
            "verification": verification,
            "command": ["systemctl", "--user", "kill", "--kill-whom=main",
                        "--signal=KILL", manifest["unit"]],
            "status": "pending",
            "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }
        # Pending record is durable before the only abrupt mutation.  It is
        # deliberately after exact manifest/token/current-InvocationID proof.
        write_json(case_dir / "abrupt-mutation.json", mutation)
        killed = self.system(mutation["command"], timeout=20)
        mutation["result"] = killed
        mutation["postMetadata"] = self.runner_module.systemd_show(manifest["unit"])
        mutation["status"] = "recorded"
        write_json(case_dir / "abrupt-mutation.json", mutation)
        if killed.get("returncode") != 0:
            raise RuntimeError(f"abrupt: verified systemctl kill failed: {killed}")
        status = self.poll_completion(run_id, timeout=FINALIZER_AWAIT_SECONDS)
        return self._assert("abrupt-supervisor-death", status, expected_code=None,
                            expected_reason="supervisor_lost",
                            absent=[read_json(case_dir / "fixture.json")],
                            sentinel_survives=True,
                            extra={"abruptMutation": mutation,
                                   "expected": "incomplete/supervisor_lost with verified cleanup"})

    def _outer_stop(self, case_dir: pathlib.Path) -> dict:
        run_id, _ = self.start(
            "outer-stop-limit", self.command_args("hold", case_dir,
                                                  max_duration="30s",
                                                  outer_timeout="45s"),
            case_dir=case_dir)
        status = self.status(run_id) or {}
        evidence = pathlib.Path(status["evidenceDir"])
        manifest = self.runner_module.load_manifest(evidence, require_binding=True)
        identity = self.runner_module.load_unit_identity(evidence, manifest)
        boundary = self.runner_module.load_boundary(evidence, manifest, identity)
        verification = self.runner_module.verify_live_unit(manifest, identity)
        metadata = verification.get("metadata") or {}
        if not verification.get("ok"):
            raise RuntimeError(f"outer-stop: exact unit verification refused: {verification}")
        if (str(metadata.get("MainPID")) != str(boundary.get("mainPid"))
                or str(metadata.get("MainPID")) != str(boundary.get("supervisorPid"))):
            raise RuntimeError("outer-stop: live MainPID does not match boundary")
        if metadata.get("SubState") != "running":
            raise RuntimeError("outer-stop: SubState is not running")
        mutation = {
            "kind": "systemctl-stop-main-runtime-limit",
            "reason": ("new STOP path pending independent audit; only systemd "
                       "RuntimeMaxSec/TimeoutStopSec may terminate this unit"),
            "pendingAudit": True,
            "unit": manifest["unit"],
            "invocationId": identity["invocationId"],
            "controlGroup": identity["controlGroup"],
            "verification": verification,
            "command": ["systemctl", "--user", "kill", "--kill-whom=main",
                        "--signal=STOP", manifest["unit"]],
            "status": "pending",
            "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }
        write_json(case_dir / "outer-stop-mutation.json", mutation)
        stopped = self.system(mutation["command"], timeout=20)
        mutation["result"] = stopped
        mutation["postMetadata"] = self.runner_module.systemd_show(manifest["unit"])
        mutation["status"] = "recorded"
        write_json(case_dir / "outer-stop-mutation.json", mutation)
        if stopped.get("returncode") != 0:
            raise RuntimeError(f"outer-stop: verified systemctl STOP failed: {stopped}")
        # No resume and no PID signal.  Only RuntimeMaxSec/TimeoutStopSec act.
        status = self.poll_completion(run_id, timeout=FINALIZER_AWAIT_SECONDS)
        return self._assert("outer-stop-limit", status, expected_code=124,
                            expected_reason="deadline",
                            absent=[read_json(case_dir / "fixture.json")],
                            sentinel_survives=True,
                            extra={"outerStopMutation": mutation,
                                   "expected": "deadline after RuntimeMaxSec/TimeoutStopSec"})

    def _assert(self, name: str, status: dict | None, *, expected_code: int | None,
                expected_reason: str | None = None,
                expected_status: str | None = "cleanup_pending",
                absent: list[dict | None], sentinel_survives: bool,
                require_cleanup_ok: bool = False,
                extra: dict | None = None) -> dict:
        errors: list[str] = []
        if not status:
            errors.append("no status output")
            return {"ok": False, "errors": errors, "extra": extra}
        completion = status.get("completion") or {}
        summary = status.get("summary") or {}
        exit_info = summary.get("exit") or {}
        if not completion.get("verified"):
            errors.append("external completion proof not verified")
        if not completion.get("noLiveOwnedProcesses"):
            errors.append("observer did not record noLiveOwnedProcesses")
        runner_code = exit_info.get("runnerCode")
        if not isinstance(runner_code, int) or isinstance(runner_code, bool):
            errors.append(f"summary runnerCode is missing or not an int: {runner_code!r}")
        elif expected_code is not None and runner_code != expected_code:
            errors.append(f"expected exit {expected_code}, got {runner_code}")
        if expected_reason is not None and exit_info.get("reason") != expected_reason:
            errors.append(f"expected reason {expected_reason}, got {exit_info.get('reason')}")
        if expected_status is not None and summary.get("status") != expected_status:
            errors.append(f"expected status {expected_status}, got {summary.get('status')}")
        if require_cleanup_ok:
            cleanup_payload = (extra or {}).get("cleanup") or {}
            if (extra or {}).get("cleanupReturncode") != 0:
                errors.append("explicit cleanup did not return 0")
            if not ((cleanup_payload.get("finalization") or {}).get("cleanup") or {}).get("ok"):
                errors.append("explicit cleanup finalization.ok is not true")
            if not (cleanup_payload.get("completion") or {}).get("verified"):
                errors.append("explicit cleanup completion was not verified")
        for identity in absent:
            if identity_state(identity) != "absent":
                errors.append(f"recorded child still identifiable: {identity}")
        if sentinel_survives and identity_state(self.sentinel) != "alive":
            errors.append("unrelated sentinel did not survive")
        if identity_state(self.harness_identity) != "alive":
            errors.append("harness ambient witness identity is not alive")
        return {"ok": not errors, "errors": errors,
                "completion": completion, "exit": exit_info, "extra": extra}

    def cleanup_owned(self) -> None:
        total = len(self.owned)
        for index, run in enumerate(self.owned):
            run_dir = RUN_ROOT / run["runId"]
            if not (run_dir / "ownership.json").is_file():
                self.cleanup.append({"label": run["label"], "runId": run["runId"],
                                     "skipped": "no manifest was created"})
                continue
            remaining_runs = total - index
            remaining_budget = self.remaining(self.deadline)
            per_run = max(5.0, remaining_budget / max(1, remaining_runs))
            try:
                status = self.runner(["status", "--run-id", run["runId"]],
                                     timeout=per_run, deadline=self.deadline)
                completion = ((status.get("json") or {}).get("completion") or {}
                              if status.get("json") else {})
                if completion.get("verified"):
                    self.cleanup.append({"label": run["label"], "runId": run["runId"],
                                         "skipped": "external completion already verified"})
                    continue
                result = self.runner(["cleanup", "--run-id", run["runId"]],
                                     timeout=per_run, deadline=self.deadline)
                self.cleanup.append({"label": run["label"], "runId": run["runId"],
                                     "returncode": result.get("returncode"),
                                     "ok": result.get("returncode") == 0,
                                     "timeoutBoundSeconds": result.get("timeoutBoundSeconds"),
                                     "stderr": result.get("stderr")})
            except Exception as exc:
                self.cleanup.append({"label": run["label"], "runId": run["runId"],
                                     "ok": False,
                                     "error": f"{type(exc).__name__}: {exc}"})


def main(argv: list[str] | None = None) -> int:
    if SAFETY_HOLD.exists():
        print("Live fixtures disabled by coordinator safety hold.", file=sys.stderr)
        return 3
    parser = argparse.ArgumentParser(description="Live dedicated-unit fixtures")
    parser.add_argument("--execute", action="store_true",
                        help="required; without it only lists the cases")
    parser.add_argument("--case", action="append", choices=CASES + CONTAINER_CASES, default=None,
                        help="run only the named case(s); repeatable")
    parser.add_argument("--container-image", help="existing BusyBox-compatible exact sha256 image ID")
    parser.add_argument("--fail-fast", action="store_true",
                        help="stop after the first failed case")
    args = parser.parse_args(argv)
    if not args.execute:
        print("Cases: " + ", ".join(CASES))
        print("Refusing to execute without --execute.")
        return 0
    selected = list(dict.fromkeys(args.case or CASES))

    evidence = EVIDENCE_ROOT / f"{utc_stamp()}-{os.getpid()}"
    if any(case in CONTAINER_CASES for case in selected) and not args.container_image:
        parser.error("container cases require --container-image")
    harness = LiveHarness(evidence, args.container_image)
    harness.prepare()
    status = 0
    try:
        # The sibling-unit sentinel is the first live object regardless of
        # --case order; the harness's own identity is the ambient witness.
        harness.ensure_sentinel()
        for case in selected:
            harness.case(case)
            if args.fail_fast and harness.cases and not harness.cases[-1].get("ok"):
                break
    finally:
        harness.cleanup_owned()
        elapsed = time.monotonic() - harness.started_monotonic
        report = {
            "schemaVersion": 1,
            "kind": "live-dedicated-unit-fixtures",
            "startedAt": harness.started_at,
            "finishedAt": utc_stamp(),
            "evidence": str(evidence),
            "selectedCases": selected,
            "bounds": {
                "configuredBudgetSeconds": MAX_HARNESS_SECONDS,
                "cleanupReserveSeconds": CLEANUP_RESERVE_SECONDS,
                "caseBudgetSeconds": MAX_HARNESS_SECONDS - CLEANUP_RESERVE_SECONDS,
                "actualElapsedSeconds": round(elapsed, 3),
                "exceededConfiguredBudget": elapsed > MAX_HARNESS_SECONDS,
            },
            "harnessWitness": harness.harness_identity,
            "cases": harness.cases,
            "cleanup": harness.cleanup,
            "sentinel": harness.sentinel,
            "hold": False,
        }
        write_json(evidence / "report.json", report)
        if any(not item.get("ok") for item in harness.cases):
            status = 1
        if any(not item.get("ok") and not item.get("skipped") for item in harness.cleanup):
            status = 1
        if elapsed > MAX_HARNESS_SECONDS:
            status = 1
        print(json.dumps({"evidence": str(evidence), "status": status,
                          "cases": len(harness.cases),
                          "actualElapsedSeconds": report["bounds"]["actualElapsedSeconds"]},
                         indent=2))
    return status


if __name__ == "__main__":
    sys.exit(main())
