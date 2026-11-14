#!/usr/bin/env python3
"""Shared candidate preparation and promotion logic.

The candidate manifest is the single identity for tested image content.  This
module deliberately contains no build steps: preparation records images that a
native runner has already exported, and promotion loads those archives, verifies
their recorded identities and pushes the exact image IDs.  Publication never
rebuilds.

Only standard-library modules are used so the helpers run on a bare runner.
Docker and registry access live in the small execution layer at the bottom of
the module and are never used by the pure validation/planning functions.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any, Iterable, Mapping, Sequence

SCHEMA_VERSION = 1
CANDIDATE_ID_PREFIX = "cand-"

ARCH_PLATFORMS = {"amd64": "linux/amd64", "arm64": "linux/arm64"}
ARCH_ORDER = ("amd64", "arm64")

CHECK_DETERMINISTIC = "deterministic"
CHECK_FAULT_RECOVERY = "fault-recovery"
CHECK_UPGRADE_ROLLBACK = "upgrade-rollback"
NATIVE_CHECKS = {"amd64": "native-amd64", "arm64": "native-arm64"}
BASE_CHECKS = (
    CHECK_DETERMINISTIC,
    "browser",
    "soak",
    "live-pomona",
    "live-sydney",
    CHECK_FAULT_RECOVERY,
    CHECK_UPGRADE_ROLLBACK,
)
# Candidate policy v1 scopes qualification to amd64.  arm64 remains buildable
# and recordable, but it is an additional image: if a candidate includes it, its
# native evidence becomes required before the candidate can qualify.
DEFAULT_REQUIRED_ARCHITECTURES = ("amd64",)
POSSIBLE_CHECKS = (
    CHECK_DETERMINISTIC,
    NATIVE_CHECKS["amd64"],
    NATIVE_CHECKS["arm64"],
    "browser",
    "soak",
    "live-pomona",
    "live-sydney",
    CHECK_FAULT_RECOVERY,
    CHECK_UPGRADE_ROLLBACK,
)
ALL_CHECKS = POSSIBLE_CHECKS
EVIDENCE_STATUSES = ("passed", "failed", "unverified")


def required_arches_from_policy(policy_path: Path) -> tuple[str, ...]:
    """Read the coordinator-owned architecture scope from policy v1."""
    if not policy_path.is_file():
        return DEFAULT_REQUIRED_ARCHITECTURES
    document = load_json(policy_path)
    if not isinstance(document, dict):
        raise CandidateError(f"qualification policy {policy_path} must be an object")
    required = document.get("requiredArchitectures", list(DEFAULT_REQUIRED_ARCHITECTURES))
    deferred = document.get("deferredArchitectures", [])
    if not isinstance(required, list) or not required:
        raise CandidateError("policy requiredArchitectures must be a non-empty list")
    if not isinstance(deferred, list):
        raise CandidateError("policy deferredArchitectures must be a list")
    if set(required) & set(deferred):
        raise CandidateError("policy architecture scopes overlap")
    for arch in [*required, *deferred]:
        if arch not in ARCH_PLATFORMS:
            raise CandidateError(f"policy names unsupported architecture {arch!r}")
    return tuple(arch for arch in ARCH_ORDER if arch in required)


def unresolved_policy_criteria(policy_path: Path) -> tuple[str, ...]:
    """Return policy criteria that still block qualification.

    ``qualified: false`` and any ``unresolvedCriteria`` entry are blockers.  A
    candidate may still be prepared and evidenced while the policy is open; it
    must not be marked qualified.
    """
    if not policy_path.is_file():
        raise CandidateError(f"qualification policy not found: {policy_path}")
    document = load_json(policy_path)
    if not isinstance(document, dict):
        raise CandidateError(f"qualification policy {policy_path} must be an object")
    unresolved = document.get("unresolvedCriteria", [])
    if not isinstance(unresolved, list):
        raise CandidateError("policy unresolvedCriteria must be a list")
    blockers: list[str] = []
    for item in unresolved:
        if not isinstance(item, str) or not item.strip():
            raise CandidateError(
                "policy unresolvedCriteria entries must be non-empty strings"
            )
        blockers.append(item)
    if document.get("qualified") is False:
        blockers.append("policy.qualified is false")
    return tuple(blockers)


def qualification_checks(
    required_arches: Iterable[str] = DEFAULT_REQUIRED_ARCHITECTURES,
    image_arches: Iterable[str] = (),
) -> tuple[str, ...]:
    arches = [arch for arch in ARCH_ORDER if arch in set(required_arches)]
    for arch in ARCH_ORDER:
        if arch in set(image_arches) and arch not in arches:
            arches.append(arch)
    checks: list[str] = [CHECK_DETERMINISTIC]
    checks.extend(NATIVE_CHECKS[arch] for arch in arches)
    checks.extend(
        check for check in BASE_CHECKS if check != CHECK_DETERMINISTIC
    )
    return tuple(checks)

TOP_LEVEL_KEYS = (
    "schemaVersion",
    "candidateId",
    "source",
    "version",
    "configuration",
    "policy",
    "scripts",
    "images",
    "evidence",
    "qualification",
)
SOURCE_KEYS = ("commit", "dirty")
CONFIGURATION_KEYS = ("path", "sha256")
POLICY_KEYS = ("path", "sha256")
# service_verification.py reads scripts as {path: {"sha256": digest}}.  The
# candidate schema uses the same shape so one candidate.json is valid input to
# both the release tooling and the verification report.
SCRIPT_KEYS = ("sha256",)
IMAGE_KEYS = ("platform", "imageId", "archive", "archiveSha256", "registryDigest")
QUALIFICATION_KEYS = ("status", "missingChecks")

CONFIGURATION_NAME = "effective-config.json"
POLICY_NAME = "qualification-policy.json"
REPORTS_DIR = "reports"

PACKAGING_MODES = ("publish", "context")
# Names that must never be published or copied into a Docker build context.
PACKAGING_FORBIDDEN_ALWAYS_DIRECTORIES = frozenset(
    {
        ".git",
        ".aws",
        ".azure",
        ".ssh",
        ".kube",
        ".config",
        ".docker",
        ".agents",
        ".codex",
        ".claude",
        ".cursor",
        ".vs",
        ".vscode",
        ".idea",
        ".pytest_cache",
        ".mypy_cache",
        "node_modules",
        "__pycache__",
        "secrets",
    }
)
# Build/test/state directories are forbidden anywhere in a Docker context.  In
# a publish tree they are only forbidden at the top level: the Playwright
# runtime legitimately ships a nested ``package/bin`` directory.
PACKAGING_BUILD_DIRECTORIES = frozenset(
    {
        "cache",
        "artifacts",
        "tests",
        "bin",
        "obj",
        "testresults",
        "test-results",
        "verification-output",
    }
)
PACKAGING_FORBIDDEN_NAMES = frozenset(
    {
        ".dockerignore",
        ".env",
        ".git-credentials",
        ".gitignore",
        ".mcp.json",
        ".netrc",
        ".pypirc",
        "_netrc",
        "appsettings.local.json",
        "appsettings.overrides.json",
        "credentials.json",
        "secrets.dev.yaml",
        "service-account.json",
        "values.dev.yaml",
    }
)
PACKAGING_FORBIDDEN_SUFFIXES = (
    ".coverage",
    ".coveragexml",
    ".key",
    ".kdbx",
    ".p12",
    ".pem",
    ".pfx",
    ".pyc",
    ".pyo",
    ".swp",
    ".temp",
    ".tmp",
    ".trx",
    "~",
)
PACKAGING_PUBLISH_ONLY_NAMES = frozenset(
    {
        "docker-compose.test.yml",
        "docker-compose.yml",
        "dockerfile",
        "global.json",
        "packages.lock.json",
    }
)
_LOCAL_OVERRIDE_RE = re.compile(r"^appsettings\..*\.local\.json$")

_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
_COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")
_IMAGE_ID_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
_VERSION_RE = re.compile(
    r"^(?P<major>0|[1-9]\d*)\.(?P<minor>0|[1-9]\d*)\.(?P<patch>0|[1-9]\d*)"
    r"(?:-(?P<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$"
)


class CandidateError(Exception):
    """A deterministic, user-facing refusal or validation failure."""


# ---------------------------------------------------------------------------
# Canonical encoding and hashing
# ---------------------------------------------------------------------------


def canonical_bytes(value: Any) -> bytes:
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        + "\n"
    ).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def scripts_identity(scripts: Mapping[str, Any]) -> str:
    """Hash the script path/digest mapping independently of JSON decoration."""
    digests = {
        path: script_digest(entry, f"scripts.{path}")
        for path, entry in scripts.items()
    }
    return sha256_bytes(canonical_bytes(dict(sorted(digests.items()))))


def candidate_identity(
    source_commit: str,
    dirty: bool,
    version: str,
    configuration_path: str,
    configuration_sha256: str,
    policy_path: str,
    policy_sha256: str,
    scripts: Mapping[str, Any],
) -> dict[str, Any]:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "source": {"commit": source_commit, "dirty": dirty},
        "version": version,
        "configuration": {
            "path": configuration_path,
            "sha256": configuration_sha256,
        },
        "policy": {"path": policy_path, "sha256": policy_sha256},
        "scripts": dict(sorted(scripts.items())),
    }


def make_candidate_id(identity: Mapping[str, Any]) -> str:
    return CANDIDATE_ID_PREFIX + sha256_bytes(canonical_bytes(identity))[:32]


def new_manifest(
    *,
    source_commit: str,
    dirty: bool,
    version: str,
    configuration_path: str,
    configuration_sha256: str,
    policy_path: str,
    policy_sha256: str,
    scripts: Mapping[str, str],
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
) -> dict[str, Any]:
    identity = candidate_identity(
        source_commit,
        dirty,
        version,
        configuration_path,
        configuration_sha256,
        policy_path,
        policy_sha256,
        scripts,
    )
    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "candidateId": make_candidate_id(identity),
        "source": identity["source"],
        "version": version,
        "configuration": identity["configuration"],
        "policy": identity["policy"],
        "scripts": identity["scripts"],
        "images": {},
        "evidence": {},
        "qualification": {
            "status": "unqualified",
            "missingChecks": list(qualification_checks(required_arches)),
        },
    }
    return manifest


# ---------------------------------------------------------------------------
# Loading and validation
# ---------------------------------------------------------------------------


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise CandidateError(f"file not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise CandidateError(f"invalid JSON in {path}: {exc}") from exc


def write_json_atomic(path: Path, value: Any, *, pretty: bool = True) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if pretty:
        text = json.dumps(value, indent=2, ensure_ascii=False) + "\n"
    else:
        text = canonical_bytes(value).decode("utf-8")
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(text, encoding="utf-8")
    os.replace(temporary, path)


def _require_exact_keys(value: Any, expected: Iterable[str], where: str) -> None:
    if not isinstance(value, dict):
        raise CandidateError(f"{where} must be an object")
    expected_set = set(expected)
    actual = set(value)
    missing = sorted(expected_set - actual)
    extra = sorted(actual - expected_set)
    if missing or extra:
        details = []
        if missing:
            details.append(f"missing {missing}")
        if extra:
            details.append(f"unexpected {extra}")
        raise CandidateError(f"{where} has invalid keys: {', '.join(details)}")


def _require_str(value: Any, where: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise CandidateError(f"{where} must be a non-empty string")
    return value


def _require_bool(value: Any, where: str) -> bool:
    if not isinstance(value, bool):
        raise CandidateError(f"{where} must be a boolean")
    return value


def _require_sha256(value: Any, where: str) -> str:
    if not isinstance(value, str) or not _SHA256_RE.match(value):
        raise CandidateError(f"{where} must be a lowercase sha256 hex digest")
    return value


def _safe_relative(value: Any, where: str) -> str:
    text = _require_str(value, where)
    if "\\" in text:
        raise CandidateError(f"{where} must use forward slashes")
    path = PurePosixPath(text)
    if path.is_absolute() or ".." in path.parts or text.startswith("./"):
        raise CandidateError(f"{where} must be a safe relative path")
    return text


def script_digest(entry: Any, where: str) -> str:
    _require_exact_keys(entry, SCRIPT_KEYS, where)
    return _require_sha256(entry["sha256"], f"{where}.sha256")


def is_valid_version(version: str) -> bool:
    return bool(_VERSION_RE.match(version))


def semver_key(version: str) -> tuple[Any, ...]:
    match = _VERSION_RE.match(version)
    if not match:
        raise CandidateError(f"invalid semantic version: {version!r}")
    pre = match.group("pre")
    prerelease: tuple[tuple[int, Any], ...] = ()
    if pre:
        parts: list[tuple[int, Any]] = []
        for identifier in pre.split("."):
            if not identifier:
                raise CandidateError(f"invalid semantic version: {version!r}")
            if identifier.isdigit():
                parts.append((0, int(identifier)))
            else:
                parts.append((1, identifier))
        prerelease = tuple(parts)
    return (
        int(match.group("major")),
        int(match.group("minor")),
        int(match.group("patch")),
        0 if pre else 1,
        prerelease,
    )


def is_stable_version(version: str) -> bool:
    match = _VERSION_RE.match(version)
    if not match:
        raise CandidateError(f"invalid semantic version: {version!r}")
    return not match.group("pre")


def assert_tag_matches(tag: str, version: str) -> None:
    expected = f"v{version}"
    if tag != expected:
        raise CandidateError(
            f"release tag {tag!r} does not equal v<MSBuild Version> ({expected!r})"
        )


def _check_file_hash(path: Path, expected: str, where: str) -> None:
    if not path.is_file():
        raise CandidateError(f"{where} file is missing: {path}")
    actual = sha256_file(path)
    if actual != expected:
        raise CandidateError(
            f"{where} sha256 mismatch: manifest {expected}, file {actual}"
        )


def _evidence_expected_arch(check: str) -> str | None:
    for arch, native_check in NATIVE_CHECKS.items():
        if check == native_check:
            return arch
    return None


def _validate_report_binding(
    report_path: Path,
    manifest: Mapping[str, Any],
    *,
    check: str,
    status: str,
    arch: str | None,
) -> None:
    report = load_json(report_path)
    if not isinstance(report, dict):
        raise CandidateError(f"evidence report {report_path} must be an object")
    if report.get("schemaVersion") != SCHEMA_VERSION:
        raise CandidateError(
            f"evidence report {report_path} schemaVersion is not {SCHEMA_VERSION}"
        )
    if report.get("check") != check:
        raise CandidateError(
            f"evidence report {report_path} is for check {report.get('check')!r}, expected {check!r}"
        )
    if report.get("status") != status:
        raise CandidateError(
            f"evidence report {report_path} status {report.get('status')!r} does not match {status!r}"
        )
    source = manifest["source"]
    if report.get("sourceCommit") != source["commit"]:
        raise CandidateError(
            f"evidence report {report_path} sourceCommit does not match candidate source"
        )
    expected_arch = _evidence_expected_arch(check)
    report_arch = report.get("arch")
    if expected_arch is not None and report_arch != expected_arch:
        raise CandidateError(
            f"evidence report {report_path} arch {report_arch!r} does not match {expected_arch!r}"
        )
    if arch is not None and report_arch is not None and report_arch != arch:
        raise CandidateError(
            f"evidence report {report_path} arch {report_arch!r} does not match {arch!r}"
        )
    image = manifest["images"].get(arch) if arch else None
    if image is not None:
        reported_image_id = report.get("imageId")
        if reported_image_id is not None and reported_image_id != image["imageId"]:
            raise CandidateError(
                f"evidence report {report_path} imageId does not match candidate image"
            )
        reported_archive = report.get("archiveSha256")
        if reported_archive is not None and reported_archive != image["archiveSha256"]:
            raise CandidateError(
                f"evidence report {report_path} archiveSha256 does not match candidate archive"
            )


def _validate_evidence(
    manifest: Mapping[str, Any],
    *,
    base_dir: Path | None,
    require_files: bool,
) -> None:
    evidence = manifest["evidence"]
    if not isinstance(evidence, dict):
        raise CandidateError("evidence must be an object")
    scripts_sha = scripts_identity(manifest["scripts"])
    for check, entry in evidence.items():
        if check not in ALL_CHECKS:
            raise CandidateError(f"evidence has unknown check {check!r}")
        where = f"evidence.{check}"
        required = {
            "status",
            "sourceCommit",
            "configurationSha256",
            "policySha256",
            "scriptsSha256",
            "reportPath",
            "reportSha256",
        }
        optional = {"arch", "imageId", "archiveSha256"}
        if not isinstance(entry, dict):
            raise CandidateError(f"{where} must be an object")
        missing = sorted(required - set(entry))
        extra = sorted(set(entry) - required - optional)
        if missing or extra:
            raise CandidateError(
                f"{where} has invalid keys: missing {missing}, unexpected {extra}"
            )
        status = entry["status"]
        if status not in EVIDENCE_STATUSES:
            raise CandidateError(f"{where}.status must be one of {EVIDENCE_STATUSES}")
        if entry["sourceCommit"] != manifest["source"]["commit"]:
            raise CandidateError(f"{where}.sourceCommit does not match candidate source")
        if entry["configurationSha256"] != manifest["configuration"]["sha256"]:
            raise CandidateError(f"{where}.configurationSha256 is not bound to the candidate")
        if entry["policySha256"] != manifest["policy"]["sha256"]:
            raise CandidateError(f"{where}.policySha256 is not bound to the candidate")
        if entry["scriptsSha256"] != scripts_sha:
            raise CandidateError(f"{where}.scriptsSha256 is not bound to the candidate")
        _safe_relative(entry["reportPath"], f"{where}.reportPath")
        _require_sha256(entry["reportSha256"], f"{where}.reportSha256")

        expected_arch = _evidence_expected_arch(check)
        arch = entry.get("arch")
        if expected_arch is not None:
            if arch != expected_arch:
                raise CandidateError(
                    f"{where}.arch must be {expected_arch!r}"
                )
        if arch is not None:
            if arch not in ARCH_PLATFORMS:
                raise CandidateError(f"{where}.arch is not a supported architecture")
            has_image_id = "imageId" in entry
            has_archive_sha = "archiveSha256" in entry
            if has_image_id != has_archive_sha:
                raise CandidateError(
                    f"{where} must record imageId and archiveSha256 together"
                )
            if has_image_id:
                image = manifest["images"].get(arch)
                if image is None:
                    raise CandidateError(f"{where} references missing image {arch!r}")
                if entry["imageId"] != image["imageId"]:
                    raise CandidateError(f"{where}.imageId does not match candidate image")
                if entry["archiveSha256"] != image["archiveSha256"]:
                    raise CandidateError(
                        f"{where}.archiveSha256 does not match candidate archive"
                    )
        elif "imageId" in entry or "archiveSha256" in entry:
            raise CandidateError(f"{where} must record arch with image identity")
        if status == "passed" and expected_arch is not None:
            image = manifest["images"].get(expected_arch)
            if image is None:
                raise CandidateError(f"{where} passed without a {expected_arch} image")
            if "imageId" not in entry:
                raise CandidateError(
                    f"{where} passed without imageId/archiveSha256 binding"
                )
        if require_files:
            if base_dir is None:
                raise CandidateError("file verification requires a candidate directory")
            report_path = base_dir / entry["reportPath"]
            _check_file_hash(report_path, entry["reportSha256"], f"{where}.report")
            _validate_report_binding(
                report_path,
                manifest,
                check=check,
                status=status,
                arch=arch,
            )


def compute_missing_checks(
    manifest: Mapping[str, Any],
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
) -> list[str]:
    missing: list[str] = []
    evidence = manifest.get("evidence", {})
    images = manifest.get("images", {})
    checks = qualification_checks(required_arches, images)
    for check in checks:
        entry = evidence.get(check)
        satisfied = isinstance(entry, dict) and entry.get("status") == "passed"
        expected_arch = _evidence_expected_arch(check)
        if satisfied and expected_arch is not None:
            image = images.get(expected_arch)
            if image is None:
                satisfied = False
            elif entry.get("imageId") != image.get("imageId"):
                satisfied = False
            elif entry.get("archiveSha256") != image.get("archiveSha256"):
                satisfied = False
        if not satisfied:
            missing.append(check)
    return missing


def _validate_image(arch: str, image: Any) -> None:
    where = f"images.{arch}"
    if arch not in ARCH_PLATFORMS:
        raise CandidateError(f"unsupported image architecture {arch!r}")
    _require_exact_keys(image, IMAGE_KEYS, where)
    if image["platform"] != ARCH_PLATFORMS[arch]:
        raise CandidateError(
            f"{where}.platform must be {ARCH_PLATFORMS[arch]!r}, got {image['platform']!r}"
        )
    if not isinstance(image["imageId"], str) or not _IMAGE_ID_RE.match(image["imageId"]):
        raise CandidateError(f"{where}.imageId must be sha256:<64 hex>")
    archive = _safe_relative(image["archive"], f"{where}.archive")
    if "/" in archive or not archive.endswith(".tar"):
        raise CandidateError(f"{where}.archive must be a .tar filename")
    _require_sha256(image["archiveSha256"], f"{where}.archiveSha256")
    registry_digest = image["registryDigest"]
    if registry_digest is not None:
        if not isinstance(registry_digest, str) or not _IMAGE_ID_RE.match(registry_digest):
            raise CandidateError(f"{where}.registryDigest must be null or sha256:<64 hex>")


def validate_manifest(
    manifest: Any,
    *,
    base_dir: Path | None = None,
    source_dir: Path | None = None,
    archive_dirs: Mapping[str, Path] | None = None,
    require_files: bool = False,
    require_archives: bool = False,
    archive_arches: Sequence[str] | None = None,
    require_all_arches: bool = False,
    require_qualified: bool = False,
    require_unpublished: bool = False,
    required_arches: Sequence[str] | None = None,
) -> None:
    if required_arches is None:
        required_arches = DEFAULT_REQUIRED_ARCHITECTURES
        if base_dir is not None:
            required_arches = required_arches_from_policy(base_dir / POLICY_NAME)
    required_arches = tuple(required_arches)
    if not required_arches:
        raise CandidateError("candidate requires at least one architecture")
    for arch in required_arches:
        if arch not in ARCH_PLATFORMS:
            raise CandidateError(f"unsupported required architecture {arch!r}")
    _require_exact_keys(manifest, TOP_LEVEL_KEYS, "candidate")
    if manifest["schemaVersion"] != SCHEMA_VERSION:
        raise CandidateError(
            f"unsupported schemaVersion {manifest['schemaVersion']!r}; expected {SCHEMA_VERSION}"
        )
    if not isinstance(manifest["candidateId"], str) or not manifest["candidateId"].startswith(
        CANDIDATE_ID_PREFIX
    ):
        raise CandidateError("candidateId must start with 'cand-'")

    _require_exact_keys(manifest["source"], SOURCE_KEYS, "source")
    commit = manifest["source"]["commit"]
    if not isinstance(commit, str) or not _COMMIT_RE.match(commit):
        raise CandidateError("source.commit must be a full 40-character commit SHA")
    _require_bool(manifest["source"]["dirty"], "source.dirty")

    version = manifest["version"]
    if not isinstance(version, str) or not is_valid_version(version):
        raise CandidateError(f"version is not a valid semantic version: {version!r}")

    for name, expected_keys, expected_name in (
        ("configuration", CONFIGURATION_KEYS, CONFIGURATION_NAME),
        ("policy", POLICY_KEYS, POLICY_NAME),
    ):
        block = manifest[name]
        _require_exact_keys(block, expected_keys, name)
        if block["path"] != expected_name:
            raise CandidateError(
                f"{name}.path must be {expected_name!r}, got {block['path']!r}"
            )
        _require_sha256(block["sha256"], f"{name}.sha256")

    scripts = manifest["scripts"]
    if not isinstance(scripts, dict) or not scripts:
        raise CandidateError("scripts must be a non-empty object")
    for path, entry in scripts.items():
        safe_path = _safe_relative(path, "scripts path")
        if not safe_path.startswith("scripts/"):
            raise CandidateError(f"script path must be under scripts/: {path!r}")
        script_digest(entry, f"scripts.{path}")

    images = manifest["images"]
    if not isinstance(images, dict):
        raise CandidateError("images must be an object")
    for arch, image in images.items():
        _validate_image(arch, image)
    if require_all_arches:
        for arch in required_arches:
            if arch not in images:
                raise CandidateError(f"candidate is missing the required {arch} image")

    _validate_evidence(manifest, base_dir=base_dir, require_files=require_files)

    qualification = manifest["qualification"]
    _require_exact_keys(qualification, QUALIFICATION_KEYS, "qualification")
    if qualification["status"] not in ("unqualified", "qualified"):
        raise CandidateError("qualification.status must be 'unqualified' or 'qualified'")
    missing = qualification["missingChecks"]
    if not isinstance(missing, list) or not all(isinstance(item, str) for item in missing):
        raise CandidateError("qualification.missingChecks must be a list of check names")
    if len(set(missing)) != len(missing):
        raise CandidateError("qualification.missingChecks contains duplicates")
    expected_missing = compute_missing_checks(manifest, required_arches)
    if missing != expected_missing:
        raise CandidateError(
            "qualification.missingChecks is inconsistent: "
            f"manifest {missing}, computed {expected_missing}"
        )
    if require_qualified and base_dir is None:
        raise CandidateError(
            "qualified verification requires the candidate policy directory"
        )
    if qualification["status"] == "qualified" and base_dir is not None:
        blockers = unresolved_policy_criteria(base_dir / POLICY_NAME)
        if blockers:
            raise CandidateError(
                "qualification policy has unresolved criteria: "
                + "; ".join(blockers)
            )
    if require_qualified:
        if qualification["status"] != "qualified":
            raise CandidateError("candidate is not qualified")
        if missing:
            raise CandidateError(
                "candidate claims qualification but is missing: " + ", ".join(missing)
            )

    identity = candidate_identity(
        commit,
        manifest["source"]["dirty"],
        version,
        manifest["configuration"]["path"],
        manifest["configuration"]["sha256"],
        manifest["policy"]["path"],
        manifest["policy"]["sha256"],
        scripts,
    )
    expected_id = make_candidate_id(identity)
    if manifest["candidateId"] != expected_id:
        raise CandidateError(
            f"candidateId does not match the canonical identity: expected {expected_id}"
        )

    if require_unpublished:
        for arch, image in images.items():
            if image["registryDigest"] is not None:
                raise CandidateError(
                    f"images.{arch}.registryDigest must be null before publication"
                )

    if require_files:
        if base_dir is None:
            raise CandidateError("file verification requires a candidate directory")
        _check_file_hash(
            base_dir / manifest["configuration"]["path"],
            manifest["configuration"]["sha256"],
            "configuration",
        )
        _check_file_hash(
            base_dir / manifest["policy"]["path"],
            manifest["policy"]["sha256"],
            "policy",
        )
        for path, entry in scripts.items():
            _check_file_hash(
                base_dir / path,
                script_digest(entry, f"scripts.{path}"),
                f"script {path}",
            )
        if source_dir is not None:
            for path, entry in scripts.items():
                _check_file_hash(
                    source_dir / path,
                    script_digest(entry, f"scripts.{path}"),
                    f"source script {path}",
                )

    if require_archives:
        if base_dir is None and not archive_dirs:
            raise CandidateError("archive verification requires a candidate/archive directory")
        arches = list(archive_arches) if archive_arches else list(images)
        for arch in arches:
            if arch not in images:
                raise CandidateError(f"archive architecture {arch!r} has no candidate image")
            image = images[arch]
            directory = (archive_dirs or {}).get(arch, base_dir)
            if directory is None:
                raise CandidateError(f"no archive directory supplied for {arch}")
            _check_file_hash(
                Path(directory) / image["archive"],
                image["archiveSha256"],
                f"{arch} archive",
            )


def load_manifest(
    path: Path,
    *,
    base_dir: Path | None = None,
    **validation: Any,
) -> dict[str, Any]:
    manifest = load_json(path)
    validate_manifest(manifest, base_dir=base_dir, **validation)
    return manifest


# ---------------------------------------------------------------------------
# Candidate mutation
# ---------------------------------------------------------------------------


def _reset_qualification(
    manifest: dict[str, Any],
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
) -> None:
    manifest["qualification"]["status"] = "unqualified"
    manifest["qualification"]["missingChecks"] = compute_missing_checks(
        manifest, required_arches
    )


def _write_candidate(
    path: Path,
    manifest: dict[str, Any],
    *,
    policy_dir: Path | None = None,
) -> None:
    policy_root = policy_dir if policy_dir is not None else path.parent
    validate_manifest(
        manifest,
        base_dir=policy_root,
        required_arches=required_arches_from_policy(policy_root / POLICY_NAME),
    )
    write_json_atomic(path, manifest)


def record_image(
    manifest: dict[str, Any],
    *,
    arch: str,
    image_id: str,
    archive: str,
    archive_sha256: str,
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
) -> dict[str, Any]:
    if arch not in ARCH_PLATFORMS:
        raise CandidateError(f"unsupported architecture {arch!r}")
    image = {
        "platform": ARCH_PLATFORMS[arch],
        "imageId": image_id,
        "archive": archive,
        "archiveSha256": archive_sha256,
        "registryDigest": None,
    }
    _validate_image(arch, image)
    existing = manifest["images"].get(arch)
    if existing is not None and existing != image:
        raise CandidateError(
            f"images.{arch} already records different content; refusing to replace it"
        )
    manifest["images"][arch] = image
    manifest["images"] = dict(sorted(manifest["images"].items()))
    _reset_qualification(manifest, required_arches)
    return manifest


def make_fragment(arch: str, image: Mapping[str, Any]) -> dict[str, Any]:
    _validate_image(arch, dict(image))
    return {
        "schemaVersion": SCHEMA_VERSION,
        "arch": arch,
        "image": dict(image),
    }


def validate_fragment(fragment: Any) -> tuple[str, dict[str, Any]]:
    _require_exact_keys(fragment, ("schemaVersion", "arch", "image"), "image fragment")
    if fragment["schemaVersion"] != SCHEMA_VERSION:
        raise CandidateError("image fragment schemaVersion is unsupported")
    arch = fragment["arch"]
    if arch not in ARCH_PLATFORMS:
        raise CandidateError(f"image fragment has unsupported arch {arch!r}")
    image = fragment["image"]
    _validate_image(arch, image)
    return arch, image


def merge_fragment(
    manifest: dict[str, Any],
    fragment: Any,
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
) -> dict[str, Any]:
    arch, image = validate_fragment(fragment)
    existing = manifest["images"].get(arch)
    if existing is not None and existing != image:
        raise CandidateError(
            f"fragment for {arch} conflicts with the candidate image identity"
        )
    manifest["images"][arch] = image
    manifest["images"] = dict(sorted(manifest["images"].items()))
    _reset_qualification(manifest, required_arches)
    return manifest


def make_report(
    *,
    check: str,
    status: str,
    source_commit: str,
    arch: str | None = None,
    image_id: str | None = None,
    archive_sha256: str | None = None,
    details: Mapping[str, str] | None = None,
) -> dict[str, Any]:
    if check not in ALL_CHECKS:
        raise CandidateError(f"unknown check {check!r}")
    if status not in EVIDENCE_STATUSES:
        raise CandidateError(f"status must be one of {EVIDENCE_STATUSES}")
    if not _COMMIT_RE.match(source_commit):
        raise CandidateError("source commit must be a full 40-character SHA")
    if arch is not None and arch not in ARCH_PLATFORMS:
        raise CandidateError(f"unsupported architecture {arch!r}")
    expected_arch = _evidence_expected_arch(check)
    if expected_arch is not None and arch != expected_arch:
        raise CandidateError(f"{check} requires arch {expected_arch!r}")
    if status == "passed" and expected_arch is not None:
        if not image_id or not _IMAGE_ID_RE.match(image_id):
            raise CandidateError(f"{check} passed report requires the tested image ID")
        _require_sha256(archive_sha256, f"{check} archiveSha256")
    if (image_id is None) != (archive_sha256 is None):
        raise CandidateError("imageId and archiveSha256 must be reported together")
    report: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "check": check,
        "status": status,
        "sourceCommit": source_commit,
    }
    if arch is not None:
        report["arch"] = arch
    if image_id is not None:
        report["imageId"] = image_id
        report["archiveSha256"] = archive_sha256
    if details:
        report["details"] = dict(sorted(details.items()))
    return report


def record_evidence(
    manifest: dict[str, Any],
    *,
    check: str,
    status: str,
    report_path: Path,
    candidate_dir: Path,
    arch: str | None = None,
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
) -> dict[str, Any]:
    if check not in ALL_CHECKS:
        raise CandidateError(f"unknown check {check!r}")
    if status not in EVIDENCE_STATUSES:
        raise CandidateError(f"status must be one of {EVIDENCE_STATUSES}")
    expected_arch = _evidence_expected_arch(check)
    if expected_arch is not None:
        if arch != expected_arch:
            raise CandidateError(f"{check} requires arch {expected_arch!r}")
    elif arch is not None and arch not in ARCH_PLATFORMS:
        raise CandidateError(f"unsupported architecture {arch!r}")

    image = manifest["images"].get(arch) if arch else None
    if status == "passed" and expected_arch is not None and image is None:
        raise CandidateError(f"{check} cannot pass without a {expected_arch} candidate image")

    destination = candidate_dir / REPORTS_DIR / f"{check}.json"
    destination.parent.mkdir(parents=True, exist_ok=True)
    if report_path.resolve() != destination.resolve():
        shutil.copyfile(report_path, destination)
    _validate_report_binding(
        destination,
        manifest,
        check=check,
        status=status,
        arch=arch,
    )
    entry: dict[str, Any] = {
        "status": status,
        "sourceCommit": manifest["source"]["commit"],
        "configurationSha256": manifest["configuration"]["sha256"],
        "policySha256": manifest["policy"]["sha256"],
        "scriptsSha256": scripts_identity(manifest["scripts"]),
        "reportPath": f"{REPORTS_DIR}/{check}.json",
        "reportSha256": sha256_file(destination),
    }
    if arch is not None:
        entry["arch"] = arch
        if image is not None:
            entry["imageId"] = image["imageId"]
            entry["archiveSha256"] = image["archiveSha256"]
    manifest["evidence"][check] = entry
    manifest["evidence"] = dict(sorted(manifest["evidence"].items()))
    _reset_qualification(manifest, required_arches)
    return manifest


def record_report(
    manifest: dict[str, Any],
    *,
    report_path: Path,
    candidate_dir: Path,
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
) -> dict[str, Any]:
    """Bind an existing schema-v1 evidence report without restating its fields."""
    report = load_json(report_path)
    if not isinstance(report, dict):
        raise CandidateError(f"evidence report {report_path} must be an object")
    if report.get("schemaVersion") != SCHEMA_VERSION:
        raise CandidateError(
            f"evidence report {report_path} schemaVersion is not {SCHEMA_VERSION}"
        )
    check = report.get("check")
    status = report.get("status")
    arch = report.get("arch")
    if not isinstance(check, str) or check not in ALL_CHECKS:
        raise CandidateError(f"evidence report has unknown check {check!r}")
    if status not in EVIDENCE_STATUSES:
        raise CandidateError(f"evidence report status must be one of {EVIDENCE_STATUSES}")
    if arch is not None and arch not in ARCH_PLATFORMS:
        raise CandidateError(f"evidence report names unsupported architecture {arch!r}")
    return record_evidence(
        manifest,
        check=check,
        status=status,
        report_path=report_path,
        candidate_dir=candidate_dir,
        arch=arch,
        required_arches=required_arches,
    )


def finalize_manifest(
    manifest: dict[str, Any],
    *,
    base_dir: Path | None = None,
    source_dir: Path | None = None,
    archive_dirs: Mapping[str, Path] | None = None,
    require_files: bool = False,
    require_archives: bool = False,
    required_arches: Sequence[str] | None = None,
) -> dict[str, Any]:
    if manifest["source"]["dirty"]:
        raise CandidateError("a candidate must not be built from a dirty source tree")
    if required_arches is None:
        required_arches = DEFAULT_REQUIRED_ARCHITECTURES
        if base_dir is not None:
            required_arches = required_arches_from_policy(base_dir / POLICY_NAME)
    validate_manifest(
        manifest,
        base_dir=base_dir,
        source_dir=source_dir,
        archive_dirs=archive_dirs,
        require_files=require_files,
        require_archives=require_archives,
        require_all_arches=True,
        require_unpublished=True,
        required_arches=required_arches,
    )
    missing = compute_missing_checks(manifest, required_arches)
    if manifest["qualification"]["status"] == "qualified" and missing:
        manifest["qualification"]["status"] = "unqualified"
    manifest["qualification"]["missingChecks"] = missing
    validate_manifest(
        manifest,
        base_dir=base_dir,
        source_dir=source_dir,
        archive_dirs=archive_dirs,
        require_files=require_files,
        require_archives=require_archives,
        require_all_arches=True,
        require_unpublished=True,
        required_arches=required_arches,
    )
    return manifest


def qualify_manifest(
    manifest: dict[str, Any],
    required_arches: Sequence[str] = DEFAULT_REQUIRED_ARCHITECTURES,
    *,
    policy_path: Path | None = None,
) -> dict[str, Any]:
    if policy_path is not None:
        blockers = unresolved_policy_criteria(policy_path)
        if blockers:
            raise CandidateError(
                "candidate cannot be qualified; policy has unresolved criteria: "
                + "; ".join(blockers)
            )
    missing = compute_missing_checks(manifest, required_arches)
    if missing:
        raise CandidateError(
            "candidate cannot be qualified; missing required checks: " + ", ".join(missing)
        )
    manifest["qualification"]["status"] = "qualified"
    manifest["qualification"]["missingChecks"] = []
    return manifest


# ---------------------------------------------------------------------------
# Publication planning and execution
# ---------------------------------------------------------------------------


def parse_push_digest(output: str) -> str:
    matches = re.findall(r"digest:\s*(sha256:[0-9a-f]{64})", output)
    unique = sorted(set(matches))
    if not unique:
        raise CandidateError("registry push output did not contain a sha256 digest")
    if len(unique) > 1:
        raise CandidateError(f"registry push reported multiple digests: {unique}")
    return unique[0]


def latest_decision(
    current_version: str | None,
    candidate_version: str,
    *,
    requested: bool,
) -> str:
    if not requested:
        return "not-requested"
    if not is_stable_version(candidate_version):
        raise CandidateError(
            f"latest is only updated for stable versions; {candidate_version} is a prerelease"
        )
    if current_version is None:
        return "allowed"
    if not is_valid_version(current_version):
        raise CandidateError(
            f"cannot compare against invalid current latest version {current_version!r}"
        )
    comparison = semver_key(candidate_version)
    current = semver_key(current_version)
    if comparison < current:
        raise CandidateError(
            f"refusing backwards latest movement: candidate {candidate_version} < published {current_version}"
        )
    if comparison == current:
        raise CandidateError(
            f"refusing to move latest: candidate {candidate_version} is already published"
        )
    return "allowed"


def parse_latest_version_from_manifest(raw_manifest: str) -> str | None:
    try:
        document = json.loads(raw_manifest)
    except json.JSONDecodeError as exc:
        raise CandidateError(f"latest registry manifest is not valid JSON: {exc}") from exc
    if not isinstance(document, dict):
        raise CandidateError("latest registry manifest is not an object")
    annotations = document.get("annotations") or {}
    if not isinstance(annotations, dict):
        raise CandidateError("latest registry annotations are not an object")
    version = annotations.get("org.opencontainers.image.version")
    if version is None:
        return None
    if not isinstance(version, str) or not is_valid_version(version):
        raise CandidateError(
            f"latest registry annotation has an invalid version: {version!r}"
        )
    return version


def build_promotion_plan(
    manifest: Mapping[str, Any],
    *,
    tag: str,
    registry: str,
    repository: str,
    update_latest: bool,
    current_latest_version: str | None,
) -> dict[str, Any]:
    version = manifest["version"]
    assert_tag_matches(tag, version)
    arches = [arch for arch in ARCH_ORDER if arch in manifest["images"]]
    if not arches:
        raise CandidateError("candidate has no images to promote")
    for arch in arches:
        image = manifest["images"][arch]
        if image["registryDigest"] is not None:
            raise CandidateError(
                f"candidate images.{arch}.registryDigest must be null before promotion"
            )
    decision = latest_decision(
        current_latest_version,
        version,
        requested=update_latest,
    )

    reference = f"{registry}/{repository}"
    index_sources = [{"arch": arch, "digestFrom": arch} for arch in arches]
    index_annotations = {
        "org.opencontainers.image.version": version,
        "org.opencontainers.image.ref.name": tag,
    }
    steps: list[dict[str, Any]] = []
    # Verify every archive before pushing any of them, so a bad export cannot
    # leave a partially promoted architecture tag behind.
    for arch in arches:
        image = manifest["images"][arch]
        steps.append(
            {
                "action": "load-archive",
                "arch": arch,
                "archive": image["archive"],
                "archiveSha256": image["archiveSha256"],
            }
        )
        steps.append(
            {
                "action": "verify-image",
                "arch": arch,
                "imageId": image["imageId"],
                "platform": image["platform"],
            }
        )
    for arch in arches:
        image = manifest["images"][arch]
        steps.append(
            {
                "action": "push-image",
                "arch": arch,
                "sourceImageId": image["imageId"],
                "target": f"{reference}:{tag}-{arch}",
            }
        )
    steps.append(
        {
            "action": "create-index",
            "purpose": "release",
            "target": f"{reference}:{tag}",
            "sources": index_sources,
            "annotations": index_annotations,
        }
    )
    steps.append(
        {
            "action": "inspect-index",
            "purpose": "release",
            "target": f"{reference}:{tag}",
        }
    )
    if update_latest:
        steps.append(
            {
                "action": "create-index",
                "purpose": "latest",
                "target": f"{reference}:latest",
                "sources": index_sources,
                "annotations": {
                    "org.opencontainers.image.version": version,
                    "org.opencontainers.image.ref.name": "latest",
                },
            }
        )
        steps.append(
            {
                "action": "inspect-index",
                "purpose": "latest",
                "target": f"{reference}:latest",
            }
        )
    return {
        "schemaVersion": SCHEMA_VERSION,
        "candidateId": manifest["candidateId"],
        "version": version,
        "tag": tag,
        "registry": registry,
        "repository": repository,
        "updateLatest": update_latest,
        "currentLatestVersion": current_latest_version,
        "latestDecision": decision,
        # latest is a repository-wide shared reference.  The publishing workflow
        # must hold this serialization group for the whole load/verify/push step.
        "serialization": {
            "scope": "repository",
            "concurrencyGroup": "image-promotion",
            "required": True,
        },
        "arches": arches,
        "steps": steps,
    }


class CommandRunner:
    """Small subprocess wrapper used only by the authorized execution path."""

    def run(self, args: Sequence[str], *, check: bool = True) -> subprocess.CompletedProcess[str]:
        printable = " ".join(shlex.quote(part) for part in args)
        print(f"+ {printable}", file=sys.stderr)
        completed = subprocess.run(
            list(args),
            text=True,
            capture_output=True,
            check=False,
        )
        if completed.stdout:
            sys.stderr.write(completed.stdout)
        if check and completed.returncode != 0:
            sys.stderr.write(completed.stderr)
            raise CandidateError(
                f"command failed ({completed.returncode}): {printable}"
            )
        return completed

    def run_raw(self, args: Sequence[str], *, check: bool = True) -> subprocess.CompletedProcess[bytes]:
        printable = " ".join(shlex.quote(part) for part in args)
        print(f"+ {printable}", file=sys.stderr)
        completed = subprocess.run(
            list(args),
            capture_output=True,
            check=False,
        )
        if check and completed.returncode != 0:
            sys.stderr.write(completed.stderr.decode("utf-8", errors="replace"))
            raise CandidateError(
                f"command failed ({completed.returncode}): {printable}"
            )
        return completed


def remote_reference_exists(
    runner: CommandRunner,
    reference: str,
) -> bool:
    completed = runner.run(
        ["docker", "buildx", "imagetools", "inspect", "--raw", reference],
        check=False,
    )
    if completed.returncode == 0:
        return True
    message = (completed.stderr or completed.stdout or "").lower()
    missing_markers = ("not found", "manifest unknown", "no such manifest", "404")
    if any(marker in message for marker in missing_markers):
        return False
    raise CandidateError(
        f"unable to inspect {reference}: "
        f"{(completed.stderr or completed.stdout).strip()}"
    )


def registry_latest_version(
    runner: CommandRunner,
    *,
    registry: str,
    repository: str,
) -> str | None:
    reference = f"{registry}/{repository}:latest"
    completed = runner.run(
        ["docker", "buildx", "imagetools", "inspect", "--raw", reference],
        check=False,
    )
    if completed.returncode != 0:
        message = (completed.stderr or completed.stdout or "").lower()
        missing_markers = (
            "not found",
            "manifest unknown",
            "no such manifest",
            "404",
        )
        if any(marker in message for marker in missing_markers):
            return None
        raise CandidateError(
            f"unable to inspect published latest {reference}: "
            f"{(completed.stderr or completed.stdout).strip()}"
        )
    version = parse_latest_version_from_manifest(completed.stdout)
    if version is None:
        raise CandidateError(
            f"published latest {reference} has no org.opencontainers.image.version "
            "annotation; refusing to guess its version"
        )
    return version


def _resolve_archive(
    arch: str,
    archive: str,
    archive_dirs: Mapping[str, Path],
) -> Path:
    directory = archive_dirs.get(arch)
    if directory is None:
        raise CandidateError(f"no archive directory supplied for {arch}")
    return Path(directory) / archive


def _write_index_metadata(path: Path, document: Mapping[str, Any]) -> None:
    write_json_atomic(path, document)


def _index_digest(metadata: Mapping[str, Any]) -> str:
    descriptor = metadata.get("containerimage.descriptor")
    if not isinstance(descriptor, dict):
        raise CandidateError("imagetools metadata is missing containerimage.descriptor")
    digest = descriptor.get("digest")
    if not isinstance(digest, str) or not _IMAGE_ID_RE.match(digest):
        raise CandidateError("imagetools metadata has an invalid index digest")
    return digest


def _inspect_raw_digest(runner: CommandRunner, reference: str) -> str:
    completed = runner.run_raw(
        ["docker", "buildx", "imagetools", "inspect", "--raw", reference]
    )
    return "sha256:" + sha256_bytes(completed.stdout)


def execute_promotion(
    plan: Mapping[str, Any],
    *,
    archive_dirs: Mapping[str, Path],
    runner: CommandRunner,
    result_path: Path | None = None,
) -> dict[str, Any]:
    registry = plan["registry"]
    repository = plan["repository"]
    reference = f"{registry}/{repository}"
    version = plan["version"]
    tag = plan["tag"]
    digests: dict[str, str] = {}
    index_digests: dict[str, str] = {}

    release_reference = f"{reference}:{tag}"
    if remote_reference_exists(runner, release_reference):
        raise CandidateError(
            f"release index {release_reference} already exists; refusing to replace "
            "published version content"
        )
    if plan["updateLatest"]:
        observed = registry_latest_version(
            runner, registry=registry, repository=repository
        )
        if observed != plan["currentLatestVersion"]:
            raise CandidateError(
                "published latest changed while promotion was being prepared; "
                f"plan expected {plan['currentLatestVersion']!r}, observed {observed!r}"
            )

    with tempfile.TemporaryDirectory(prefix="bom-promotion-") as temporary:
        metadata_path = Path(temporary) / "metadata.json"
        for step in plan["steps"]:
            action = step["action"]
            if action == "load-archive":
                archive_path = _resolve_archive(
                    step["arch"], step["archive"], archive_dirs
                )
                _check_file_hash(
                    archive_path,
                    step["archiveSha256"],
                    f"{step['arch']} archive",
                )
                runner.run(["docker", "load", "--input", str(archive_path)])
            elif action == "verify-image":
                completed = runner.run(
                    [
                        "docker",
                        "image",
                        "inspect",
                        "--format",
                        "{{.Id}}|{{.Os}}/{{.Architecture}}",
                        step["imageId"],
                    ]
                )
                observed_id, observed_platform = completed.stdout.strip().split("|", 1)
                if observed_id != step["imageId"]:
                    raise CandidateError(
                        f"loaded {step['arch']} image ID {observed_id} does not match {step['imageId']}"
                    )
                if observed_platform != step["platform"]:
                    raise CandidateError(
                        f"loaded {step['arch']} platform {observed_platform} does not match {step['platform']}"
                    )
            elif action == "push-image":
                runner.run(["docker", "tag", step["sourceImageId"], step["target"]])
                completed = runner.run(["docker", "push", step["target"]])
                digests[step["arch"]] = parse_push_digest(
                    (completed.stdout or "") + (completed.stderr or "")
                )
            elif action == "create-index":
                if step["purpose"] == "latest":
                    observed = registry_latest_version(
                        runner, registry=registry, repository=repository
                    )
                    if observed != plan["currentLatestVersion"]:
                        raise CandidateError(
                            "published latest changed during promotion; refusing to "
                            f"move latest (expected {plan['currentLatestVersion']!r}, "
                            f"observed {observed!r})"
                        )
                sources = [
                    f"{reference}@{digests[source['digestFrom']]}"
                    for source in step["sources"]
                ]
                args = ["docker", "buildx", "imagetools", "create"]
                for key, value in sorted(step["annotations"].items()):
                    args.extend(["--annotation", f"{key}={value}"])
                args.extend(["--metadata-file", str(metadata_path), "--tag", step["target"]])
                args.extend(sources)
                runner.run(args)
                index_digests[step["purpose"]] = _index_digest(load_json(metadata_path))
            elif action == "inspect-index":
                observed = _inspect_raw_digest(runner, step["target"])
                expected = index_digests.get(step["purpose"])
                if expected is None:
                    raise CandidateError(f"no created index digest for {step['purpose']}")
                if observed != expected:
                    raise CandidateError(
                        f"{step['purpose']} index digest mismatch: observed {observed}, "
                        f"created {expected}"
                    )
            else:
                raise CandidateError(f"unsupported promotion action {action!r}")

    result = {
        "schemaVersion": SCHEMA_VERSION,
        "candidateId": plan["candidateId"],
        "version": version,
        "tag": tag,
        "registry": registry,
        "repository": repository,
        "images": {
            arch: {
                "tag": f"{reference}:{tag}-{arch}",
                "digest": digests[arch],
            }
            for arch in plan["arches"]
        },
        "releaseIndex": {
            "tag": f"{reference}:{tag}",
            "digest": index_digests["release"],
        },
        "latest": (
            {
                "tag": f"{reference}:latest",
                "digest": index_digests["latest"],
                "updated": True,
            }
            if plan["updateLatest"]
            else {"updated": False}
        ),
        "promotedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    if result_path is not None:
        write_json_atomic(result_path, result)
    return result


# ---------------------------------------------------------------------------
# Packaging audits
# ---------------------------------------------------------------------------


def _packaging_reason(relative: PurePosixPath, *, mode: str, is_link: bool) -> str | None:
    if is_link:
        return "symbolic link"
    parts = [part.lower() for part in relative.parts if part]
    if not parts:
        return None
    name = parts[-1]
    if name.startswith("test_") and name.endswith(".py"):
        return "Python test suite"
    if any(part in PACKAGING_FORBIDDEN_ALWAYS_DIRECTORIES for part in parts):
        return "runtime state, test, or build-artifact path"
    if mode == "context":
        if any(part in PACKAGING_BUILD_DIRECTORIES for part in parts):
            return "runtime state, test, or build-artifact path"
    elif parts[0] in PACKAGING_BUILD_DIRECTORIES:
        return "runtime state, test, or build-artifact path"
    if name in PACKAGING_FORBIDDEN_NAMES:
        return "credential, local-configuration, or repository metadata file"
    if _LOCAL_OVERRIDE_RE.match(name):
        return "local configuration override"
    if name.startswith(".env.") and not name.endswith(".example"):
        return "local environment file"
    if any(name.endswith(suffix) for suffix in PACKAGING_FORBIDDEN_SUFFIXES):
        return "credential, test, or temporary file"
    if mode == "publish":
        if name in PACKAGING_PUBLISH_ONLY_NAMES:
            return "source or restore input in publish output"
        if name.endswith((".csproj", ".sln", ".suo", ".user")):
            return "source or IDE file in publish output"
    return None


def packaging_violations(root: Path, *, mode: str = "publish") -> list[str]:
    """Return forbidden local/credential/state paths below ``root``.

    ``publish`` inspects a dotnet publish tree, while ``context`` inspects the
    Docker build context before it is sent to the daemon.  The audit is
    deliberately path-based: it does not read file contents or contact a
    registry.
    """
    if mode not in PACKAGING_MODES:
        raise CandidateError(f"packaging mode must be one of {PACKAGING_MODES}")
    root = Path(root)
    if not root.is_dir():
        raise CandidateError(f"packaging root is not a directory: {root}")
    violations: list[str] = []
    for current, dirnames, filenames in os.walk(root, followlinks=False):
        directory = Path(current)
        relative_dir = directory.relative_to(root)
        kept: list[str] = []
        for name in sorted(dirnames):
            path = directory / name
            reason = _packaging_reason(
                PurePosixPath((relative_dir / name).as_posix()),
                mode=mode,
                is_link=path.is_symlink(),
            )
            if reason is None:
                kept.append(name)
            else:
                violations.append(f"{(relative_dir / name).as_posix()}: {reason}")
        dirnames[:] = kept
        for name in sorted(filenames):
            path = directory / name
            reason = _packaging_reason(
                PurePosixPath((relative_dir / name).as_posix()),
                mode=mode,
                is_link=path.is_symlink(),
            )
            if reason is not None:
                violations.append(f"{(relative_dir / name).as_posix()}: {reason}")
    return sorted(violations)


def assert_packaging_clean(root: Path, *, mode: str = "publish") -> None:
    violations = packaging_violations(root, mode=mode)
    if violations:
        raise CandidateError(
            f"{mode} tree {root} contains forbidden paths: " + "; ".join(violations)
        )


# ---------------------------------------------------------------------------
# Source helpers
# ---------------------------------------------------------------------------


def version_from_csproj(path: Path) -> str:
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError as exc:
        raise CandidateError(f"project file not found: {path}") from exc
    versions = re.findall(r"<Version>\s*([^<\s]+)\s*</Version>", text)
    if len(versions) != 1:
        raise CandidateError(
            f"expected exactly one <Version> element in {path}, found {len(versions)}"
        )
    version = versions[0]
    if not is_valid_version(version):
        raise CandidateError(f"project version is not a valid semantic version: {version!r}")
    return version


def git_source_state(source_dir: Path) -> tuple[str, bool]:
    def run_git(*args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["git", "-C", str(source_dir), *args],
            text=True,
            capture_output=True,
            check=False,
        )

    head = run_git("rev-parse", "HEAD")
    if head.returncode != 0:
        raise CandidateError(f"{source_dir} is not a git checkout")
    commit = head.stdout.strip()
    if not _COMMIT_RE.match(commit):
        raise CandidateError(f"unexpected git HEAD: {commit!r}")
    status = run_git("status", "--porcelain", "--untracked-files=normal")
    if status.returncode != 0:
        raise CandidateError(f"unable to read git status in {source_dir}")
    return commit, bool(status.stdout.strip())


def initialize_candidate(
    *,
    source_dir: Path,
    source_commit: str | None,
    allow_dirty: bool,
    version: str | None,
    csproj: Path | None,
    configuration: Path,
    policy: Path,
    scripts: Sequence[Path],
    output_dir: Path,
) -> tuple[dict[str, Any], Path]:
    actual_commit, dirty = git_source_state(source_dir)
    if source_commit is not None and source_commit != actual_commit:
        raise CandidateError(
            f"source commit {source_commit} does not match checkout HEAD {actual_commit}"
        )
    if dirty and not allow_dirty:
        raise CandidateError("source tree is dirty; commit or discard changes first")
    if version is None:
        if csproj is None:
            raise CandidateError("provide --version or --csproj")
        version = version_from_csproj(csproj)
    elif not is_valid_version(version):
        raise CandidateError(f"invalid version {version!r}")
    if csproj is not None:
        project_version = version_from_csproj(csproj)
        if project_version != version:
            raise CandidateError(
                f"--version {version} does not match project version {project_version}"
            )

    if not configuration.is_file():
        raise CandidateError(f"configuration file not found: {configuration}")
    if not policy.is_file():
        raise CandidateError(
            f"qualification policy file not found: {policy}. A committed machine-readable "
            "policy is required before a candidate can be prepared."
        )
    if not scripts:
        raise CandidateError("at least one verification script must be recorded")

    output_dir.mkdir(parents=True, exist_ok=True)
    destination_config = output_dir / CONFIGURATION_NAME
    destination_policy = output_dir / POLICY_NAME
    if configuration.resolve() != destination_config.resolve():
        shutil.copyfile(configuration, destination_config)
    if policy.resolve() != destination_policy.resolve():
        shutil.copyfile(policy, destination_policy)

    script_hashes: dict[str, Any] = {}
    for script in scripts:
        relative = script.as_posix()
        if not relative.startswith("scripts/"):
            try:
                relative = script.resolve().relative_to(source_dir.resolve()).as_posix()
            except ValueError as exc:
                raise CandidateError(
                    f"script {script} is outside the source tree"
                ) from exc
        _safe_relative(relative, "script path")
        if not script.is_file():
            raise CandidateError(f"script not found: {script}")
        destination = output_dir / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        if script.resolve() != destination.resolve():
            shutil.copyfile(script, destination)
        script_hashes[relative] = {"sha256": sha256_file(destination)}

    manifest = new_manifest(
        source_commit=actual_commit,
        dirty=dirty,
        version=version,
        configuration_path=CONFIGURATION_NAME,
        configuration_sha256=sha256_file(destination_config),
        policy_path=POLICY_NAME,
        policy_sha256=sha256_file(destination_policy),
        scripts=script_hashes,
        required_arches=required_arches_from_policy(destination_policy),
    )
    manifest_path = output_dir / "candidate.json"
    _write_candidate(manifest_path, manifest)
    return manifest, manifest_path
