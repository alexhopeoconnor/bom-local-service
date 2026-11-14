#!/usr/bin/env python3
"""Pure fixture tests for candidate preparation and promotion.

These tests do not build images, start containers, contact a registry or run a
service.  Docker command execution is exercised through a fake runner so the
exact promotion ordering can be asserted without side effects.
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any, Mapping
from unittest import mock

REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / "scripts"))

import candidate_release as release

try:
    import yaml
except ImportError:  # pragma: no cover - PyYAML is present in the local checker.
    yaml = None


def _sha(label: str) -> str:
    return hashlib.sha256(label.encode("utf-8")).hexdigest()


def _write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _make_base_candidate(
    directory: Path,
    version: str = "0.1.1",
    arches: tuple[str, ...] = release.ARCH_ORDER,
) -> dict[str, Any]:
    directory.mkdir(parents=True, exist_ok=True)
    _write(directory / "appsettings.json", '{"debug": false}\n')
    _write(directory / "qualification-policy.json", '{"qualified": true}\n')
    _write(directory / "scripts" / "verify-service.sh", "#!/usr/bin/env bash\nexit 0\n")
    scripts = {
        "scripts/verify-service.sh": {
            "sha256": release.sha256_file(
                directory / "scripts" / "verify-service.sh"
            )
        }
    }
    manifest = release.new_manifest(
        source_commit="a" * 40,
        dirty=False,
        version=version,
        configuration_path="effective-config.json",
        configuration_sha256=release.sha256_file(directory / "appsettings.json"),
        policy_path="qualification-policy.json",
        policy_sha256=release.sha256_file(directory / "qualification-policy.json"),
        scripts=scripts,
    )
    # The manifest names the copied configuration file; keep the fixture layout
    # identical to a real candidate artifact.
    (directory / "effective-config.json").write_bytes(
        (directory / "appsettings.json").read_bytes()
    )
    for arch in arches:
        archive = directory / f"candidate-{arch}.tar"
        archive.write_bytes(f"archive-{arch}-{version}".encode("utf-8"))
        release.record_image(
            manifest,
            arch=arch,
            image_id="sha256:" + _sha(f"image-{arch}"),
            archive=archive.name,
            archive_sha256=release.sha256_file(archive),
        )
    return manifest


def _add_all_evidence(
    manifest: dict[str, Any], directory: Path, *, version: str = "0.1.1"
) -> None:
    reports = directory / "incoming"
    checks = release.qualification_checks(
        release.DEFAULT_REQUIRED_ARCHITECTURES, manifest["images"].keys()
    )
    for check in checks:
        arch = None
        if check in release.NATIVE_CHECKS.values():
            arch = next(
                candidate_arch
                for candidate_arch, native_check in release.NATIVE_CHECKS.items()
                if native_check == check
            )
        image = manifest["images"].get(arch) if arch else None
        report = release.make_report(
            check=check,
            status="passed",
            source_commit=manifest["source"]["commit"],
            arch=arch,
            image_id=image["imageId"] if image else None,
            archive_sha256=image["archiveSha256"] if image else None,
            details={"fixture": version},
        )
        report_path = reports / f"{check}.json"
        release.write_json_atomic(report_path, report)
        release.record_evidence(
            manifest,
            check=check,
            status="passed",
            report_path=report_path,
            candidate_dir=directory,
            arch=arch,
        )


def _qualified_candidate(
    directory: Path,
    version: str = "0.1.1",
    arches: tuple[str, ...] = release.ARCH_ORDER,
) -> Path:
    manifest = _make_base_candidate(directory, version, arches)
    _add_all_evidence(manifest, directory, version=version)
    release.qualify_manifest(manifest)
    release.finalize_manifest(
        manifest,
        base_dir=directory,
        require_files=True,
        require_archives=True,
    )
    candidate_path = directory / "candidate.json"
    release._write_candidate(candidate_path, manifest)
    return candidate_path


class CandidateManifestTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name) / "candidate"

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_manifest_has_exact_schema_and_stable_identity(self) -> None:
        manifest = _make_base_candidate(self.directory)
        self.assertEqual(tuple(manifest), release.TOP_LEVEL_KEYS)
        self.assertTrue(manifest["candidateId"].startswith("cand-"))
        self.assertEqual(len(manifest["candidateId"]), len("cand-") + 32)
        expected = release.qualification_checks(
            release.DEFAULT_REQUIRED_ARCHITECTURES, manifest["images"].keys()
        )
        self.assertEqual(
            manifest["qualification"]["missingChecks"], list(expected)
        )
        self.assertEqual(manifest["source"]["dirty"], False)
        release.validate_manifest(manifest, base_dir=self.directory, require_files=True)

    def test_validation_rejects_unknown_top_level_key(self) -> None:
        manifest = _make_base_candidate(self.directory)
        manifest["extra"] = True
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest)

    def test_validation_rejects_configuration_hash_mismatch(self) -> None:
        manifest = _make_base_candidate(self.directory)
        (self.directory / "effective-config.json").write_text("tampered", encoding="utf-8")
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest, base_dir=self.directory, require_files=True)

    def test_validation_rejects_script_hash_mismatch(self) -> None:
        manifest = _make_base_candidate(self.directory)
        (self.directory / "scripts" / "verify-service.sh").write_text("tampered\n")
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest, base_dir=self.directory, require_files=True)

    def test_validation_rejects_archive_hash_mismatch(self) -> None:
        manifest = _make_base_candidate(self.directory)
        (self.directory / "candidate-amd64.tar").write_bytes(b"changed")
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest, base_dir=self.directory, require_archives=True)

    def test_validation_rejects_tampered_candidate_id(self) -> None:
        manifest = _make_base_candidate(self.directory)
        manifest["candidateId"] = "cand-" + "0" * 32
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest)

    def test_validation_rejects_inconsistent_missing_checks(self) -> None:
        manifest = _make_base_candidate(self.directory)
        manifest["qualification"]["missingChecks"] = []
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest)

    def test_validation_rejects_registry_digest_before_publication(self) -> None:
        manifest = _make_base_candidate(self.directory)
        manifest["images"]["amd64"]["registryDigest"] = "sha256:" + _sha("digest")
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest, require_unpublished=True)

    def test_validation_rejects_wrong_platform(self) -> None:
        manifest = _make_base_candidate(self.directory)
        manifest["images"]["amd64"]["platform"] = "linux/arm64"
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest)

    def test_merge_is_idempotent_but_rejects_conflict(self) -> None:
        manifest = _make_base_candidate(self.directory)
        fragment = release.make_fragment("amd64", manifest["images"]["amd64"])
        release.merge_fragment(manifest, fragment)
        release.merge_fragment(manifest, fragment)
        conflicting = json.loads(json.dumps(fragment))
        conflicting["image"]["imageId"] = "sha256:" + _sha("other")
        with self.assertRaises(release.CandidateError):
            release.merge_fragment(manifest, conflicting)

    def test_evidence_report_must_match_source_and_image(self) -> None:
        manifest = _make_base_candidate(self.directory)
        image = manifest["images"]["amd64"]
        report = release.make_report(
            check="native-amd64",
            status="passed",
            source_commit="b" * 40,
            arch="amd64",
            image_id=image["imageId"],
            archive_sha256=image["archiveSha256"],
        )
        report_path = self.directory / "incoming" / "native-amd64.json"
        release.write_json_atomic(report_path, report)
        with self.assertRaises(release.CandidateError):
            release.record_evidence(
                manifest,
                check="native-amd64",
                status="passed",
                report_path=report_path,
                candidate_dir=self.directory,
                arch="amd64",
            )

    def test_native_evidence_requires_image_binding(self) -> None:
        manifest = _make_base_candidate(self.directory)
        report = release.make_report(
            check="native-amd64",
            status="passed",
            source_commit=manifest["source"]["commit"],
            arch="amd64",
            image_id=manifest["images"]["amd64"]["imageId"],
            archive_sha256=manifest["images"]["amd64"]["archiveSha256"],
        )
        report_path = self.directory / "incoming" / "native-amd64.json"
        release.write_json_atomic(report_path, report)
        release.record_evidence(
            manifest,
            check="native-amd64",
            status="passed",
            report_path=report_path,
            candidate_dir=self.directory,
            arch="amd64",
        )
        manifest["evidence"]["native-amd64"].pop("archiveSha256")
        with self.assertRaises(release.CandidateError):
            release.validate_manifest(manifest, base_dir=self.directory, require_files=True)

    def test_live_evidence_binds_architecture_without_report_image_fields(self) -> None:
        manifest = _make_base_candidate(self.directory)
        report = release.make_report(
            check="live-pomona",
            status="passed",
            source_commit=manifest["source"]["commit"],
            arch="amd64",
            details={"verificationReportSha256": _sha("report")},
        )
        report_path = self.directory / "incoming" / "live-pomona.json"
        release.write_json_atomic(report_path, report)
        release.record_evidence(
            manifest,
            check="live-pomona",
            status="passed",
            report_path=report_path,
            candidate_dir=self.directory,
            arch="amd64",
        )
        entry = manifest["evidence"]["live-pomona"]
        self.assertEqual(entry["arch"], "amd64")
        self.assertEqual(entry["imageId"], manifest["images"]["amd64"]["imageId"])

    def test_qualification_requires_step18_reports(self) -> None:
        manifest = _make_base_candidate(self.directory)
        checks = set(
            release.qualification_checks(
                release.DEFAULT_REQUIRED_ARCHITECTURES, manifest["images"].keys()
            )
        )
        self.assertIn(release.CHECK_FAULT_RECOVERY, checks)
        self.assertIn(release.CHECK_UPGRADE_ROLLBACK, checks)

    def test_qualify_refuses_unresolved_policy_criteria(self) -> None:
        manifest = _make_base_candidate(self.directory)
        _add_all_evidence(manifest, self.directory)
        policy = self.directory / "qualification-policy.json"
        release.write_json_atomic(
            policy,
            {
                "qualified": False,
                "unresolvedCriteria": ["browser process count baseline"],
            },
        )
        with self.assertRaises(release.CandidateError) as context:
            release.qualify_manifest(manifest, policy_path=policy)
        self.assertIn("unresolved", str(context.exception))
        self.assertEqual(manifest["qualification"]["status"], "unqualified")

    def test_qualified_manifest_rejects_unresolved_policy(self) -> None:
        candidate = _qualified_candidate(self.directory)
        manifest = release.load_manifest(candidate, base_dir=self.directory)
        release.write_json_atomic(
            self.directory / "qualification-policy.json",
            {"qualified": False, "unresolvedCriteria": ["sustained memory growth"]},
        )
        with self.assertRaises(release.CandidateError) as context:
            release.validate_manifest(manifest, base_dir=self.directory)
        self.assertIn("unresolved", str(context.exception))

    def test_qualification_requires_every_check(self) -> None:
        manifest = _make_base_candidate(self.directory)
        with self.assertRaises(release.CandidateError) as context:
            release.qualify_manifest(manifest)
        self.assertIn("deterministic", str(context.exception))
        _add_all_evidence(manifest, self.directory)
        release.qualify_manifest(manifest)
        self.assertEqual(manifest["qualification"]["status"], "qualified")
        self.assertEqual(manifest["qualification"]["missingChecks"], [])

    def test_failed_evidence_remains_missing(self) -> None:
        manifest = _make_base_candidate(self.directory)
        report = release.make_report(
            check="deterministic",
            status="failed",
            source_commit=manifest["source"]["commit"],
        )
        report_path = self.directory / "incoming" / "deterministic.json"
        release.write_json_atomic(report_path, report)
        release.record_evidence(
            manifest,
            check="deterministic",
            status="failed",
            report_path=report_path,
            candidate_dir=self.directory,
        )
        self.assertIn("deterministic", manifest["qualification"]["missingChecks"])
        with self.assertRaises(release.CandidateError):
            release.qualify_manifest(manifest)

    def test_qualified_candidate_passes_strict_verification(self) -> None:
        candidate = _qualified_candidate(self.directory)
        manifest = release.load_manifest(
            candidate,
            base_dir=self.directory,
            require_files=True,
            require_archives=True,
            require_all_arches=True,
            require_qualified=True,
            require_unpublished=True,
        )
        self.assertEqual(manifest["qualification"]["missingChecks"], [])

    def test_qualification_resets_after_evidence_mutation(self) -> None:
        candidate = _qualified_candidate(self.directory)
        manifest = release.load_manifest(candidate, base_dir=self.directory)
        report = self.directory / "reports" / "deterministic.json"
        release.record_evidence(
            manifest,
            check="deterministic",
            status="passed",
            report_path=report,
            candidate_dir=self.directory,
        )
        self.assertEqual(manifest["qualification"]["status"], "unqualified")
        self.assertEqual(manifest["qualification"]["missingChecks"], [])


class VersionAndPublicationTests(unittest.TestCase):
    def test_semver_ordering_and_stability(self) -> None:
        self.assertLess(release.semver_key("0.1.1"), release.semver_key("0.1.2"))
        self.assertLess(release.semver_key("0.9.9"), release.semver_key("1.0.0"))
        self.assertLess(
            release.semver_key("1.0.0-rc.1"), release.semver_key("1.0.0")
        )
        self.assertTrue(release.is_stable_version("0.1.1"))
        self.assertFalse(release.is_stable_version("0.1.1-rc.1"))

    def test_tag_must_equal_v_project_version(self) -> None:
        release.assert_tag_matches("v0.1.1", "0.1.1")
        with self.assertRaises(release.CandidateError):
            release.assert_tag_matches("0.1.1", "0.1.1")
        with self.assertRaises(release.CandidateError):
            release.assert_tag_matches("v0.1.2", "0.1.1")

    def test_latest_requires_explicit_stable_advance(self) -> None:
        self.assertEqual(
            release.latest_decision("0.1.0", "0.1.1", requested=True), "allowed"
        )
        self.assertEqual(
            release.latest_decision(None, "0.1.1", requested=True), "allowed"
        )
        self.assertEqual(
            release.latest_decision("0.1.0", "0.1.1", requested=False), "not-requested"
        )
        with self.assertRaises(release.CandidateError):
            release.latest_decision("0.1.1", "0.1.1", requested=True)
        with self.assertRaises(release.CandidateError):
            release.latest_decision("0.2.0", "0.1.1", requested=True)
        with self.assertRaises(release.CandidateError):
            release.latest_decision(None, "0.1.1-rc.1", requested=True)

    def test_push_digest_parser_is_strict(self) -> None:
        digest = "sha256:" + _sha("push")
        self.assertEqual(
            release.parse_push_digest(f"latest: digest: {digest} size: 12"), digest
        )
        with self.assertRaises(release.CandidateError):
            release.parse_push_digest("no digest here")
        with self.assertRaises(release.CandidateError):
            release.parse_push_digest(
                f"digest: {digest}\ndigest: sha256:{_sha('other')}"
            )

    def test_latest_annotation_parser(self) -> None:
        raw = json.dumps(
            {
                "schemaVersion": 2,
                "annotations": {"org.opencontainers.image.version": "0.1.1"},
            }
        )
        self.assertEqual(release.parse_latest_version_from_manifest(raw), "0.1.1")
        self.assertIsNone(release.parse_latest_version_from_manifest("{}"))
        with self.assertRaises(release.CandidateError):
            release.parse_latest_version_from_manifest("not json")


class PromotionPlanTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name) / "candidate"
        self.candidate = _qualified_candidate(self.directory)
        self.manifest = release.load_manifest(
            self.candidate,
            base_dir=self.directory,
            require_files=True,
            require_archives=True,
            require_all_arches=True,
            require_qualified=True,
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _plan(self, *, update_latest: bool = False, current: str | None = None) -> dict[str, Any]:
        return release.build_promotion_plan(
            self.manifest,
            tag="v0.1.1",
            registry="ghcr.io",
            repository="example/service",
            update_latest=update_latest,
            current_latest_version=current,
        )

    def test_plan_promotes_exact_image_ids_and_digests(self) -> None:
        plan = self._plan(update_latest=True, current="0.1.0")
        actions = [step["action"] for step in plan["steps"]]
        self.assertEqual(
            actions,
            [
                "load-archive",
                "verify-image",
                "load-archive",
                "verify-image",
                "push-image",
                "push-image",
                "create-index",
                "inspect-index",
                "create-index",
                "inspect-index",
            ],
        )
        self.assertNotIn("build", actions)
        pushes = [step for step in plan["steps"] if step["action"] == "push-image"]
        self.assertEqual(
            {step["sourceImageId"] for step in pushes},
            {
                self.manifest["images"]["amd64"]["imageId"],
                self.manifest["images"]["arm64"]["imageId"],
            },
        )
        release_index = plan["steps"][6]
        latest_index = plan["steps"][8]
        actions_before_push = [
            index
            for index, step in enumerate(plan["steps"])
            if step["action"] == "push-image"
        ]
        self.assertGreater(min(actions_before_push), 3)
        self.assertEqual(
            release_index["target"], "ghcr.io/example/service:v0.1.1"
        )
        self.assertEqual(latest_index["target"], "ghcr.io/example/service:latest")
        self.assertEqual(
            release_index["annotations"]["org.opencontainers.image.version"], "0.1.1"
        )
        self.assertEqual(plan["serialization"]["scope"], "repository")
        self.assertTrue(plan["serialization"]["required"])
        for step in (release_index, latest_index):
            self.assertEqual(
                step["sources"],
                [
                    {"arch": "amd64", "digestFrom": "amd64"},
                    {"arch": "arm64", "digestFrom": "arm64"},
                ],
            )

    def test_plan_refuses_backwards_or_equal_latest(self) -> None:
        with self.assertRaises(release.CandidateError):
            self._plan(update_latest=True, current="0.2.0")
        with self.assertRaises(release.CandidateError):
            self._plan(update_latest=True, current="0.1.1")

    def test_plan_can_publish_version_without_moving_latest(self) -> None:
        plan = self._plan(update_latest=False)
        self.assertFalse(plan["updateLatest"])
        self.assertEqual(plan["latestDecision"], "not-requested")
        self.assertFalse(
            any(
                step.get("target", "").endswith(":latest")
                for step in plan["steps"]
            )
        )

    def test_amd64_only_candidate_promotes_only_tested_scope(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "amd64-candidate"
            candidate = _qualified_candidate(directory, arches=("amd64",))
            manifest = release.load_manifest(
                candidate,
                base_dir=directory,
                require_files=True,
                require_archives=True,
                require_all_arches=True,
                require_qualified=True,
            )
            plan = release.build_promotion_plan(
                manifest,
                tag="v0.1.1",
                registry="ghcr.io",
                repository="example/service",
                update_latest=False,
                current_latest_version=None,
            )
            self.assertEqual(plan["arches"], ["amd64"])
            self.assertEqual(
                [step["action"] for step in plan["steps"]],
                [
                    "load-archive",
                    "verify-image",
                    "push-image",
                    "create-index",
                    "inspect-index",
                ],
            )
            self.assertEqual(
                plan["steps"][3]["sources"],
                [{"arch": "amd64", "digestFrom": "amd64"}],
            )


class PolicyScopeTests(unittest.TestCase):
    def test_committed_policy_scopes_qualification_to_amd64(self) -> None:
        policy = REPO_ROOT / ".github" / "qualification-policy.json"
        if not policy.is_file():
            self.skipTest("coordinator policy has not been committed yet")
        scope = release.required_arches_from_policy(policy)
        self.assertEqual(scope, ("amd64",))
        document = release.load_json(policy)
        self.assertIn("arm64", document.get("deferredArchitectures", []))

    def test_qualification_checks_add_deferred_arch_only_when_image_present(self) -> None:
        amd64 = release.qualification_checks(("amd64",))
        both = release.qualification_checks(("amd64",), ("amd64", "arm64"))
        self.assertNotIn("native-arm64", amd64)
        self.assertIn("native-arm64", both)


class FakeRunner:
    def __init__(
        self,
        platforms: Mapping[str, str],
        latest_raw: str | None = None,
        existing_targets: set[str] | None = None,
    ):
        self.platforms = dict(platforms)
        self.latest_raw = latest_raw
        self.existing_targets = existing_targets or set()
        self.calls: list[list[str]] = []
        self.raw_by_target: dict[str, str] = {}

    def run_raw(self, args: list[str], *, check: bool = True) -> subprocess.CompletedProcess[bytes]:
        completed = self.run(args, check=check)
        return subprocess.CompletedProcess(
            completed.args, completed.returncode, completed.stdout.encode("utf-8"), completed.stderr.encode("utf-8")
        )

    def run(self, args: list[str], *, check: bool = True) -> subprocess.CompletedProcess[str]:
        self.calls.append(list(args))
        if args[:2] == ["docker", "load"]:
            return subprocess.CompletedProcess(args, 0, "", "")
        if args[:3] == ["docker", "image", "inspect"]:
            image_id = args[5]
            return subprocess.CompletedProcess(
                args, 0, f"{image_id}|{self.platforms[image_id]}", ""
            )
        if args[:2] == ["docker", "tag"]:
            return subprocess.CompletedProcess(args, 0, "", "")
        if args[:2] == ["docker", "push"]:
            target = args[2]
            digest = "sha256:" + _sha("push-" + target)
            return subprocess.CompletedProcess(
                args, 0, f"latest: digest: {digest} size: 10", ""
            )
        if args[:4] == ["docker", "buildx", "imagetools", "create"]:
            metadata = Path(args[args.index("--metadata-file") + 1])
            target = args[args.index("--tag") + 1]
            raw = json.dumps(
                {"schemaVersion": 2, "target": target}, separators=(",", ":")
            )
            self.raw_by_target[target] = raw
            digest = "sha256:" + hashlib.sha256(raw.encode("utf-8")).hexdigest()
            metadata.write_text(
                json.dumps({"containerimage.descriptor": {"digest": digest}}),
                encoding="utf-8",
            )
            return subprocess.CompletedProcess(args, 0, "", "")
        if args[:4] == ["docker", "buildx", "imagetools", "inspect"]:
            target = args[5]
            if target in self.raw_by_target:
                return subprocess.CompletedProcess(args, 0, self.raw_by_target[target], "")
            if target in self.existing_targets:
                return subprocess.CompletedProcess(args, 0, "{}", "")
            if target.endswith(":latest") and self.latest_raw is not None:
                return subprocess.CompletedProcess(args, 0, self.latest_raw, "")
            return subprocess.CompletedProcess(args, 1, "", "manifest unknown")
        raise AssertionError(f"unexpected command {args}")


class PromotionExecutionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name) / "candidate"
        self.candidate = _qualified_candidate(self.directory)
        self.manifest = release.load_manifest(
            self.candidate,
            base_dir=self.directory,
            require_files=True,
            require_archives=True,
            require_all_arches=True,
            require_qualified=True,
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_execute_uses_pushed_digests_for_indexes_and_never_builds(self) -> None:
        latest_raw = json.dumps(
            {
                "schemaVersion": 2,
                "annotations": {"org.opencontainers.image.version": "0.1.0"},
            }
        )
        runner = FakeRunner(
            {
                image["imageId"]: image["platform"]
                for image in self.manifest["images"].values()
            },
            latest_raw=latest_raw,
        )
        plan = release.build_promotion_plan(
            self.manifest,
            tag="v0.1.1",
            registry="ghcr.io",
            repository="example/service",
            update_latest=True,
            current_latest_version="0.1.0",
        )
        result = release.execute_promotion(
            plan,
            archive_dirs={"amd64": self.directory, "arm64": self.directory},
            runner=runner,
            result_path=self.directory / "promotion-result.json",
        )
        self.assertEqual(result["releaseIndex"]["tag"], "ghcr.io/example/service:v0.1.1")
        self.assertTrue(result["latest"]["updated"])
        flat = [" ".join(call) for call in runner.calls]
        self.assertFalse(any("docker build " in command or "buildx build" in command for command in flat))
        create_calls = [call for call in runner.calls if call[:3] == ["docker", "buildx", "imagetools"] and "create" in call]
        self.assertEqual(len(create_calls), 2)
        for call in create_calls:
            sources = [part for part in call if "@sha256:" in part]
            self.assertEqual(len(sources), 2)

    def test_execute_refuses_to_replace_an_existing_release_index(self) -> None:
        runner = FakeRunner(
            {
                image["imageId"]: image["platform"]
                for image in self.manifest["images"].values()
            },
            existing_targets={"ghcr.io/example/service:v0.1.1"},
        )
        plan = release.build_promotion_plan(
            self.manifest,
            tag="v0.1.1",
            registry="ghcr.io",
            repository="example/service",
            update_latest=False,
            current_latest_version=None,
        )
        with self.assertRaises(release.CandidateError):
            release.execute_promotion(
                plan,
                archive_dirs={"amd64": self.directory, "arm64": self.directory},
                runner=runner,
            )
        self.assertFalse(
            any(call[:2] == ["docker", "tag"] for call in runner.calls)
        )

    def test_execute_refuses_changed_latest(self) -> None:
        latest_raw = json.dumps(
            {
                "schemaVersion": 2,
                "annotations": {"org.opencontainers.image.version": "0.1.0"},
            }
        )
        runner = FakeRunner(
            {
                image["imageId"]: image["platform"]
                for image in self.manifest["images"].values()
            },
            latest_raw=latest_raw,
        )
        plan = release.build_promotion_plan(
            self.manifest,
            tag="v0.1.1",
            registry="ghcr.io",
            repository="example/service",
            update_latest=True,
            current_latest_version="0.0.9",
        )
        with self.assertRaises(release.CandidateError):
            release.execute_promotion(
                plan,
                archive_dirs={"amd64": self.directory, "arm64": self.directory},
                runner=runner,
            )


class PublicationAuthorizationTests(unittest.TestCase):
    def test_promotion_execute_requires_a_publication_lease(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "candidate"
            candidate = _qualified_candidate(directory, arches=("amd64",))
            environment = os.environ.copy()
            environment.pop("GITHUB_ACTIONS", None)
            environment.pop("BOM_PUBLICATION_LEASE", None)
            completed = subprocess.run(
                [
                    sys.executable,
                    str(REPO_ROOT / "scripts" / "promote-candidate.py"),
                    "--candidate",
                    str(candidate),
                    "--candidate-dir",
                    str(directory),
                    "--archive-dir",
                    f"amd64={directory}",
                    "--tag",
                    "v0.1.1",
                    "--repository",
                    "example/service",
                    "--authorize",
                    "v0.1.1",
                    "--execute",
                ],
                cwd=REPO_ROOT,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )
            self.assertEqual(completed.returncode, 1)
            self.assertIn("BOM_PUBLICATION_LEASE", completed.stderr)


class InitializationTests(unittest.TestCase):
    def test_initialize_copies_identity_files_and_records_scripts(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary) / "repo"
            repo.mkdir()
            _write(repo / "appsettings.json", '{"a": 1}\n')
            _write(repo / "policy.json", '{"qualified": false}\n')
            _write(repo / "scripts" / "verify-service.sh", "#!/usr/bin/env bash\n")

            # initialize_candidate reads, but never writes, git state.  Mock the
            # two read-only git calls so this test is pure and never makes an
            # unsigned commit or depends on a host signing setup.
            def fake_git(args: Any, **_kwargs: Any) -> subprocess.CompletedProcess[str]:
                self.assertIsInstance(args, list)
                self.assertEqual(args[:2], ["git", "-C"])
                if args[3] == "rev-parse":
                    return subprocess.CompletedProcess(args, 0, "a" * 40 + "\n", "")
                if args[3] == "status":
                    return subprocess.CompletedProcess(args, 0, "", "")
                raise AssertionError(f"unexpected git arguments: {args}")

            with mock.patch.object(release.subprocess, "run", side_effect=fake_git):
                manifest, candidate = release.initialize_candidate(
                    source_dir=repo,
                    source_commit=None,
                    allow_dirty=False,
                    version="0.1.1",
                    csproj=None,
                    configuration=repo / "appsettings.json",
                    policy=repo / "policy.json",
                    scripts=[repo / "scripts" / "verify-service.sh"],
                    output_dir=repo / "artifacts" / "candidate",
                )
            self.assertFalse(manifest["source"]["dirty"])
            self.assertEqual(manifest["source"]["commit"], "a" * 40)
            self.assertTrue((candidate.parent / "effective-config.json").is_file())
            self.assertTrue((candidate.parent / "qualification-policy.json").is_file())
            self.assertTrue((candidate.parent / "scripts" / "verify-service.sh").is_file())
            self.assertEqual(
                tuple(release.load_json(candidate)), release.TOP_LEVEL_KEYS
            )

    def test_record_image_execution_requires_a_lease(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            environment = os.environ.copy()
            environment.pop("GITHUB_ACTIONS", None)
            environment.pop("BOM_CANDIDATE_LEASE", None)
            completed = subprocess.run(
                [
                    sys.executable,
                    str(REPO_ROOT / "scripts" / "prepare-candidate.py"),
                    "record-image",
                    "--arch",
                    "amd64",
                    "--image-ref",
                    "example:missing",
                    "--archive",
                    str(Path(temporary) / "candidate-amd64.tar"),
                    "--fragment",
                    str(Path(temporary) / "fragment.json"),
                    "--execute",
                ],
                cwd=REPO_ROOT,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )
            self.assertEqual(completed.returncode, 1)
            self.assertIn("BOM_CANDIDATE_LEASE", completed.stderr)

    def test_record_image_plan_does_not_execute_docker(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fragment = Path(temporary) / "fragment.json"
            completed = subprocess.run(
                [
                    sys.executable,
                    str(REPO_ROOT / "scripts" / "prepare-candidate.py"),
                    "record-image",
                    "--arch",
                    "amd64",
                    "--image-ref",
                    "bom-local-service:candidate-amd64",
                    "--archive",
                    str(Path(temporary) / "candidate-amd64.tar"),
                    "--fragment",
                    str(fragment),
                ],
                cwd=REPO_ROOT,
                text=True,
                capture_output=True,
                check=False,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr)
            payload = json.loads(completed.stdout)
            self.assertFalse(payload["execute"])
            flattened = " ".join(" ".join(command) for command in payload["commands"])
            self.assertIn("docker save", flattened)
            self.assertFalse(fragment.exists())


class SchemaAlignmentTests(unittest.TestCase):
    def test_candidate_schema_is_readable_by_service_verification(self) -> None:
        import service_verification

        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "candidate"
            manifest = _make_base_candidate(directory, arches=("amd64",))
            candidate = directory / "candidate.json"
            release._write_candidate(candidate, manifest)
            image = manifest["images"]["amd64"]
            identity = service_verification.load_candidate_identity(
                str(candidate),
                architecture="amd64",
                image_id=image["imageId"],
                image_archive=str(directory / image["archive"]),
                repo_root=REPO_ROOT,
            )
            self.assertEqual(identity["missing"], [])
            self.assertEqual(identity["conflicts"], [])
            self.assertTrue(identity["identityComplete"])

    def test_record_report_uses_the_embedded_evidence_fields(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / "candidate"
            manifest = _make_base_candidate(directory, arches=("amd64",))
            report = release.make_report(
                check="deterministic",
                status="passed",
                source_commit=manifest["source"]["commit"],
                details={"fixture": "record-report"},
            )
            report_path = directory / "incoming" / "deterministic.json"
            release.write_json_atomic(report_path, report)
            release.record_report(
                manifest,
                report_path=report_path,
                candidate_dir=directory,
            )
            self.assertEqual(manifest["evidence"]["deterministic"]["status"], "passed")


class PackagingAuditTests(unittest.TestCase):
    def test_python_suites_are_excluded_from_publish_and_context(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _write(root / "scripts/test_service_runner.py", "# test fixture\n")
            for mode in ("publish", "context"):
                with self.subTest(mode=mode):
                    self.assertTrue(release.packaging_violations(root, mode=mode))

    def test_publish_audit_flags_local_credentials_and_state(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _write(root / "BomLocalService.dll", "binary")
            _write(root / "appsettings.json", "{}")
            _write(root / ".env.example", "SAFE=1\n")
            for relative in (
                "appsettings.Local.json",
                "appsettings.Development.local.json",
                ".env",
                ".env.production",
                "cache/state.json",
                "artifacts/report.json",
                "tests/example.cs",
                "__pycache__/example.pyc",
                "packages.lock.json",
                "global.json",
                "BomLocalService.csproj",
            ):
                _write(root / relative, "sentinel")
            violations = release.packaging_violations(root, mode="publish")
            flattened = "\n".join(violations)
            for expected in (
                "appsettings.Local.json",
                "appsettings.Development.local.json",
                ".env:",
                ".env.production",
                "cache:",
                "artifacts:",
                "tests:",
                "__pycache__:",
                "packages.lock.json",
                "global.json",
                "BomLocalService.csproj",
            ):
                self.assertIn(expected, flattened)
            self.assertNotIn(".env.example", flattened)
            self.assertNotIn("appsettings.json:", flattened)

    def test_context_audit_allows_restore_inputs_but_not_overrides(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _write(root / "packages.lock.json", "{}")
            _write(root / "global.json", "{}")
            _write(root / "BomLocalService.csproj", "<Project />")
            _write(root / "appsettings.Local.json", "{}")
            violations = release.packaging_violations(root, mode="context")
            flattened = "\n".join(violations)
            self.assertIn("appsettings.Local.json", flattened)
            self.assertNotIn("packages.lock.json", flattened)
            self.assertNotIn("global.json", flattened)
            self.assertNotIn("BomLocalService.csproj", flattened)

    def test_nested_playwright_bin_is_allowed_in_publish_tree(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _write(root / ".playwright" / "package" / "bin" / "install.sh", "#!/bin/sh\n")
            self.assertEqual(release.packaging_violations(root, mode="publish"), [])
            self.assertIn(
                ".playwright/package/bin",
                "\n".join(release.packaging_violations(root, mode="context")),
            )

    def test_symlinks_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "publish"
            root.mkdir()
            target = Path(temporary) / "outside.txt"
            target.write_text("outside", encoding="utf-8")
            (root / "linked.txt").symlink_to(target)
            violations = release.packaging_violations(root, mode="publish")
            self.assertTrue(any("symbolic link" in item for item in violations))

    def test_audit_cli_exit_code_reports_violations(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            _write(root / "appsettings.Local.json", "{}")
            completed = subprocess.run(
                [
                    sys.executable,
                    str(REPO_ROOT / "scripts" / "prepare-candidate.py"),
                    "audit-tree",
                    "--root",
                    str(root),
                    "--mode",
                    "publish",
                ],
                cwd=REPO_ROOT,
                text=True,
                capture_output=True,
                check=False,
            )
            self.assertEqual(completed.returncode, 1)
            payload = json.loads(completed.stdout)
            self.assertFalse(payload["clean"])
            self.assertIn("appsettings.Local.json", "\n".join(payload["violations"]))


def _trigger(document: Mapping[str, Any]) -> Mapping[str, Any]:
    return document.get("on", document.get(True, {}))


@unittest.skipUnless(yaml is not None, "PyYAML is required for workflow structure checks")
class WorkflowStructureTests(unittest.TestCase):
    def _workflow(self, name: str) -> Mapping[str, Any]:
        return yaml.safe_load((REPO_ROOT / ".github" / "workflows" / name).read_text())

    @staticmethod
    def _runs(job: Mapping[str, Any]) -> str:
        return "\n".join(
            str(step.get("run", ""))
            for step in job.get("steps", [])
        )

    def test_service_checks_separate_deterministic_native_and_candidate(self) -> None:
        document = self._workflow("service-checks.yml")
        jobs = document["jobs"]
        for job in jobs.values():
            self.assertIn("timeout-minutes", job)

        deterministic = jobs["deterministic"]
        deterministic_text = self._runs(deterministic)
        self.assertIn("Category!=Browser", deterministic_text)
        self.assertIn("--locked-mode", deterministic_text)
        self.assertNotIn("--base-url", deterministic_text)
        self.assertNotIn("BOM_", deterministic_text)
        self.assertIn("artifacts/publish", deterministic_text)
        self.assertIn("audit-tree", deterministic_text)
        self.assertIn("test_candidate_release.py", deterministic_text)
        self.assertIn("test_service_verification.py", deterministic_text)
        self.assertIn("test_service_runner.py", deterministic_text)
        self.assertIn("--vulnerable", deterministic_text)
        self.assertIn("dependencyAudit", deterministic_text)
        deterministic_json = json.dumps(jobs["deterministic"])
        self.assertIn("MSBUILDDISABLENODEREUSE", deterministic_json)
        self.assertIn("DOTNET_CLI_USE_MSBUILD_SERVER", deterministic_json)
        self.assertIn("UseSharedCompilation=false", deterministic_text)
        self.assertIn("--disable-build-servers", deterministic_text)
        self.assertNotIn("build-server shutdown", deterministic_text)

        plan_text = self._runs(jobs["plan"])
        self.assertIn("amd64", plan_text)
        self.assertIn("arm64", plan_text)
        self.assertIn("BOM_INCLUDE_DEFERRED_ARM64", json.dumps(jobs["plan"]))
        self.assertIn("BOM_DEDICATED_RUNNER_LABELS", json.dumps(jobs["plan"]))
        self.assertIn("runner_labels", json.dumps(jobs["plan"]))
        self.assertIn("self-hosted", plan_text)
        self.assertIn("BOM_RUNNER_APPROVED", json.dumps(jobs["native"]))
        self.assertIn("BOM_RUNNER_APPROVED", json.dumps(jobs["plan"]))
        image = jobs["image"]
        self.assertIn("fromJSON", str(image["strategy"]["matrix"]))
        image_text = self._runs(image)
        self.assertIn("docker buildx build", image_text)
        self.assertIn("--load", image_text)
        self.assertNotIn("--push", image_text)
        self.assertIn("record-image", image_text)
        self.assertIn("--execute", image_text)

        self.assertNotIn("browser", jobs)
        native_text = self._runs(jobs["native"])
        self.assertIn("docker load --input", native_text)
        self.assertIn("docker image inspect", native_text)
        self.assertIn("run-service-test.sh preflight", native_text)
        self.assertIn("run-service-test.sh run", native_text)
        self.assertIn("--mode container", native_text)
        self.assertIn("--test-command", native_text)
        self.assertIn("check-candidate-image.py", native_text)
        self.assertIn("Category=Browser", native_text)
        self.assertIn("BOM_DEMO_BASE_URL", native_text)
        self.assertIn("BOM_REAL_BROWSER_TESTS=1", native_text)
        self.assertIn("xvfb-run -a", native_text)
        self.assertIn("run-service-test.sh wait", native_text)
        self.assertIn("run-service-test.sh cleanup", native_text)
        self.assertIn("runnerRunId", native_text)
        self.assertIn("MSBUILDDISABLENODEREUSE", json.dumps(jobs["native"]))
        self.assertIn("DOTNET_CLI_USE_MSBUILD_SERVER", json.dumps(jobs["native"]))
        self.assertIn("UseSharedCompilation=false", native_text)
        self.assertIn("--disable-build-servers", native_text)
        self.assertNotIn("build-server shutdown", native_text)
        self.assertNotIn("docker run", native_text)
        self.assertNotIn("docker rm -f", native_text)
        self.assertNotIn("docker image rm", native_text)
        self.assertNotIn("trap cleanup EXIT", native_text)

        candidate_text = self._runs(jobs["candidate"])
        self.assertIn("merge", candidate_text)
        self.assertIn("record-evidence", candidate_text)
        self.assertIn("finalize", candidate_text)
        self.assertIn("verify", candidate_text)
        self.assertIn("candidate-manifest", json.dumps(jobs["candidate"]))
        self.assertIn("BOM_INCLUDE_DEFERRED_ARM64", json.dumps(jobs["candidate"]))

        self.assertNotIn("docker push", image_text)
        self.assertNotIn("ghcr.io", self._runs(jobs["deterministic"]))

    def test_publication_workflow_only_promotes_downloaded_candidates(self) -> None:
        document = self._workflow("docker-build-release.yml")
        triggers = _trigger(document)
        self.assertEqual(set(triggers), {"workflow_dispatch"})
        self.assertEqual(document["concurrency"]["group"], "image-promotion")
        self.assertFalse(document["concurrency"]["cancel-in-progress"])
        text = "\n".join(self._runs(job) for job in document["jobs"].values())
        self.assertIn("promote-candidate.py", text)
        self.assertIn("--execute", text)
        self.assertIn("--require-qualified", text)
        self.assertIn("v$version", text)
        self.assertNotIn("docker build ", text)
        self.assertNotIn("buildx build", text)
        inputs = triggers["workflow_dispatch"]["inputs"]
        self.assertIn("update_latest", inputs)
        self.assertIn("confirm", inputs)
        self.assertIn("candidate_run_id", inputs)

    def test_live_workflow_consumes_an_exact_candidate_archive(self) -> None:
        document = self._workflow("live-bom-verification.yml")
        triggers = _trigger(document)
        self.assertEqual(set(triggers), {"workflow_dispatch"})
        self.assertIn("candidate_run_id", triggers["workflow_dispatch"]["inputs"])
        self.assertNotIn("browser_base_url", triggers["workflow_dispatch"]["inputs"])
        text = "\n".join(self._runs(job) for job in document["jobs"].values())
        self.assertIn("download-artifact", json.dumps(document))
        self.assertIn("run-id", json.dumps(document))
        self.assertIn("docker load --input", text)
        self.assertIn("run-service-test.sh preflight", text)
        self.assertIn("run-service-test.sh run", text)
        self.assertIn("--mode container", text)
        self.assertIn("--test-command", text)
        self.assertIn("run-service-test.sh wait", text)
        self.assertIn("run-service-test.sh cleanup", text)
        self.assertIn("record-report", text)
        self.assertIn("runnerRunId", text)
        self.assertIn("BOM_RUNNER_APPROVED", json.dumps(document["jobs"]["live"]))
        self.assertNotIn("trap cleanup EXIT", text)
        self.assertNotIn("docker run", text)
        self.assertNotIn("docker rm -f", text)
        self.assertNotIn("docker image rm", text)
        self.assertNotIn("docker build", text)

    def test_dockerfile_audits_context_before_restore(self) -> None:
        dockerfile = (REPO_ROOT / "Dockerfile").read_text()
        self.assertIn("AS context", dockerfile)
        self.assertIn("find /context", dockerfile)
        self.assertIn("COPY --from=context", dockerfile)
        self.assertIn("global.json", dockerfile)
        self.assertIn("--locked-mode", dockerfile)
        self.assertNotIn("find /src -type f", dockerfile)
        context_position = dockerfile.index("AS context")
        restore_position = dockerfile.index("--locked-mode")
        self.assertLess(context_position, restore_position)

    def test_dockerfile_carries_build_identity_labels(self) -> None:
        dockerfile = (REPO_ROOT / "Dockerfile").read_text()
        self.assertIn("ARG VERSION", dockerfile)
        self.assertIn("ARG SOURCE_REVISION", dockerfile)
        self.assertIn("org.opencontainers.image.version", dockerfile)
        self.assertIn("org.opencontainers.image.revision", dockerfile)

    def test_dockerfile_guards_packaging_sentinels(self) -> None:
        dockerfile = (REPO_ROOT / "Dockerfile").read_text()
        self.assertIn("forbidden build-context file", dockerfile)
        self.assertIn("forbidden publish file", dockerfile)
        self.assertIn("appsettings.*.local.json", dockerfile)
        self.assertIn("packages.lock.json", dockerfile)

    def test_dockerfile_disables_shared_build_servers(self) -> None:
        dockerfile = (REPO_ROOT / "Dockerfile").read_text()
        for token in (
            "MSBUILDDISABLENODEREUSE=1",
            "DOTNET_CLI_USE_MSBUILD_SERVER=0",
            "--disable-build-servers",
            "UseSharedCompilation=false",
        ):
            self.assertIn(token, dockerfile)
        self.assertNotIn("build-server shutdown", dockerfile)


if __name__ == "__main__":
    unittest.main(verbosity=2)
