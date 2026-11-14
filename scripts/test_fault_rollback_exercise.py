#!/usr/bin/env python3
"""Pure mocked regression tests for scripts/fault_rollback_exercise.py.

These tests never start a process, container, service, browser or Docker
command. They exercise only pure validation, status, manifest, tree-copy and
evidence-gating helpers. The coordinator runtime gate remains authoritative.
"""

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "scripts" / "fault_rollback_exercise.py"
RUN_ID = "20261115T120000Z-deadbeef"
TOKEN = "b" * 64
IMAGE = "sha256:" + "c" * 64
CONTAINER = "a" * 64


def load_module():
    spec = importlib.util.spec_from_file_location("bom_fault_exercise", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def iso(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def make_manifest(module, cache_dir: pathlib.Path) -> dict:
    return {
        "runId": RUN_ID,
        "ownerToken": TOKEN,
        "mode": "container",
        "cacheOwned": True,
        "cacheDir": str(cache_dir),
        "network": {"host": "127.0.0.1", "port": 12345},
        "container": {"imageId": IMAGE, "project": "bom-test-project"},
        "runnerVersion": "2.0.0",
    }


class PureFaultExerciseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.m = load_module()
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name)

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_checks_negative_required_check_cannot_pass(self) -> None:
        checks = self.m.Checks("fault")
        checks.record("observation", False)
        self.assertEqual(checks.required_failures(), [])
        with self.assertRaises(self.m.ExerciseError):
            checks.require("required", False, "expected")
        self.assertEqual(checks.required_failures(), ["required"])

    def test_checks_optional_false_is_not_a_pass_blocker_but_is_marked(self) -> None:
        checks = self.m.Checks("fault")
        checks.record("optional", False, {"why": "observed"}, required=False)
        self.assertEqual(checks.required_failures(), [])
        self.assertEqual(checks.items[0]["required"], False)

    def test_direct_required_false_blocks_pass(self) -> None:
        checks = self.m.Checks("fault")
        checks.record("restoreBeforeAdmission", False, {"restoreFound": False}, required=True)
        self.assertEqual(checks.required_failures(), ["restoreBeforeAdmission"])
        checks.record("optionalObservation", False, {"why": "observed"})
        self.assertEqual(checks.required_failures(), ["restoreBeforeAdmission"])

    def test_status_value_accepts_camel_and_pascal(self) -> None:
        self.assertEqual(self.m.status_value({"updateFailed": True}, "updateFailed"), True)
        self.assertEqual(self.m.status_value({"UpdateFailed": True}, "updateFailed"), True)
        self.assertIsNone(self.m.status_value(None, "updateFailed"))

    def test_failure_predicate_requires_future_backoff_and_idle(self) -> None:
        now = datetime.now(timezone.utc)
        status = {
            "updateFailed": True,
            "isUpdating": False,
            "errorCode": "SCRAPING_ERROR",
            "consecutiveFailures": 1,
            "nextRetryAt": iso(now + timedelta(seconds=60)),
            "lastSuccessfulCaptureAt": iso(now - timedelta(minutes=2)),
        }
        self.assertTrue(self.m.failure_predicate(status))
        status["nextRetryAt"] = iso(now - timedelta(seconds=30))
        self.assertFalse(self.m.failure_predicate(status))
        status["nextRetryAt"] = iso(now + timedelta(seconds=60))
        status["isUpdating"] = True
        self.assertFalse(self.m.failure_predicate(status))

    def test_refresh_response_fresh_noop_200_is_not_accepted(self) -> None:
        response = {
            "status": 200,
            "body": {
                "updateAccepted": False,
                "updateTriggered": False,
                "isUpdating": False,
                "cacheIsValid": True,
                "message": "Cache is valid, no update needed",
            },
        }
        outcome = self.m.refresh_response_outcome(response)
        self.assertFalse(outcome["accepted"])
        self.assertIsNone(outcome["rejectionCode"])

    def test_refresh_response_queued_or_running_is_accepted(self) -> None:
        accepted = {
            "status": 200,
            "body": {
                "updateAccepted": True,
                "updateTriggered": True,
                "isUpdating": True,
                "cacheIsValid": False,
            },
        }
        already_running = {
            "status": 200,
            "body": {
                "updateAccepted": False,
                "updateTriggered": False,
                "isUpdating": True,
                "cacheIsValid": True,
                "message": "Cache update is in progress",
            },
        }
        rejected = {
            "status": 429,
            "body": {
                "updateAccepted": False,
                "updateTriggered": False,
                "isUpdating": False,
                "rejectionCode": "RejectedBackoff",
            },
        }
        self.assertTrue(self.m.refresh_response_outcome(accepted)["accepted"])
        self.assertTrue(self.m.refresh_response_outcome(already_running)["accepted"])
        self.assertFalse(self.m.refresh_response_outcome(rejected)["accepted"])

    def test_refresh_window_requires_stale_or_updating_and_respects_backoff(self) -> None:
        now = datetime.now(timezone.utc)
        fresh = {
            "cacheIsValid": True,
            "isUpdating": False,
            "nextRetryAt": None,
        }
        stale = {
            "cacheIsValid": False,
            "isUpdating": False,
            "nextRetryAt": None,
        }
        updating = {
            "cacheIsValid": True,
            "isUpdating": True,
            "nextRetryAt": None,
        }
        backoff = {
            "cacheIsValid": False,
            "isUpdating": False,
            "nextRetryAt": iso(now + timedelta(seconds=60)),
        }
        self.assertFalse(self.m.refresh_window_ready(fresh))
        self.assertTrue(self.m.refresh_window_ready(stale))
        self.assertTrue(self.m.refresh_window_ready(updating))
        self.assertFalse(self.m.refresh_window_ready(backoff))

    def test_wait_for_refresh_window_records_stale_wait(self) -> None:
        now = datetime.now(timezone.utc)
        fresh = {"cacheIsValid": True, "isUpdating": False, "lastSuccessfulCaptureAt": iso(now)}
        stale = {"cacheIsValid": False, "isUpdating": False, "lastSuccessfulCaptureAt": iso(now)}
        sequence = iter([fresh, stale])
        ticks = iter(range(100))
        status, detail = self.m.wait_for_refresh_window(
            "http://127.0.0.1:12345",
            "Pomona",
            "QLD",
            timeout=10,
            fetcher=lambda _base, _suburb, _state: next(sequence),
            clock=lambda: next(ticks),
            sleeper=lambda _seconds: None,
        )
        self.assertEqual(status, stale)
        self.assertEqual(detail["reason"], "stale")
        self.assertEqual(detail["polls"], 2)

    def test_wait_for_refresh_window_stops_at_deadline(self) -> None:
        now = datetime.now(timezone.utc)
        fresh = {"cacheIsValid": True, "isUpdating": False, "lastSuccessfulCaptureAt": iso(now)}
        ticks = iter(range(1000))
        with self.assertRaises(self.m.ExerciseError):
            self.m.wait_for_refresh_window(
                "http://127.0.0.1:12345",
                "Pomona",
                "QLD",
                timeout=2,
                fetcher=lambda _base, _suburb, _state: fresh,
                clock=lambda: next(ticks),
                sleeper=lambda _seconds: None,
            )

    def test_success_newer_than_rejects_old_and_equal_success(self) -> None:
        baseline = datetime.now(timezone.utc)
        base_status = {
            "updateFailed": False,
            "isUpdating": False,
            "consecutiveFailures": 0,
            "nextRetryAt": None,
        }
        older = {**base_status, "lastSuccessfulCaptureAt": iso(baseline - timedelta(seconds=1))}
        equal = {**base_status, "lastSuccessfulCaptureAt": iso(baseline)}
        newer = {**base_status, "lastSuccessfulCaptureAt": iso(baseline + timedelta(seconds=1))}
        self.assertFalse(self.m.success_newer_than(older, baseline))
        self.assertFalse(self.m.success_newer_than(equal, baseline))
        self.assertTrue(self.m.success_newer_than(newer, baseline))

    def test_acceptance_evidence_with_datetimes_is_json_serializable(self) -> None:
        now = datetime.now(timezone.utc)
        acceptance = {
            "accepted": True,
            "baselineSuccess": now,
            "baselineAttempt": now + timedelta(seconds=1),
            "status": {"lastSuccessfulCaptureAt": iso(now)},
        }
        checks = self.m.Checks("baseline")
        checks.record("baseline.refreshAccepted", True, acceptance)
        target = self.root / "evidence.json"
        self.m.write_json_exclusive(target, {"checks": checks.items})
        saved = json.loads(target.read_text(encoding="utf-8"))
        detail = saved["checks"][0]["detail"]
        self.assertEqual(detail["baselineSuccess"], iso(now))
        self.assertEqual(detail["baselineAttempt"], iso(now + timedelta(seconds=1)))
        self.assertIsInstance(acceptance["baselineSuccess"], datetime)

    def test_failure_requires_new_attempt_after_baseline(self) -> None:
        now = datetime.now(timezone.utc)
        status = {
            "updateFailed": True,
            "isUpdating": False,
            "errorCode": "TIMEOUT_ERROR",
            "consecutiveFailures": 1,
            "nextRetryAt": iso(now + timedelta(seconds=60)),
            "lastUpdateAttempt": iso(now),
        }
        self.assertTrue(
            self.m.expected_attempt_completed(
                status,
                baseline_success=None,
                baseline_attempt=now - timedelta(seconds=1),
                expected_failure=True,
            )
        )
        self.assertFalse(
            self.m.expected_attempt_completed(
                status,
                baseline_success=None,
                baseline_attempt=now,
                expected_failure=True,
            )
        )

    def test_timeout_and_success_predicates_are_distinct(self) -> None:
        now = datetime.now(timezone.utc)
        timeout = {
            "updateFailed": True,
            "isUpdating": False,
            "errorCode": "TIMEOUT_ERROR",
            "consecutiveFailures": 1,
            "nextRetryAt": iso(now + timedelta(seconds=300)),
        }
        self.assertTrue(self.m.timeout_predicate(timeout))
        self.assertFalse(self.m.success_predicate(timeout))
        success = {
            "updateFailed": False,
            "isUpdating": False,
            "consecutiveFailures": 0,
            "nextRetryAt": None,
            "lastSuccessfulCaptureAt": iso(now),
        }
        self.assertTrue(self.m.success_predicate(success))
        self.assertFalse(self.m.timeout_predicate(success))

    def test_recovery_window_boundary(self) -> None:
        start = datetime.now(timezone.utc)
        self.assertTrue(self.m.recovery_window(start, start + timedelta(seconds=1800))["ok"])
        self.assertFalse(self.m.recovery_window(start, start + timedelta(seconds=1801))["ok"])

    def test_manifest_scope_accepts_exact_binding(self) -> None:
        cache = self.root / "cache"
        cache.mkdir()
        manifest = make_manifest(self.m, cache)
        errors = self.m.validate_manifest_scope(
            manifest,
            run_id=RUN_ID,
            token=TOKEN,
            cache_env=str(cache),
            base_url="http://127.0.0.1:12345",
        )
        self.assertEqual(errors, [])

    def test_manifest_scope_rejects_foreign_token_and_mode(self) -> None:
        cache = self.root / "cache"
        cache.mkdir()
        manifest = make_manifest(self.m, cache)
        manifest["mode"] = "native"
        errors = self.m.validate_manifest_scope(
            manifest,
            run_id=RUN_ID,
            token=TOKEN,
            cache_env=str(cache),
            base_url="http://127.0.0.1:12345",
        )
        self.assertTrue(any("container" in item for item in errors))
        errors = self.m.validate_manifest_scope(
            manifest,
            run_id=RUN_ID,
            token="d" * 64,
            cache_env=str(cache),
            base_url="http://127.0.0.1:12345",
        )
        self.assertTrue(any("token" in item for item in errors))

    def test_container_identity_accepts_exact_labels_and_id(self) -> None:
        cache = self.root / "cache"
        cache.mkdir()
        manifest = make_manifest(self.m, cache)
        labels = {
            self.m.cc.RUN_LABEL: RUN_ID,
            self.m.cc.TOKEN_LABEL: TOKEN,
            self.m.cc.PROJECT_LABEL: "bom-test-project",
            "io.bom.test.runner-version": "2.0.0",
            "io.bom.test.role": "service",
        }
        inspected = {
            "Id": CONTAINER,
            "Image": IMAGE,
            "Config": {"Labels": labels},
            "State": {"Running": True},
        }
        errors = self.m.validate_container_identity(
            CONTAINER,
            inspected,
            manifest,
            run_id=RUN_ID,
            token=TOKEN,
            require_running=True,
        )
        self.assertEqual(errors, [])

    def test_container_identity_rejects_short_id_wrong_role_and_stopped(self) -> None:
        cache = self.root / "cache"
        cache.mkdir()
        manifest = make_manifest(self.m, cache)
        labels = {
            self.m.cc.RUN_LABEL: RUN_ID,
            self.m.cc.TOKEN_LABEL: TOKEN,
            self.m.cc.PROJECT_LABEL: "bom-test-project",
            "io.bom.test.runner-version": "2.0.0",
            "io.bom.test.role": "helper",
        }
        inspected = {
            "Id": "a" * 12,
            "Image": IMAGE,
            "Config": {"Labels": labels},
            "State": {"Running": False},
        }
        errors = self.m.validate_container_identity(
            "a" * 12,
            inspected,
            manifest,
            run_id=RUN_ID,
            token=TOKEN,
            require_running=True,
        )
        self.assertTrue(any("64-hex" in item for item in errors))
        self.assertTrue(any("role" in item for item in errors))
        self.assertTrue(any("not running" in item for item in errors))

    def test_tree_manifest_detects_tamper(self) -> None:
        tree = self.root / "tree"
        (tree / "radar").mkdir(parents=True)
        (tree / "radar" / "frame.png").write_bytes(b"one")
        manifest = self.m.tree_manifest(tree)
        self.m.verify_tree_manifest(tree, manifest, "tree")
        (tree / "radar" / "frame.png").write_bytes(b"two")
        with self.assertRaises(self.m.ExerciseError):
            self.m.verify_tree_manifest(tree, manifest, "tree")

    def test_copy_tree_verified_preserves_hash_and_refuses_symlink(self) -> None:
        source = self.root / "source"
        source.mkdir()
        (source / "a.txt").write_text("alpha", encoding="utf-8")
        destination = self.root / "destination"
        copied = self.m.copy_tree_verified(source, destination)
        self.m.verify_tree_manifest(destination, copied, "copy")
        try:
            (source / "link").symlink_to(source / "a.txt")
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable")
        with self.assertRaises(self.m.UnsafeState):
            self.m.copy_tree_verified(source, self.root / "destination2")

    def test_copy_tree_into_empty_existing_cache(self) -> None:
        source = self.root / "source"
        source.mkdir()
        (source / "a.txt").write_text("alpha", encoding="utf-8")
        destination = self.root / "cache"
        destination.mkdir()
        copied = self.m.copy_tree_into_empty(source, destination)
        self.m.verify_tree_manifest(destination, copied, "cache copy")
        with self.assertRaises(self.m.ExerciseError):
            self.m.copy_tree_into_empty(source, destination)

    def test_clear_directory_contents_refuses_symlink(self) -> None:
        cache = self.root / "cache"
        cache.mkdir()
        (cache / "file.txt").write_text("x", encoding="utf-8")
        self.m.clear_directory_contents(cache)
        self.assertEqual(list(cache.iterdir()), [])
        try:
            (cache / "link").symlink_to(cache)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable")
        with self.assertRaises(self.m.UnsafeState):
            self.m.clear_directory_contents(cache)

    def test_fault_server_script_and_mode_validation(self) -> None:
        script = self.m.build_fault_server_script(19099)
        self.assertIn("/__health", script)
        self.assertIn("/__mode", script)
        self.assertIn("503", script)
        self.assertIn("302", script)
        self.assertIn("https://www.bom.gov.au/", script)
        self.assertEqual(self.m.validate_fault_mode("redirect"), "redirect")
        with self.assertRaises(self.m.ExerciseError):
            self.m.validate_fault_mode("crash")

    def test_legacy_folder_name_is_old_shape(self) -> None:
        when = datetime(2026, 11, 15, 12, 0, 0, tzinfo=timezone.utc)
        self.assertEqual(
            self.m.legacy_folder_name("Pomona", "QLD", when),
            "Pomona_QLD_20261115_120000",
        )

    def test_old_response_without_pinned_folder_uses_expected_folder(self) -> None:
        png = self.m.cc.png_2x2(1, 2, 3)
        body = {
            "observationTime": "2026-11-15T12:00:00Z",
            "frames": [{"imageUrl": "/api/radar/Pomona/QLD/frame/0"}],
        }
        with mock.patch.object(self.m.sv, "fetch_bounded", return_value=(200, png, {})):
            result = self.m.validate_radar_compat(
                "http://127.0.0.1:12345",
                body,
                suburb="Pomona",
                state="QLD",
                expected_folder="Pomona_QLD_20261115_120000",
            )
        self.assertEqual(result["cacheFolder"], "Pomona_QLD_20261115_120000")
        self.assertEqual(result["frames"], 1)

    def test_staging_and_incomplete_folder_detection(self) -> None:
        cache = self.root / "cache"
        (cache / ".staging" / "Pomona_QLD_20261115_120000").mkdir(parents=True)
        (cache / ".staging" / "not-a-dir.txt").write_text("x", encoding="utf-8")
        self.assertEqual(self.m.staging_entries(cache), ["Pomona_QLD_20261115_120000"])
        (cache / "Pomona_QLD_20261114_120000").mkdir()
        (cache / "Sydney_NSW_20261114_120000").mkdir()
        (cache / "Sydney_NSW_20261114_120000" / ".complete").write_text("{}", encoding="utf-8")
        self.assertEqual(self.m.incomplete_folders(cache), ["Pomona_QLD_20261114_120000"])

    def test_state_files_returns_empty_for_service_file(self) -> None:
        cache = self.root / "cache"
        cache.mkdir()
        (cache / ".service").write_text("unavailable", encoding="utf-8")
        self.assertEqual(self.m.state_files(cache), [])

    def test_state_file_hash_and_identity_lookup(self) -> None:
        cache = self.root / "cache"
        locations = cache / ".service" / "locations"
        locations.mkdir(parents=True)
        state = {"Suburb": "Pomona", "State": "QLD", "UpdateFailed": False}
        (locations / "abc.json").write_text(json.dumps(state), encoding="utf-8")
        entries = self.m.state_files(cache)
        self.assertEqual(len(entries), 1)
        self.assertEqual(len(entries[0]["sha256"]), 64)
        self.assertEqual(self.m.state_file_for(cache, "Pomona")["name"], "abc.json")
        self.assertIsNone(self.m.state_file_for(cache, "Sydney"))

    def _data_env(self, experiment: str = "exp-1") -> tuple[pathlib.Path, pathlib.Path, dict]:
        root = self.root
        base = root / "artifacts" / "execution" / "fault-rollback" / "data"
        base.mkdir(parents=True, exist_ok=True)
        directory = base / experiment
        directory.mkdir()
        (directory / self.m.OWNER_MARKER_NAME).write_text(
            json.dumps({"schema": self.m.OWNER_MARKER_SCHEMA, "experimentId": experiment}),
            encoding="utf-8",
        )
        return root, base, {"BOM_EXERCISE_DATA_DIR": str(directory)}

    def test_data_root_requires_existing_marked_experiment_directory(self) -> None:
        root, base, env = self._data_env()
        with mock.patch.object(self.m, "ROOT", root), mock.patch.object(
            self.m, "DATA_ROOT_BASE", base
        ), mock.patch.dict(os.environ, env, clear=False):
            self.assertEqual(self.m.data_root(), pathlib.Path(env["BOM_EXERCISE_DATA_DIR"]).resolve())
        with mock.patch.object(self.m, "ROOT", root), mock.patch.object(
            self.m, "DATA_ROOT_BASE", base
        ), mock.patch.dict(os.environ, {}, clear=False):
            with self.assertRaises(self.m.UnsafeState):
                self.m.data_root()

    def test_data_root_rejects_relative_outside_missing_and_symlink(self) -> None:
        root, base, env = self._data_env()
        with mock.patch.object(self.m, "ROOT", root), mock.patch.object(
            self.m, "DATA_ROOT_BASE", base
        ):
            with mock.patch.dict(os.environ, {"BOM_EXERCISE_DATA_DIR": "relative/path"}, clear=False):
                with self.assertRaises(self.m.UnsafeState):
                    self.m.data_root()
            with mock.patch.dict(os.environ, {"BOM_EXERCISE_DATA_DIR": str(root / "outside")}, clear=False):
                with self.assertRaises(self.m.UnsafeState):
                    self.m.data_root()
            with mock.patch.dict(os.environ, {"BOM_EXERCISE_DATA_DIR": str(base / "missing")}, clear=False):
                with self.assertRaises(self.m.UnsafeState):
                    self.m.data_root()
            real = base / "real"
            real.mkdir()
            (real / self.m.OWNER_MARKER_NAME).write_text(
                json.dumps({"schema": self.m.OWNER_MARKER_SCHEMA}), encoding="utf-8"
            )
            link = base / "link"
            try:
                link.symlink_to(real, target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest("symlinks unavailable")
            with mock.patch.dict(os.environ, {"BOM_EXERCISE_DATA_DIR": str(link)}, clear=False):
                with self.assertRaises(self.m.UnsafeState):
                    self.m.data_root()

    def test_data_root_rejects_missing_or_wrong_marker(self) -> None:
        root, base, env = self._data_env("exp-marker")
        directory = pathlib.Path(env["BOM_EXERCISE_DATA_DIR"])
        (directory / self.m.OWNER_MARKER_NAME).unlink()
        with mock.patch.object(self.m, "ROOT", root), mock.patch.object(
            self.m, "DATA_ROOT_BASE", base
        ), mock.patch.dict(os.environ, env, clear=False):
            with self.assertRaises(self.m.UnsafeState):
                self.m.data_root()
        (directory / self.m.OWNER_MARKER_NAME).write_text(
            json.dumps({"schema": "wrong"}), encoding="utf-8"
        )
        with mock.patch.object(self.m, "ROOT", root), mock.patch.object(
            self.m, "DATA_ROOT_BASE", base
        ), mock.patch.dict(os.environ, env, clear=False):
            with self.assertRaises(self.m.UnsafeState):
                self.m.data_root()

    def test_exclusive_manifest_write_refuses_existing(self) -> None:
        target = self.root / "manifest.json"
        self.m.write_json_exclusive(target, {"one": 1})
        with self.assertRaises(self.m.ExerciseError):
            self.m.write_json_exclusive(target, {"two": 2})
        self.assertEqual(json.loads(target.read_text(encoding="utf-8")), {"one": 1})

    def test_old_image_identity_stage_default(self) -> None:
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("BOM_EXERCISE_OLD_IMAGE", None)
            self.assertEqual(
                self.m.expected_image_for_stage("rollback", {}),
                self.m.OLD_BASELINE_IMAGE,
            )
            self.assertIsNone(self.m.expected_image_for_stage("upgrade-candidate", {}))

    def test_error_value_accepts_both_casings(self) -> None:
        self.assertEqual(self.m.error_value({"errorCode": "STORAGE_ERROR"}, "errorCode"), "STORAGE_ERROR")
        self.assertEqual(self.m.error_value({"ErrorCode": "STORAGE_ERROR"}, "errorCode"), "STORAGE_ERROR")


if __name__ == "__main__":
    unittest.main(verbosity=2)
