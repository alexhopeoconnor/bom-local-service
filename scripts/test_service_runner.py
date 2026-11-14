#!/usr/bin/env python3
"""Pure mocked regression tests for scripts/service_test_runner.py.

These tests never start a process, systemd unit, container or signal.  Every
systemctl/docker call is injected or replaced by a fake that only records the
argv it would have run.  The suite is intentionally safe to run while
artifacts/execution/runner/SAFETY_HOLD exists; ``./scripts/run-service-test.sh
selftest`` remains blocked by that hold.

The live synthetic fixture gate (success, failure, interruption, deadline,
abrupt controller death, unrelated sentinel survives) is deliberately not
implemented here.  It must be designed and authorized separately after this
pure suite and the independent audit pass.
"""

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import tempfile
import time
import types
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parent.parent
RUNNER = ROOT / "scripts" / "service_test_runner.py"
COMPOSE = ROOT / "docker-compose.test.yml"
RUN_ID = "20261115T120000Z-deadbeef"
UNIT = "bom-local-test-" + "a" * 32 + ".service"
TOKEN = "b" * 64
IMAGE_ID = "sha256:" + "c" * 64
INVOCATION = "d" * 32


def load_runner_module():
    spec = importlib.util.spec_from_file_location("bom_service_test_runner", RUNNER)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def control_group_for(unit: str) -> str:
    uid = os.getuid()
    return f"/user.slice/user-{uid}.slice/user@{uid}.service/app.slice/{unit}"


def make_run_dir(base: pathlib.Path) -> pathlib.Path:
    run_dir = base / RUN_ID
    run_dir.mkdir(mode=0o700)
    return run_dir


def make_manifest(module, run_dir: pathlib.Path, *, mode: str = "command",
                  container: dict | None = None, **overrides) -> dict:
    cache_root = run_dir.parent / "cache-root"
    cache_root.mkdir(exist_ok=True)
    cache_dir = pathlib.Path(tempfile.mkdtemp(
        prefix=f"bom-local-test-{run_dir.name}-cache-", dir=str(cache_root)))
    manifest = module.make_manifest(
        run_id=run_dir.name,
        unit=UNIT,
        token=TOKEN,
        evidence_dir=str(run_dir),
        mode=mode,
        cache_dir=str(cache_dir),
        cache_root=str(cache_root),
        cache_owned=True,
        service_command=["/bin/true"] if mode != "container" else None,
        container=container,
    )
    manifest.update(overrides)
    module.create_manifest(run_dir, manifest)
    return manifest


def make_identity(module, manifest: dict, control_group: str | None = None,
                  invocation: str = INVOCATION) -> dict:
    identity = {
        "schemaVersion": module.SCHEMA_VERSION,
        "runnerVersion": module.RUNNER_VERSION,
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "ownerToken": manifest["ownerToken"],
        "binding": manifest["binding"],
        "invocationId": invocation,
        "controlGroup": control_group or control_group_for(manifest["unit"]),
        "transient": True,
        "recordedAt": module.iso(),
    }
    identity["ownershipDigest"] = module.ownership_digest(manifest, identity)
    return identity


def make_boundary(module, manifest: dict, identity: dict,
                  parent_stat: dict | None = None,
                  unit_stat: dict | None = None,
                  mount: dict | None = None) -> dict:
    mount = mount or {
        "mountId": 30, "parentId": 1, "majorMinor": "0:30", "root": "/",
        "mountPoint": "/sys/fs/cgroup", "fstype": "cgroup2", "dev": 1, "ino": 2,
    }
    unit_stat = unit_stat or {
        "exists": True, "path": "/sys/fs/cgroup" + identity["controlGroup"],
        "dev": 1, "ino": 3, "mode": 0o040700, "isDir": True, "isSymlink": False,
    }
    parent_stat = parent_stat or {
        "exists": True, "path": "/sys/fs/cgroup/app.slice", "dev": 1, "ino": 4,
        "mode": 0o040700, "isDir": True, "isSymlink": False, "listable": True,
    }
    return {
        "schemaVersion": module.SCHEMA_VERSION,
        "runnerVersion": module.RUNNER_VERSION,
        "runId": manifest["runId"],
        "unit": manifest["unit"],
        "ownerToken": manifest["ownerToken"],
        "binding": manifest["binding"],
        "invocationId": identity["invocationId"],
        "controlGroup": identity["controlGroup"],
        "ownershipDigest": identity["ownershipDigest"],
        "supervisorPid": 4242,
        "mainPid": 4242,
        "procSelfCgroup": identity["controlGroup"],
        "mainPidInCgroup": True,
        "selfInCgroup": True,
        "observedAt": module.iso(),
        "pidNamespace": manifest["binding"]["pidNamespace"],
        "cgroupNamespace": manifest["binding"]["cgroupNamespace"],
        "bootId": manifest["binding"]["bootId"],
        "cgroup2Mount": mount,
        "unitCgroupStat": unit_stat,
        "parentStat": parent_stat,
    }


def make_metadata(module, manifest: dict, identity: dict, *, active: str = "active",
                  load_state: str = "loaded", ok: bool = True, **overrides) -> dict:
    environment = " ".join([
        f"BOM_TEST_OWNER_TOKEN={manifest['ownerToken']}",
        f"BOM_TEST_BOOT_ID={manifest['binding']['bootId']}",
        f"BOM_TEST_PID_NAMESPACE={manifest['binding']['pidNamespace']}",
        f"BOM_TEST_CGROUP_NAMESPACE={manifest['binding']['cgroupNamespace']}",
        f"BOM_TEST_RUNNER_HASH={manifest['binding']['runnerHash']}",
    ])
    metadata = {
        "_ok": ok,
        "Id": manifest["unit"],
        "LoadState": load_state,
        "ActiveState": active,
        "SubState": "running" if active == "active" else "dead",
        "Result": "success",
        "MainPID": str(os.getpid()),
        "InvocationID": identity["invocationId"],
        "Transient": "yes",
        "ControlGroup": identity["controlGroup"],
        "Description": f"BOM test owner={manifest['ownerToken']}",
        "Environment": environment,
        "FragmentPath": "/run/user/1000/systemd/transient/" + manifest["unit"],
    }
    metadata.update(overrides)
    return metadata


class FakeDocker:
    """Read-only fake docker CLI; records argv and mutates only fake state."""

    def __init__(self, manifest: dict, *, image_id: str | None = None,
                 corrupt_labels: bool = False, fail_list: bool = False,
                 endpoint: str | None = None):
        self.manifest = manifest
        self.image_id = image_id or (manifest.get("container") or {}).get("imageId")
        self.endpoint = endpoint or (manifest.get("container") or {}).get("endpoint")
        self.corrupt_labels = corrupt_labels
        self.fail_list = fail_list
        self.calls: list[list[str]] = []
        self.envs: list[dict | None] = []
        self.containers = {"c" * 64: self._container("c" * 64, "service")}
        self.networks = {"e" * 64: self._network("e" * 64)}

    def _container(self, cid: str, role: str) -> dict:
        labels = {
            "io.bom.test.run-id": self.manifest["runId"],
            "io.bom.test.owner-token": self.manifest["ownerToken"],
            "io.bom.test.runner-version": self.manifest["binding"]["runnerVersion"],
            "com.docker.compose.project": (self.manifest.get("container") or {}).get("project"),
            "io.bom.test.role": role,
        }
        if self.corrupt_labels:
            labels["io.bom.test.owner-token"] = "f" * 64
        return {
            "Id": cid,
            "Image": self.image_id,
            "Config": {"Labels": labels},
            "State": {"Status": "running", "ExitCode": 0, "Health": {"Status": "healthy"}},
            "Name": "/bom-local-test-fake",
        }

    def _network(self, nid: str) -> dict:
        labels = {
            "io.bom.test.run-id": self.manifest["runId"],
            "io.bom.test.owner-token": self.manifest["ownerToken"],
            "io.bom.test.runner-version": self.manifest["binding"]["runnerVersion"],
            "com.docker.compose.project": (self.manifest.get("container") or {}).get("project"),
        }
        if self.corrupt_labels:
            labels["io.bom.test.owner-token"] = "f" * 64
        return {"Id": nid, "Labels": labels, "Name": "bom-local-test-fake_default"}

    def _result(self, argv, stdout="", ok=True):
        return {"ok": ok, "argv": argv, "returncode": 0 if ok else 1,
                "stdout": stdout, "stderr": "" if ok else "failed",
                "timedOut": False, "startedAt": "x", "finishedAt": "x"}

    def __call__(self, argv, timeout=20.0, env=None, cwd=None):
        self.calls.append(list(argv))
        self.envs.append(env)
        if argv[:2] == ["docker", "info"]:
            return self._result(argv, self.endpoint)
        if argv[:3] == ["docker", "image", "inspect"]:
            return self._result(argv, self.image_id)
        if argv[:2] == ["docker", "ps"]:
            if self.fail_list:
                return self._result(argv, "", False)
            return self._result(argv, "".join(f"{cid}\n" for cid in self.containers))
        if argv[:3] == ["docker", "network", "ls"]:
            if self.fail_list:
                return self._result(argv, "", False)
            return self._result(argv, "".join(f"{nid}\n" for nid in self.networks))
        if argv[:2] == ["docker", "inspect"] or argv[:3] == ["docker", "container", "inspect"]:
            cid = argv[-1]
            if cid not in self.containers:
                return self._result(argv, "", False)
            return self._result(argv, json.dumps([self.containers[cid]]))
        if argv[:3] == ["docker", "network", "inspect"]:
            nid = argv[-1]
            if nid not in self.networks:
                return self._result(argv, "", False)
            return self._result(argv, json.dumps([self.networks[nid]]))
        if argv[:2] == ["docker", "stop"]:
            return self._result(argv)
        if argv[:3] == ["docker", "rm", "-f"]:
            self.containers.pop(argv[-1], None)
            return self._result(argv)
        if argv[:3] == ["docker", "network", "rm"]:
            self.networks.pop(argv[-1], None)
            return self._result(argv)
        return self._result(argv, "", False)


class PureRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = load_runner_module()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = pathlib.Path(self.temp.name)
        self.run_dir = make_run_dir(self.base)
        self.manifest = make_manifest(self.module, self.run_dir)
        self.identity = make_identity(self.module, self.manifest)
        self.boundary = make_boundary(self.module, self.manifest, self.identity)
        self.subprocess_run = mock.patch.object(
            self.module.subprocess, "run",
            side_effect=AssertionError("pure test attempted a real subprocess"))
        self.subprocess_popen = mock.patch.object(
            self.module.subprocess, "Popen",
            side_effect=AssertionError("pure test attempted a real process"))
        self.subprocess_run.start()
        self.subprocess_popen.start()
        self.addCleanup(self.subprocess_run.stop)
        self.addCleanup(self.subprocess_popen.stop)

    # -- immutable binding and manifest ---------------------------------

    def test_manifest_binds_boot_pid_cgroup_namespace_and_runner_hash(self):
        binding = self.manifest["binding"]
        self.assertEqual(binding["bootId"], self.module.boot_id())
        self.assertEqual(binding["pidNamespace"], self.module.pid_namespace_inode())
        self.assertEqual(binding["cgroupNamespace"], self.module.namespace_inode("cgroup"))
        self.assertEqual(binding["runnerHash"], self.module.RUNNER_HASH)
        self.assertEqual(binding["schemaVersion"], self.module.SCHEMA_VERSION)

    def test_manifest_is_exclusive_create(self):
        with self.assertRaises(self.module.RunnerError):
            self.module.create_manifest(self.run_dir, self.manifest)

    def test_manifest_rejects_foreign_runner_version(self):
        bad = dict(self.manifest)
        bad["runnerVersion"] = "1.0.0"
        with self.assertRaises(self.module.RunnerError):
            self.module.validate_manifest(bad, self.run_dir)

    def test_manifest_rejects_unsafe_prior_schema(self):
        bad = dict(self.manifest)
        bad["schemaVersion"] = 1
        with self.assertRaises(self.module.RunnerError):
            self.module.validate_manifest(bad, self.run_dir)

    def test_manifest_binding_mismatch_is_refused_for_cleanup(self):
        bad = dict(self.manifest)
        bad["binding"] = dict(self.manifest["binding"], bootId="00000000-0000-0000-0000-000000000000")
        with self.assertRaises(self.module.RunnerError):
            self.module.assert_current_binding(bad)

    def test_manifest_rejects_symlink(self):
        target = self.run_dir / "ownership.json"
        other = self.base / "other.json"
        other.write_text("{}")
        target.unlink()
        target.symlink_to(other)
        with self.assertRaises(self.module.RunnerError):
            self.module.load_manifest(self.run_dir)

    def test_manifest_rejects_arbitrary_unit_and_token(self):
        bad = dict(self.manifest, unit="alex-pc-paseo.service")
        with self.assertRaises(self.module.RunnerError):
            self.module.validate_manifest(bad, self.run_dir)
        bad = dict(self.manifest, ownerToken="short")
        with self.assertRaises(self.module.RunnerError):
            self.module.validate_manifest(bad, self.run_dir)

    def test_unit_identity_requires_exact_binding_and_digest(self):
        self.module.validate_unit_identity(self.manifest, self.identity)
        bad = dict(self.identity, binding=dict(self.manifest["binding"], runnerHash="0" * 64))
        with self.assertRaises(self.module.RunnerError):
            self.module.validate_unit_identity(self.manifest, bad)
        bad = dict(self.identity, ownershipDigest="0" * 64)
        with self.assertRaises(self.module.RunnerError):
            self.module.validate_unit_identity(self.manifest, bad)

    def test_native_env_forces_loopback_and_owned_cache_after_aliases(self):
        manifest = dict(
            self.manifest,
            mode="native",
            serviceEnv={
                "ASPNETCORE_URLS": "http://0.0.0.0:9999",
                "CACHEDIRECTORY": "/tmp/not-owned",
                "aspnetcore_urls": "http://0.0.0.0:8888",
                "cachedirectory": "/tmp/not-owned-lower",
            },
        )
        with mock.patch.dict(os.environ, {
                "ASPNETCORE_URLS": "http://0.0.0.0:7777",
                "CACHEDIRECTORY": "/tmp/inherited"}, clear=False):
            env = self.module.base_env(manifest)
        self.assertEqual(env["ASPNETCORE_URLS"],
                         f"http://127.0.0.1:{manifest['network']['port']}")
        self.assertEqual(env["CACHEDIRECTORY"], manifest["cacheDir"])
        self.assertNotIn("aspnetcore_urls", env)
        self.assertNotIn("cachedirectory", env)

    def test_parse_kv_rejects_case_aliases_and_docker_env(self):
        for value in ("aspnetcore_urls=http://0.0.0.0:1",
                      "CachedDirectory=/tmp/evil",
                      "Docker_Host=tcp://evil:2375",
                      "bom_test_port=1"):
            with self.assertRaises(Exception):
                self.module.parse_kv([value], "--service-env")

    def test_effective_status_prefers_verified_observer(self):
        summary = {"status": "cleanup_failed",
                   "exit": {"reason": "command_exit", "runnerCode": 0}}
        finalization = {"cleanup": {"destructiveOk": True}}
        self.assertEqual(self.module.effective_status(
            summary, {"verified": True}, finalization), "passed")
        summary = {"status": "passed",
                   "exit": {"reason": "deadline", "runnerCode": 124}}
        self.assertEqual(self.module.effective_status(
            summary, {"verified": True}, finalization), "timed_out")
        self.assertEqual(self.module.effective_status(
            {"status": "passed"}, {"verified": False}, finalization), "passed")

    def test_systemd_timeout_overrides_interrupted_summary(self):
        exit_info = {"reason": "interrupted", "code": 143, "runnerCode": 143}
        result = self.module.classify_systemd_timeout(
            exit_info, {"SERVICE_RESULT": "timeout"})
        self.assertEqual(result["reason"], "deadline")
        self.assertEqual(result["runnerCode"], 124)

    def test_finalize_env_token_requires_unit_or_verified_caller(self):
        with mock.patch.dict(os.environ, {"BOM_TEST_OWNER_TOKEN": TOKEN}, clear=False):
            self.assertTrue(self.module.finalize_env_allowed(self.manifest))
        with mock.patch.dict(os.environ, {"BOM_TEST_OWNER_TOKEN": "f" * 64}, clear=False):
            self.assertFalse(self.module.finalize_env_allowed(self.manifest))
        with mock.patch.dict(os.environ, {"BOM_TEST_OWNER_TOKEN": "f" * 64}, clear=False), \
             mock.patch.object(self.module, "resolve_run_dir",
                               return_value=self.run_dir), \
             mock.patch.object(self.module, "load_manifest",
                               return_value=self.manifest), \
             mock.patch.object(self.module, "finalize_run") as finalize:
            self.assertEqual(self.module.cmd_finalize(self.run_dir.name, "test"), 4)
            finalize.assert_not_called()

    def test_supervisor_persists_identity_before_workload(self):
        with mock.patch.dict(os.environ, {"INVOCATION_ID": INVOCATION}, clear=False):
            with mock.patch.object(self.module, "proc_self_cgroup_path",
                                   return_value=self.identity["controlGroup"]):
                identity = self.module.record_supervisor_identity(
                    self.run_dir, self.manifest)
        self.assertEqual(identity["invocationId"], INVOCATION)
        self.assertEqual(identity["verificationSource"], "supervisor-env-self-cgroup")
        self.assertEqual(self.module.load_unit_identity(
            self.run_dir, self.manifest)["ownershipDigest"],
            self.identity["ownershipDigest"])
        with mock.patch.object(self.module, "systemd_show",
                               side_effect=AssertionError("launcher must not race systemd")):
            loaded = self.module.wait_for_persisted_identity(
                self.run_dir, self.manifest, timeout=1)
        self.assertEqual(loaded["invocationId"], INVOCATION)

    def test_existing_boundary_is_validated_not_adopted(self):
        first = self.module.create_boundary(
            self.run_dir, self.boundary, manifest=self.manifest,
            identity=self.identity)
        self.assertEqual(first["ownershipDigest"], self.identity["ownershipDigest"])
        tampered = dict(self.boundary, observedAt="different")
        with self.assertRaises(self.module.RunnerError):
            self.module.create_boundary(
                self.run_dir, tampered, manifest=self.manifest,
                identity=self.identity)

    def test_finalize_wrapper_takes_finalize_lock_before_body(self):
        (self.run_dir / self.module.UNIT_IDENTITY_NAME).write_text(
            json.dumps(self.identity))
        observed = {}

        class Lock:
            def __enter__(self_inner):
                observed["locked"] = True
                return True

            def __exit__(self_inner, *_args):
                observed["unlocked"] = True

        def body(*_args, **_kwargs):
            observed["body"] = observed.get("locked") and not observed.get("unlocked")
            return {"ok": True}

        with mock.patch.object(self.module, "cleanup_lock",
                               side_effect=lambda *_a, **_k: Lock()) as lock:
            with mock.patch.object(self.module, "_finalize_run_locked",
                                   side_effect=body):
                self.module.finalize_run(self.run_dir, "test")
        self.assertTrue(observed.get("body"))
        self.assertEqual(lock.call_args.args[1], ".finalize.lock")

    def test_external_stop_beats_racing_child_sigterm_return(self):
        class Child:
            def poll(self):
                return -15

        deadline = time.monotonic() + 1
        result = self.module.wait_process(
            Child(), deadline, {"signal": 15})
        self.assertEqual(result["kind"], "interrupted")
        self.assertEqual(result["code"], 143)
        self.assertEqual(result["signal"], "SIGTERM")

    def test_command_self_signal_remains_command_exit(self):
        class Child:
            def poll(self):
                return -15

        result = self.module.wait_process(
            Child(), time.monotonic() + 1, {"signal": None})
        self.assertEqual(result["kind"], "exit")
        self.assertEqual(result["code"], 143)
        self.assertEqual(result["signal"], "SIGTERM")

    def test_final_normalization_preserves_classified_sigint(self):
        classified = {"reason": "interrupted", "code": 130,
                      "runnerCode": 130, "signal": "SIGINT"}
        result = self.module.normalize_runner_code(classified, {"signal": None})
        self.assertEqual(result["runnerCode"], 130)
        self.assertEqual(result["signal"], "SIGINT")
        stop_race = {"reason": "interrupted", "code": 143,
                     "signal": "SIGTERM"}
        self.assertEqual(self.module.normalize_runner_code(
            stop_race, {"signal": 15})["runnerCode"], 143)
        self.assertEqual(self.module.normalize_runner_code(
            {"reason": "command_exit", "code": 7}, {})["runnerCode"], 7)
        self.assertEqual(self.module.normalize_runner_code(
            {"reason": "service_exit", "code": 0}, {})["runnerCode"], 5)
        self.assertEqual(self.module.normalize_runner_code(
            {"reason": "service_exit", "code": 7}, {})["runnerCode"], 7)
        self.assertEqual(self.module.normalize_runner_code(
            {"reason": "deadline", "code": 124}, {})["runnerCode"], 124)

    def test_racing_signal_exit_uses_deactivating_unit_state(self):
        exit_info = {"reason": "command_exit", "code": 143,
                     "runnerCode": 143, "signal": "SIGTERM"}
        self.assertEqual(self.module.classify_racing_signal_exit(
            dict(exit_info), {"signal": None},
            {"ActiveState": "deactivating"})["reason"], "interrupted")
        self.assertEqual(self.module.classify_racing_signal_exit(
            dict(exit_info), {"signal": None},
            {"ActiveState": "active"})["reason"], "command_exit")

    def test_external_stop_override_updates_command_exit_summary(self):
        exit_info = {"reason": "command_exit", "code": 143,
                     "runnerCode": 143, "signal": "SIGTERM"}
        result = self.module.external_stop_overrides(
            exit_info, {"signal": 15})
        self.assertEqual(result["reason"], "interrupted")
        self.assertEqual(result["runnerCode"], 143)
        self.assertEqual(result["signal"], "SIGTERM")
        self.assertEqual(self.module.external_stop_overrides(
            {"reason": "command_exit", "code": 7, "runnerCode": 7},
            {"signal": None})["reason"], "command_exit")

    def test_finalizer_accepts_pre_stop_verified_post_stop_unloaded_enoent(self):
        (self.run_dir / self.module.UNIT_IDENTITY_NAME).write_text(
            json.dumps(self.identity))
        (self.run_dir / self.module.BOUNDARY_NAME).write_text(
            json.dumps(self.boundary))
        verification = {
            "ok": True,
            "activeState": "active",
            "controlGroup": self.identity["controlGroup"],
            "errors": [],
        }
        unloaded = {
            "_ok": True, "LoadState": "not-found", "ActiveState": None,
            "InvocationID": None, "ControlGroup": None, "Result": None,
        }
        with mock.patch.object(self.module, "systemd_show", return_value=unloaded), \
             mock.patch.object(self.module, "wait_cgroup_empty",
                               return_value={"ok": True, "exists": False,
                                             "empty": False, "verified": False,
                                             "remainingPids": []}), \
             mock.patch.object(self.module, "enoent_boundary_stable",
                               return_value={"ok": True}), \
             mock.patch.object(self.module, "remove_cache",
                               return_value={"removed": True}):
            result = self.module._finalize_run_locked(
                self.run_dir, "test", verification=verification)
        self.assertTrue(result["cleanup"]["ok"], result)
        self.assertTrue(result["cleanup"]["provisionalVerifiedEmpty"])
        summary = json.loads((self.run_dir / self.module.SUMMARY_NAME).read_text())
        self.assertEqual(summary["status"], "cleanup_pending")
        self.assertTrue(summary["cleanup"]["provisionalVerifiedEmpty"])

    def test_explicit_cleanup_releases_stop_lock_before_finalize(self):
        state = {"inside": False, "finalized_after_release": False}

        class Lock:
            def __enter__(self_inner):
                state["inside"] = True
                return True

            def __exit__(self_inner, *_args):
                state["inside"] = False

        def finalize(*_args, **_kwargs):
            state["finalized_after_release"] = not state["inside"]
            return {"cleanup": {"ok": True, "destructiveOk": True}}

        with mock.patch.object(self.module, "resolve_run_dir",
                               return_value=self.run_dir), \
             mock.patch.object(self.module, "cleanup_lock",
                               side_effect=lambda *_a, **_k: Lock()):
            with mock.patch.object(self.module, "load_manifest",
                                   return_value=self.manifest), \
                 mock.patch.object(self.module, "load_unit_identity",
                                   return_value=self.identity), \
                 mock.patch.object(self.module, "load_boundary",
                                   return_value=self.boundary), \
                 mock.patch.object(self.module, "systemd_show",
                                   return_value=make_metadata(
                                       self.module, self.manifest, self.identity)), \
                 mock.patch.object(self.module, "plan_cleanup",
                                   return_value={"action": "verify",
                                                 "verification": {"ok": True}}), \
                 mock.patch.object(self.module, "stop_verified_unit",
                                   return_value={"ok": True}), \
                 mock.patch.object(self.module, "finalize_run", side_effect=finalize), \
                 mock.patch.object(self.module, "observe_completion",
                                   return_value={"verified": True}):
                args = types.SimpleNamespace(run_id=self.run_dir.name)
                self.assertEqual(self.module.cmd_cleanup(args), 0)
        self.assertTrue(state["finalized_after_release"])

    # -- control group and metadata ------------------------------------

    def test_control_group_accepts_exact_user_unit(self):
        result = self.module.control_group_belongs_to_unit(
            UNIT, control_group_for(UNIT))
        self.assertTrue(result["ok"], result["errors"])

    def test_control_group_rejects_inherited_paseo_controller(self):
        paseo = f"/user.slice/user-{os.getuid()}.slice/user@{os.getuid()}.service/app.slice/alex-pc-paseo.service"
        result = self.module.control_group_belongs_to_unit(UNIT, paseo)
        self.assertFalse(result["ok"])
        nested = paseo + "/" + UNIT
        result = self.module.control_group_belongs_to_unit(UNIT, nested)
        self.assertFalse(result["ok"])
        self.assertTrue(any("service ancestor" in item for item in result["errors"]))

    def test_control_group_rejects_empty_relative_and_system_paths(self):
        invalid_user = f"/user.slice/user-{os.getuid() + 1}.slice/user@{os.getuid()}.service/app.slice/{UNIT}"
        for path in ("", "/", UNIT, "/system.slice/" + UNIT, invalid_user,
                     f"/user.slice/user-{os.getuid()}.slice/session-1.scope/app.slice/{UNIT}"):
            result = self.module.control_group_belongs_to_unit(UNIT, path)
            self.assertFalse(result["ok"], path)

    def test_control_group_rejects_inherited_scope_ancestor(self):
        path = (f"/user.slice/user-{os.getuid()}.slice/user@{os.getuid()}.service/"
                f"session-2.scope/app.slice/{UNIT}")
        result = self.module.control_group_belongs_to_unit(UNIT, path)
        self.assertFalse(result["ok"])
        self.assertTrue(any("scope ancestor" in item for item in result["errors"]))

    def test_metadata_requires_successful_show_and_loaded_state(self):
        ok = make_metadata(self.module, self.manifest, self.identity)
        self.assertTrue(self.module.verify_unit_metadata(
            UNIT, TOKEN, INVOCATION, ok, binding=self.manifest["binding"])["ok"])
        failed = dict(ok, _ok=False)
        self.assertFalse(self.module.verify_unit_metadata(
            UNIT, TOKEN, INVOCATION, failed, binding=self.manifest["binding"])["ok"])
        absent = dict(ok, LoadState="not-found")
        self.assertFalse(self.module.verify_unit_metadata(
            UNIT, TOKEN, INVOCATION, absent, binding=self.manifest["binding"])["ok"])

    def test_metadata_rejects_token_invocation_and_foreign_unit(self):
        ok = make_metadata(self.module, self.manifest, self.identity)
        self.assertFalse(self.module.verify_unit_metadata(
            UNIT, "f" * 64, INVOCATION, ok, binding=self.manifest["binding"])["ok"])
        self.assertFalse(self.module.verify_unit_metadata(
            UNIT, TOKEN, "e" * 32, ok, binding=self.manifest["binding"])["ok"])
        self.assertFalse(self.module.verify_unit_metadata(
            "alex-pc-paseo.service", TOKEN, INVOCATION, ok,
            binding=self.manifest["binding"])["ok"])

    def test_metadata_rejects_ambient_controller_cgroup(self):
        ok = make_metadata(
            self.module, self.manifest, self.identity,
            ControlGroup=f"/user.slice/user-{os.getuid()}.slice/user@{os.getuid()}.service/app.slice/alex-pc-paseo.service")
        self.assertFalse(self.module.verify_unit_metadata(
            UNIT, TOKEN, INVOCATION, ok, binding=self.manifest["binding"])["ok"])

    def test_outer_timeout_covers_30h_sample_under_31h_policy(self):
        manifest = self.module.make_manifest(
            run_id=RUN_ID,
            unit=UNIT,
            token=TOKEN,
            evidence_dir=str(self.run_dir),
            mode="command",
            cache_dir=self.manifest["cacheDir"],
            cache_root=self.manifest["cacheRoot"],
            max_duration_seconds=self.module.parse_duration("30h"),
            startup_timeout_seconds=self.module.parse_duration("3m"),
            shutdown_grace_seconds=self.module.parse_duration("20s"),
            outer_timeout_seconds=111600,
            runtime_max_seconds=111600,
        )
        self.module.validate_manifest(manifest, self.run_dir)
        self.assertLessEqual(manifest["runtimeMaxSeconds"], 111600)
        self.assertGreaterEqual(
            manifest["outerTimeoutSeconds"],
            manifest["maxDurationSeconds"] + manifest["startupTimeoutSeconds"]
            + manifest["shutdownGraceSeconds"])
        command = self.module.systemd_run_command(manifest, self.run_dir)
        self.assertIn("--property=RuntimeMaxSec=111600", command)
        too_short = dict(manifest, outerTimeoutSeconds=108000, runtimeMaxSeconds=108000)
        with self.assertRaises(self.module.RunnerError):
            self.module.validate_manifest(too_short, self.run_dir)

    def test_cmd_run_creates_owned_logs_dir_before_systemd_run(self):
        run_root = self.base / "runs"
        temp_root = self.base / "temp"
        run_dir = run_root / RUN_ID
        seen = {}

        def fake_exec(argv, timeout=45, env=None, cwd=None):
            seen["argv"] = argv
            seen["logsExists"] = (run_dir / "logs").is_dir()
            seen["logsSymlink"] = (run_dir / "logs").is_symlink()
            return {"ok": True, "returncode": 0, "stdout": "", "stderr": ""}

        parser = self.module.build_parser()
        args = parser.parse_args([
            "run", "--mode", "command", "--run-id", RUN_ID,
            "--temp-root", str(temp_root), "--detach", "--", "/bin/true",
        ])
        with mock.patch.object(self.module, "RUN_ROOT", run_root), \
             mock.patch.object(self.module, "detect_capabilities",
                               return_value={"systemd": {"ok": True},
                                             "cgroup2": True,
                                             "docker": {"ok": True}}), \
             mock.patch.object(self.module, "require_capabilities"), \
             mock.patch.object(self.module, "allocate_port", return_value=12345), \
             mock.patch.object(self.module, "exec_capture", side_effect=fake_exec), \
             mock.patch.object(self.module, "wait_for_persisted_identity",
                               return_value={"invocationId": INVOCATION,
                                             "controlGroup": control_group_for(UNIT)}):
            self.assertEqual(self.module.cmd_run(args), 0)
        self.assertTrue(seen["logsExists"])
        self.assertFalse(seen["logsSymlink"])
        self.assertIn("--property=StandardOutput=append:", " ".join(seen["argv"]))

    def test_unit_name_is_random_not_run_id(self):
        unit_a = self.module.new_unit_name()
        unit_b = self.module.new_unit_name()
        self.assertRegex(unit_a, r"^bom-local-test-[0-9a-f]{32}\.service$")
        self.assertNotEqual(unit_a, unit_b)
        self.assertNotIn(RUN_ID, unit_a)
        command = self.module.systemd_run_command(self.manifest, self.run_dir)
        self.assertIn("--unit", command)
        self.assertIn(self.manifest["unit"], command)
        self.assertNotIn("--durability", command)

    # -- cleanup planning and read-only stops ---------------------------

    def test_plan_refuses_before_mutation_on_invocation_mismatch(self):
        metadata = make_metadata(self.module, self.manifest, self.identity,
                                 InvocationID="e" * 32)
        plan = self.module.plan_cleanup(self.manifest, self.identity,
                                        self.boundary, metadata)
        self.assertEqual(plan["action"], "refuse")
        self.assertTrue(plan["errors"])

    def test_plan_refuses_tampered_boundary_and_binding(self):
        bad_boundary = dict(self.boundary, ownerToken="f" * 64)
        metadata = make_metadata(self.module, self.manifest, self.identity)
        plan = self.module.plan_cleanup(self.manifest, self.identity,
                                        bad_boundary, metadata)
        self.assertEqual(plan["action"], "refuse")
        bad_manifest = dict(self.manifest)
        bad_manifest["binding"] = dict(self.manifest["binding"], runnerHash="0" * 64)
        plan = self.module.plan_cleanup(bad_manifest, self.identity,
                                        self.boundary, metadata)
        self.assertEqual(plan["action"], "refuse")

    def test_plan_selects_stop_only_for_active_exact_unit(self):
        active = make_metadata(self.module, self.manifest, self.identity, active="active")
        self.assertEqual(self.module.plan_cleanup(
            self.manifest, self.identity, self.boundary, active)["action"], "stop_unit")
        inactive = make_metadata(self.module, self.manifest, self.identity, active="inactive")
        self.assertEqual(self.module.plan_cleanup(
            self.manifest, self.identity, self.boundary, inactive)["action"], "verify")
        unknown = make_metadata(self.module, self.manifest, self.identity, active="unknown")
        self.assertEqual(self.module.plan_cleanup(
            self.manifest, self.identity, self.boundary, unknown)["action"], "refuse")

    def test_stop_verified_unit_uses_only_exact_unit(self):
        calls = []
        def fake(argv, timeout=120.0, env=None, cwd=None):
            calls.append(list(argv))
            return {"ok": True, "returncode": 0, "stdout": "", "stderr": ""}
        verification = self.module.verify_unit_metadata(
            UNIT, TOKEN, INVOCATION,
            make_metadata(self.module, self.manifest, self.identity, active="active"),
            binding=self.manifest["binding"])
        result = self.module.stop_verified_unit(
            self.manifest, self.identity, verification, exec_fn=fake)
        self.assertTrue(result["ok"])
        self.assertEqual(calls, [["systemctl", "--user", "stop", UNIT]])

    def test_stop_is_not_called_when_verification_failed(self):
        calls = []
        def fake(argv, timeout=120.0, env=None, cwd=None):
            calls.append(list(argv))
            return {"ok": True}
        failed = {"ok": False, "errors": ["InvocationID mismatch"],
                  "activeState": "active"}
        result = self.module.stop_verified_unit(
            self.manifest, self.identity, failed, exec_fn=fake)
        self.assertFalse(result["ok"])
        self.assertEqual(calls, [])

    def test_cleanup_lock_uses_holder_identity_not_pid_signals(self):
        handle = self.module.cleanup_lock(self.run_dir)
        with handle as acquired:
            self.assertTrue(acquired)
            holder = json.loads((self.run_dir / ".cleanup.lock").read_text())
            self.assertEqual(holder["bootId"], self.module.boot_id())
            self.assertEqual(holder["pid"], os.getpid())
        with self.module.cleanup_lock(self.run_dir) as acquired_again:
            self.assertTrue(acquired_again)

    # -- cgroup boundary proof ------------------------------------------

    def _cgroup(self, *, procs="", populated="0", child_pid: int | None = None,
                missing: bool = False) -> pathlib.Path:
        root = self.base / "cgroup"
        unit_path = root / self.identity["controlGroup"].lstrip("/")
        if missing:
            return root
        unit_path.mkdir(parents=True)
        (unit_path / "cgroup.procs").write_text(procs)
        (unit_path / "cgroup.events").write_text(f"populated {populated}\n")
        if child_pid is not None:
            child = unit_path / "child.slice"
            child.mkdir()
            (child / "cgroup.procs").write_text(f"{child_pid}\n")
            (child / "cgroup.events").write_text("populated 1\n")
        return root

    def test_cgroup_observation_requires_positive_populated_zero(self):
        root = self._cgroup(procs="", populated="0")
        result = self.module.read_cgroup_observation(
            self.identity["controlGroup"], self_pid=os.getpid(), cgroup_root=root)
        self.assertTrue(result["ok"])
        self.assertTrue(result["exists"])
        self.assertTrue(result["empty"])
        self.assertTrue(result["allPopulatedZero"])
        self.assertTrue(result["verified"])

    def test_exec_stop_post_self_is_provisional_not_final(self):
        root = self._cgroup(procs=f"{os.getpid()}\n", populated="1")
        result = self.module.read_cgroup_observation(
            self.identity["controlGroup"], self_pid=os.getpid(), cgroup_root=root)
        self.assertTrue(result["empty"])          # self excluded
        self.assertFalse(result["allPopulatedZero"])  # self still occupies boundary
        self.assertFalse(result["verified"])

    def test_cgroup_observation_detects_child_cgroup_process(self):
        root = self._cgroup(procs="", populated="0", child_pid=999999)
        result = self.module.read_cgroup_observation(
            self.identity["controlGroup"], self_pid=os.getpid(), cgroup_root=root)
        self.assertFalse(result["empty"])
        self.assertEqual(result["remainingPids"], [999999])
        self.assertFalse(result["verified"])

    def test_vanished_cgroup_is_unverified_not_empty(self):
        root = self._cgroup(missing=True)
        result = self.module.read_cgroup_observation(
            self.identity["controlGroup"], self_pid=os.getpid(), cgroup_root=root)
        self.assertTrue(result["ok"])
        self.assertFalse(result["exists"])
        self.assertFalse(result["empty"])
        self.assertFalse(result["verified"])
        self.assertEqual(result["remainingPids"], [])

    def test_unreadable_cgroup_is_unknown_not_empty(self):
        root = self._cgroup(procs="", populated="0")
        unit_path = root / self.identity["controlGroup"].lstrip("/")
        (unit_path / "cgroup.procs").unlink()
        (unit_path / "cgroup.procs").mkdir()
        result = self.module.read_cgroup_observation(
            self.identity["controlGroup"], self_pid=os.getpid(), cgroup_root=root)
        self.assertFalse(result["ok"])
        self.assertFalse(result["verified"])

    # -- container exactness --------------------------------------------

    def test_default_docker_environment_is_pinned_by_endpoint(self):
        args = self.module.build_parser().parse_args([
            "run", "--mode", "container", "--image", IMAGE_ID,
            "--compose-file", str(COMPOSE), "--no-readiness",
        ])
        args.unit, args.token = UNIT, self.manifest["ownerToken"]
        def build(env, endpoint):
            return self.module.build_manifest_from_args(
                args, RUN_ID, self.run_dir,
                self.base / "cache", self.base, 12345, IMAGE_ID,
                docker_env=env, docker_endpoint=endpoint)
        manifest = build({}, "daemon-test|bom|1.0")
        self.assertEqual(manifest["container"]["dockerEnv"], {})
        self.assertEqual(manifest["container"]["endpoint"], "daemon-test|bom|1.0")
        for env, endpoint in [(None, "daemon-test|bom|1.0"), ({}, None)]:
            with self.assertRaises(self.module.PreflightError):
                build(env, endpoint)

    def _container_manifest(self):
        run_dir = self.base / "20261115T120001Z-cafebabe"
        run_dir.mkdir()
        container = {
            "project": "bom-local-test-abcdef123456",
            "composeFile": str(COMPOSE),
            "image": "bom-local-service:candidate",
            "imageId": IMAGE_ID,
            "dockerEnv": {"DOCKER_HOST": "unix:///tmp/bom-test-docker.sock"},
            "endpoint": "daemon-test|bom|1.0",
        }
        manifest = make_manifest(self.module, run_dir, mode="container",
                                 container=container)
        return manifest

    def test_container_cleanup_targets_only_exact_label_image_and_ids(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest)
        result = self.module.container_cleanup(manifest, exec_fn=fake,
                                               source="test")
        self.assertTrue(result["ok"], result["errors"])
        mutations = [call for call in fake.calls
                     if call[:2] in (["docker", "stop"], ["docker", "rm"])
                     or call[:3] == ["docker", "network", "rm"]]
        self.assertEqual(mutations[0][:3], ["docker", "stop", "-t"])
        self.assertIn("c" * 64, mutations[0])
        self.assertIn(["docker", "rm", "-f", "c" * 64], mutations)
        self.assertIn(["docker", "network", "rm", "e" * 64], mutations)
        self.assertFalse(any("prune" in call or "down" in call for call in fake.calls))

    def test_container_cleanup_failed_listing_is_not_empty(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest, fail_list=True)
        result = self.module.container_cleanup(manifest, exec_fn=fake, source="test")
        self.assertFalse(result["ok"])
        self.assertFalse(result["verifiedEmpty"])
        self.assertIsNone(result["containerIdsAfter"])
        self.assertIsNone(result["networkIdsAfter"])
        self.assertEqual([call for call in fake.calls if call[:2] == ["docker", "stop"]], [])

    def test_container_cleanup_endpoint_mismatch_performs_zero_mutations(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest, endpoint="other-daemon|bom|1.0")
        result = self.module.container_cleanup(manifest, exec_fn=fake, source="test")
        self.assertFalse(result["ok"])
        self.assertIsNone(result["containerIdsAfter"])
        self.assertFalse(any(call[:2] == ["docker", "stop"] or
                             call[:3] == ["docker", "rm", "-f"] or
                             call[:3] == ["docker", "network", "rm"]
                             for call in fake.calls))

    def test_container_env_uses_exact_image_id_and_recorded_docker_env(self):
        manifest = self._container_manifest()
        env = self.module.container_env(manifest)
        self.assertEqual(env["BOM_TEST_IMAGE"], IMAGE_ID)
        self.assertEqual(env["DOCKER_HOST"], "unix:///tmp/bom-test-docker.sock")

    def test_container_lists_request_untruncated_ids_for_exact_equality(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest)
        self.module.container_cleanup(manifest, exec_fn=fake, source="test")
        lists = [call for call in fake.calls
                 if call[:2] == ["docker", "ps"]
                 or call[:3] == ["docker", "network", "ls"]]
        self.assertTrue(lists)
        for call in lists:
            self.assertIn("--no-trunc", call)
        self.assertEqual(len("c" * 64), 64)
        self.assertEqual(len("e" * 64), 64)

    def test_container_cleanup_calls_all_docker_with_pinned_env(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest)
        self.module.container_cleanup(manifest, exec_fn=fake, source="test")
        docker_calls = [(call, env) for call, env in zip(fake.calls, fake.envs)
                        if call and call[0] == "docker"]
        self.assertTrue(docker_calls)
        for _call, env in docker_calls:
            self.assertIsNotNone(env)
            self.assertEqual(env.get("DOCKER_HOST"), "unix:///tmp/bom-test-docker.sock")

    def test_container_start_refuses_image_tag_drift_before_up(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest, image_id="sha256:" + "0" * 64)
        result = self.module.container_start(manifest, exec_fn=fake)
        self.assertFalse(result["ok"])
        self.assertIn("image tag moved", result["error"])
        self.assertFalse(any(call[:3] == ["docker", "compose", "up"]
                             for call in fake.calls))

    def test_container_cleanup_label_mismatch_performs_zero_mutations(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest, corrupt_labels=True)
        result = self.module.container_cleanup(manifest, exec_fn=fake, source="test")
        self.assertFalse(result["ok"])
        self.assertFalse(any(call[:2] == ["docker", "stop"] or
                             call[:3] == ["docker", "rm", "-f"] or
                             call[:3] == ["docker", "network", "rm"]
                             for call in fake.calls))

    def test_container_cleanup_image_mismatch_performs_zero_mutations(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest, image_id="sha256:" + "0" * 64)
        result = self.module.container_cleanup(manifest, exec_fn=fake, source="test")
        self.assertFalse(result["ok"])
        self.assertFalse(any(call[:2] == ["docker", "stop"] for call in fake.calls))

    def test_container_cleanup_reinspects_before_mutation(self):
        manifest = self._container_manifest()
        fake = FakeDocker(manifest)

        def flaky(argv, timeout=20.0, env=None, cwd=None):
            if argv[:2] == ["docker", "inspect"] or argv[:3] == ["docker", "container", "inspect"]:
                # First inventory inspects; later pre-mutation check is ambiguous.
                return {"ok": False, "returncode": 1, "stdout": "", "stderr": "unknown",
                        "timedOut": False}
            return fake(argv, timeout=timeout, env=env, cwd=cwd)

        result = self.module.container_cleanup(manifest, exec_fn=flaky, source="test")
        self.assertFalse(result["ok"])
        self.assertTrue(result.get("mutationAborted")
                        or result["inspection"]["ok"] is False)
        self.assertFalse(any(call[:2] == ["docker", "stop"] for call in fake.calls))

    # -- cache ancestry --------------------------------------------------

    def test_cache_requires_immutable_direct_child_of_recorded_root(self):
        self.assertTrue(self.module.cache_ancestry(self.manifest)["ok"])
        bad = dict(self.manifest, cacheDir=str(self.base / "somewhere-else"))
        self.assertFalse(self.module.cache_ancestry(bad)["ok"])
        bad = dict(self.manifest, cacheDir=str(pathlib.Path(self.manifest["cacheRoot"]) / "wrong-prefix"))
        self.assertFalse(self.module.cache_ancestry(bad)["ok"])

    def test_remove_cache_refuses_wrong_ancestry(self):
        bad = dict(self.manifest, cacheDir=str(self.base / "somewhere-else"))
        result = self.module.remove_cache(bad)
        self.assertFalse(result["removed"])
        self.assertIn("direct child", result["error"])

    def test_remove_cache_removes_verified_owned_cache(self):
        result = self.module.remove_cache(self.manifest)
        self.assertTrue(result["removed"], result)
        self.assertFalse(pathlib.Path(self.manifest["cacheDir"]).exists())

    # -- external terminal observer -------------------------------------

    def _write_finalization(self, manifest, identity, boundary, *, terminal=True,
                            cgroup_observation=None, destructive=True):
        previous = mock.patch.object(self.module, "cgroup2_mount_identity",
                                     return_value=boundary["cgroup2Mount"])
        previous_ns = mock.patch.object(self.module, "namespace_inode",
                                        side_effect=lambda name: manifest["binding"][
                                            "pidNamespace" if name == "pid" else "cgroupNamespace"])
        previous.start(); self.addCleanup(previous.stop)
        previous_ns.start(); self.addCleanup(previous_ns.stop)
        finalization = {
            "schemaVersion": self.module.SCHEMA_VERSION,
            "runnerVersion": self.module.RUNNER_VERSION,
            "runId": manifest["runId"],
            "unit": manifest["unit"],
            "invocationId": identity["invocationId"],
            "controlGroup": identity["controlGroup"],
            "provisional": {
                "binding": manifest["binding"],
                "invocationId": identity["invocationId"],
                "controlGroup": identity["controlGroup"],
                "terminalEvidence": {"ok": terminal, "activeState": "inactive",
                                     "invocationId": identity["invocationId"],
                                     "controlGroup": identity["controlGroup"]},
                "cgroupObservation": cgroup_observation or {
                    "ok": True, "exists": True, "empty": True,
                    "remainingPids": [], "selfPids": [4242]},
            },
            "cleanup": {"ok": terminal, "destructiveOk": destructive,
                        "verifiedEmpty": False, "source": "test"},
        }
        (self.run_dir / self.module.FINALIZATION_NAME).write_text(
            json.dumps(finalization))
        (self.run_dir / self.module.SUMMARY_NAME).write_text(json.dumps({
            "status": "cleanup_pending",
            "exit": {"runnerCode": 0},
        }))
        identity_path = self.run_dir / self.module.UNIT_IDENTITY_NAME
        identity_path.write_text(json.dumps(identity))
        boundary_path = self.run_dir / self.module.BOUNDARY_NAME
        boundary_path.write_text(json.dumps(boundary))

    def test_observer_verifies_populated_zero_after_finalizer(self):
        metadata = make_metadata(self.module, self.manifest, self.identity,
                                 active="inactive")
        with mock.patch.object(self.module, "systemd_show", return_value=metadata):
            root = self._cgroup(procs="", populated="0")
            self._write_finalization(self.manifest, self.identity, self.boundary)
            result = self.module.observe_completion(self.run_dir, cgroup_root=root)
        self.assertTrue(result["verified"], result)
        self.assertEqual(result["proofBasis"], "cgroup.events.populated=0")
        self.assertTrue(result["noLiveOwnedProcesses"])

    def test_observer_rejects_active_unit_even_with_old_terminal_evidence(self):
        metadata = make_metadata(self.module, self.manifest, self.identity,
                                 active="active")
        with mock.patch.object(self.module, "systemd_show", return_value=metadata):
            root = self._cgroup(procs="", populated="0")
            self._write_finalization(self.manifest, self.identity, self.boundary,
                                     terminal=True)
            result = self.module.observe_completion(self.run_dir, cgroup_root=root)
        self.assertFalse(result["verified"])
        self.assertEqual(result["proofBasis"], "unit-not-terminal")

    def test_observer_accepts_exact_enoent_with_stable_mount_and_parent(self):
        metadata = make_metadata(self.module, self.manifest, self.identity,
                                 active="inactive")
        with mock.patch.object(self.module, "systemd_show", return_value=metadata):
            root = self.base / "cgroup"
            path = root / self.identity["controlGroup"].lstrip("/")
            path.parent.mkdir(parents=True)
            boundary = make_boundary(
                self.module, self.manifest, self.identity,
                parent_stat=self.module.parent_identity(path))
            self._write_finalization(self.manifest, self.identity, boundary)
            result = self.module.observe_completion(self.run_dir, cgroup_root=root)
        self.assertTrue(result["verified"], result)
        self.assertEqual(result["proofBasis"],
                         "exact-directory-enoent-with-stable-mount-and-parent")
        self.assertTrue(result["noLiveOwnedProcesses"])

    def test_observer_rejects_enoent_without_stable_parent_identity(self):
        metadata = make_metadata(self.module, self.manifest, self.identity,
                                 active="inactive")
        with mock.patch.object(self.module, "systemd_show", return_value=metadata):
            root = self.base / "cgroup"
            path = root / self.identity["controlGroup"].lstrip("/")
            path.parent.mkdir(parents=True)
            boundary = make_boundary(
                self.module, self.manifest, self.identity,
                parent_stat={"exists": True, "isDir": True, "listable": True,
                             "dev": 999, "ino": 999})
            self._write_finalization(self.manifest, self.identity, boundary)
            result = self.module.observe_completion(self.run_dir, cgroup_root=root)
        self.assertFalse(result["verified"])
        self.assertFalse(result["noLiveOwnedProcesses"])

    # -- CLI, hold and source safety -------------------------------------

    def test_global_safety_hold_blocks_main_before_parser(self):
        hold = self.base / "SAFETY_HOLD"
        hold.write_text("hold")
        with mock.patch.object(self.module, "SAFETY_HOLD", hold):
            with mock.patch.object(self.module, "cmd_preflight") as command:
                self.assertEqual(self.module.main(["preflight"]), 3)
                command.assert_not_called()

    def test_no_local_fallback_or_arbitrary_unit_option(self):
        parser = self.module.build_parser()
        subparsers = [action for action in parser._actions
                      if isinstance(action, __import__("argparse")._SubParsersAction)]
        self.assertEqual(len(subparsers), 1)
        run_parser = subparsers[0].choices["run"]
        options = {option for action in run_parser._actions
                   for option in action.option_strings}
        for forbidden in ("--unit", "--durability", "--allow-unattended-local",
                          "--evidence-dir", "--protect-pid", "--build"):
            self.assertNotIn(forbidden, options)
        with self.assertRaises(self.module.PreflightError):
            self.module.require_capabilities(
                {"systemd": {"ok": False}, "cgroup2": True, "docker": {"ok": True}},
                "command", 30)

    def test_source_has_no_pid_or_process_group_signalling(self):
        import ast
        source = RUNNER.read_text(encoding="utf-8")
        tree = ast.parse(source)
        forbidden_calls: list[str] = []
        for node in ast.walk(tree):
            if not isinstance(node, ast.Call):
                continue
            func = node.func
            if isinstance(func, ast.Attribute) and isinstance(func.value, ast.Name):
                if func.value.id == "os" and func.attr in {"kill", "killpg"}:
                    forbidden_calls.append(f"os.{func.attr}")
                if func.value.id == "subprocess" and func.attr in {
                        "run", "Popen", "call", "check_call", "check_output"}:
                    for keyword in node.keywords:
                        if keyword.arg == "start_new_session":
                            forbidden_calls.append("start_new_session")
            if isinstance(func, ast.Name) and func.id == "pkill":
                forbidden_calls.append("pkill")
        self.assertEqual(forbidden_calls, [])
        self.assertNotIn("pidfd_send_signal", source)
        # /proc/self/cgroup is allowed only for the supervisor's own positive
        # equality proof; it is never a cleanup scope.
        self.assertEqual(source.count("/proc/self/cgroup"), 2)

    def test_compose_contract_has_exact_owner_labels_and_bounds(self):
        text = COMPOSE.read_text(encoding="utf-8")
        for expected in ("io.bom.test.owner-token", "io.bom.test.run-id",
                         "io.bom.test.runner-version", "restart: \"no\"",
                         "127.0.0.1", "pids_limit", "mem_limit", "pull_policy: never"):
            self.assertIn(expected, text)
        self.assertNotIn("container_name:", text)
        self.assertNotIn("ipc: host", text)
        self.assertNotIn("\n    build:", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
