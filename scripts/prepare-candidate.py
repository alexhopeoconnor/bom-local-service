#!/usr/bin/env python3
"""Prepare candidate manifests and export exact image archives.

This tool never builds an image.  `record-image` only inspects and saves an
image that a native runner has already built.  Execution is refused locally
unless a coordinator lease file is supplied through BOM_CANDIDATE_LEASE; CI is
authorized by running inside the workflow runner.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

import candidate_release as release


def _parse_archive_dirs(values: Sequence[str]) -> dict[str, Path]:
    result: dict[str, Path] = {}
    for value in values:
        arch, separator, directory = value.partition("=")
        if not separator or not arch or not directory:
            raise release.CandidateError(
                f"--archive-dir must be ARCH=DIR, got {value!r}"
            )
        if arch not in release.ARCH_PLATFORMS:
            raise release.CandidateError(f"unsupported archive architecture {arch!r}")
        result[arch] = Path(directory)
    return result


def _candidate_scope(candidate_path: Path) -> tuple[str, ...]:
    return _scope_for(candidate_path.parent)


def _scope_for(directory: Path) -> tuple[str, ...]:
    return release.required_arches_from_policy(directory / release.POLICY_NAME)


def _candidate_directory(args: argparse.Namespace) -> Path:
    value = getattr(args, "candidate_dir", None)
    return Path(value) if value else Path(args.candidate).parent


def _require_execution_lease() -> None:
    if os.environ.get("GITHUB_ACTIONS") == "true":
        return
    lease = os.environ.get("BOM_CANDIDATE_LEASE")
    if lease and Path(lease).is_file():
        return
    raise release.CandidateError(
        "image export executes Docker; set BOM_CANDIDATE_LEASE to a coordinator "
        "lease file or run inside the authorized workflow"
    )


def _docker_inspect(reference: str) -> tuple[str, str]:
    completed = subprocess.run(
        [
            "docker",
            "image",
            "inspect",
            "--format",
            "{{.Id}}|{{.Os}}/{{.Architecture}}",
            reference,
        ],
        text=True,
        capture_output=True,
        check=False,
    )
    if completed.returncode != 0:
        raise release.CandidateError(
            f"unable to inspect image {reference}: {completed.stderr.strip()}"
        )
    image_id, separator, platform = completed.stdout.strip().partition("|")
    if not separator:
        raise release.CandidateError(
            f"unexpected docker image inspect output for {reference}"
        )
    return image_id, platform


def cmd_version(args: argparse.Namespace) -> int:
    print(release.version_from_csproj(Path(args.csproj)))
    return 0


def cmd_init(args: argparse.Namespace) -> int:
    manifest, path = release.initialize_candidate(
        source_dir=Path(args.source_dir),
        source_commit=args.source_commit,
        allow_dirty=args.allow_dirty,
        version=args.version,
        csproj=Path(args.csproj) if args.csproj else None,
        configuration=Path(args.config),
        policy=Path(args.policy),
        scripts=[Path(script) for script in args.script],
        output_dir=Path(args.output_dir),
    )
    print(
        json.dumps(
            {
                "candidate": str(path),
                "candidateId": manifest["candidateId"],
                "version": manifest["version"],
                "missingChecks": manifest["qualification"]["missingChecks"],
            },
            indent=2,
        )
    )
    return 0


def cmd_record_image(args: argparse.Namespace) -> int:
    if not args.candidate and not args.fragment:
        raise release.CandidateError("provide --candidate and/or --fragment")
    archive = Path(args.archive)
    commands = [
        [
            "docker",
            "image",
            "inspect",
            "--format",
            "{{.Id}}|{{.Os}}/{{.Architecture}}",
            args.image_ref,
        ],
        ["docker", "save", "--output", str(archive), args.image_ref],
    ]
    if not args.execute:
        print(
            json.dumps(
                {
                    "execute": False,
                    "arch": args.arch,
                    "platform": release.ARCH_PLATFORMS.get(args.arch),
                    "commands": commands,
                    "note": "no Docker command was executed",
                },
                indent=2,
            )
        )
        return 0

    _require_execution_lease()
    if args.arch not in release.ARCH_PLATFORMS:
        raise release.CandidateError(f"unsupported architecture {args.arch!r}")
    image_id, platform = _docker_inspect(args.image_ref)
    expected_platform = release.ARCH_PLATFORMS[args.arch]
    if platform != expected_platform:
        raise release.CandidateError(
            f"image platform {platform} does not match {args.arch} ({expected_platform})"
        )
    archive.parent.mkdir(parents=True, exist_ok=True)
    completed = subprocess.run(
        ["docker", "save", "--output", str(archive), args.image_ref],
        text=True,
        capture_output=True,
        check=False,
    )
    if completed.returncode != 0:
        raise release.CandidateError(
            f"docker save failed: {completed.stderr.strip()}"
        )
    if not archive.is_file():
        raise release.CandidateError(f"docker save did not create {archive}")
    archive_sha256 = release.sha256_file(archive)

    image = {
        "platform": platform,
        "imageId": image_id,
        "archive": archive.name,
        "archiveSha256": archive_sha256,
        "registryDigest": None,
    }
    release._validate_image(args.arch, image)

    if args.fragment:
        fragment = release.make_fragment(args.arch, image)
        release.write_json_atomic(Path(args.fragment), fragment)
    if args.candidate:
        candidate_path = Path(args.candidate)
        manifest = release.load_manifest(candidate_path, base_dir=candidate_path.parent)
        release.record_image(
            manifest,
            arch=args.arch,
            image_id=image_id,
            archive=archive.name,
            archive_sha256=archive_sha256,
            required_arches=_candidate_scope(candidate_path),
        )
        release._write_candidate(candidate_path, manifest)
    print(
        json.dumps(
            {
                "arch": args.arch,
                "imageId": image_id,
                "archive": str(archive),
                "archiveSha256": archive_sha256,
            },
            indent=2,
        )
    )
    return 0


def cmd_merge(args: argparse.Namespace) -> int:
    candidate_path = Path(args.candidate)
    manifest = release.load_manifest(candidate_path, base_dir=candidate_path.parent)
    for fragment_path in args.fragment:
        release.merge_fragment(
            manifest,
            release.load_json(Path(fragment_path)),
            required_arches=_candidate_scope(candidate_path),
        )
    output = Path(args.output) if args.output else candidate_path
    release._write_candidate(output, manifest)
    print(
        json.dumps(
            {
                "candidate": str(output),
                "images": sorted(manifest["images"]),
                "missingChecks": manifest["qualification"]["missingChecks"],
            },
            indent=2,
        )
    )
    return 0


def cmd_write_report(args: argparse.Namespace) -> int:
    details: dict[str, str] = {}
    for item in args.detail:
        key, separator, value = item.partition("=")
        if not separator or not key or not value:
            raise release.CandidateError(
                f"--detail must be KEY=VALUE, got {item!r}"
            )
        details[key] = value
    report = release.make_report(
        check=args.check,
        status=args.status,
        source_commit=args.source_commit,
        arch=args.arch,
        image_id=args.image_id,
        archive_sha256=args.archive_sha256,
        details=details,
    )
    output = Path(args.output)
    release.write_json_atomic(output, report)
    print(output)
    return 0


def cmd_record_evidence(args: argparse.Namespace) -> int:
    candidate_path = Path(args.candidate)
    candidate_dir = _candidate_directory(args)
    manifest = release.load_manifest(candidate_path, base_dir=candidate_dir)
    release.record_evidence(
        manifest,
        check=args.check,
        status=args.status,
        report_path=Path(args.report),
        candidate_dir=candidate_dir,
        arch=args.arch,
        required_arches=_scope_for(candidate_dir),
    )
    release._write_candidate(candidate_path, manifest, policy_dir=candidate_dir)
    print(
        json.dumps(
            {
                "candidate": str(candidate_path),
                "check": args.check,
                "missingChecks": manifest["qualification"]["missingChecks"],
            },
            indent=2,
        )
    )
    return 0


def cmd_record_report(args: argparse.Namespace) -> int:
    """Bind a schema-v1 report using the check/status/arch recorded inside it."""
    candidate_path = Path(args.candidate)
    candidate_dir = _candidate_directory(args)
    manifest = release.load_manifest(candidate_path, base_dir=candidate_dir)
    release.record_report(
        manifest,
        report_path=Path(args.report),
        candidate_dir=candidate_dir,
        required_arches=_scope_for(candidate_dir),
    )
    release._write_candidate(candidate_path, manifest, policy_dir=candidate_dir)
    print(
        json.dumps(
            {
                "candidate": str(candidate_path),
                "report": str(args.report),
                "missingChecks": manifest["qualification"]["missingChecks"],
            },
            indent=2,
        )
    )
    return 0


def cmd_audit_tree(args: argparse.Namespace) -> int:
    violations = release.packaging_violations(Path(args.root), mode=args.mode)
    print(
        json.dumps(
            {
                "root": str(args.root),
                "mode": args.mode,
                "clean": not violations,
                "violations": violations,
            },
            indent=2,
        )
    )
    return 1 if violations else 0


def cmd_qualify(args: argparse.Namespace) -> int:
    candidate_path = Path(args.candidate)
    candidate_dir = _candidate_directory(args)
    manifest = release.load_manifest(candidate_path, base_dir=candidate_dir)
    release.qualify_manifest(
        manifest,
        required_arches=_scope_for(candidate_dir),
        policy_path=candidate_dir / release.POLICY_NAME,
    )
    release._write_candidate(candidate_path, manifest, policy_dir=candidate_dir)
    print(f"qualified {manifest['candidateId']}")
    return 0


def cmd_finalize(args: argparse.Namespace) -> int:
    candidate_path = Path(args.candidate)
    candidate_dir = _candidate_directory(args)
    manifest = release.load_manifest(candidate_path, base_dir=candidate_dir)
    release.finalize_manifest(
        manifest,
        base_dir=candidate_dir,
        source_dir=Path(args.source_dir) if args.source_dir else None,
        archive_dirs=_parse_archive_dirs(args.archive_dir),
        require_files=args.require_files,
        require_archives=args.require_archives,
        archive_arches=args.archive_arch or None,
    )
    release._write_candidate(candidate_path, manifest, policy_dir=candidate_dir)
    print(
        json.dumps(
            {
                "candidate": str(candidate_path),
                "manifestSha256": release.sha256_file(candidate_path),
                "candidateId": manifest["candidateId"],
                "qualification": manifest["qualification"],
            },
            indent=2,
        )
    )
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    candidate_path = Path(args.candidate)
    candidate_dir = _candidate_directory(args)
    manifest = release.load_manifest(
        candidate_path,
        base_dir=candidate_dir,
        source_dir=Path(args.source_dir) if args.source_dir else None,
        archive_dirs=_parse_archive_dirs(args.archive_dir),
        require_files=args.require_files,
        require_archives=args.require_archives,
        archive_arches=args.archive_arch or None,
        require_all_arches=args.require_all_arches,
        require_qualified=args.require_qualified,
        require_unpublished=args.require_unpublished,
    )
    print(
        json.dumps(
            {
                "candidate": str(candidate_path),
                "candidateId": manifest["candidateId"],
                "version": manifest["version"],
                "qualification": manifest["qualification"],
                "manifestSha256": release.sha256_file(candidate_path),
            },
            indent=2,
        )
    )
    return 0


def cmd_show(args: argparse.Namespace) -> int:
    manifest = release.load_manifest(
        Path(args.candidate), base_dir=Path(args.candidate).parent
    )
    value: Any = manifest
    for part in args.field.split("."):
        if not isinstance(value, dict) or part not in value:
            raise release.CandidateError(f"candidate has no field {args.field!r}")
        value = value[part]
    print(json.dumps(value) if args.json else value)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    version = subparsers.add_parser("version", help="print the project version")
    version.add_argument("--csproj", default="BomLocalService.csproj")
    version.set_defaults(func=cmd_version)

    init = subparsers.add_parser("init", help="create a candidate manifest")
    init.add_argument("--source-dir", default=".")
    init.add_argument("--source-commit")
    init.add_argument("--allow-dirty", action="store_true")
    init.add_argument("--version")
    init.add_argument("--csproj")
    init.add_argument("--config", default="appsettings.json")
    init.add_argument("--policy", default=".github/qualification-policy.json")
    init.add_argument("--script", action="append", default=None)
    init.add_argument("--output-dir", required=True)
    init.set_defaults(func=cmd_init)

    image = subparsers.add_parser(
        "record-image", help="export and record an already-built native image"
    )
    image.add_argument("--arch", required=True, choices=sorted(release.ARCH_PLATFORMS))
    image.add_argument("--image-ref", required=True)
    image.add_argument("--archive", required=True)
    image.add_argument("--fragment")
    image.add_argument("--candidate")
    image.add_argument("--execute", action="store_true")
    image.set_defaults(func=cmd_record_image)

    merge = subparsers.add_parser("merge", help="merge native image fragments")
    merge.add_argument("--candidate", required=True)
    merge.add_argument("--fragment", action="append", required=True)
    merge.add_argument("--output")
    merge.set_defaults(func=cmd_merge)

    report = subparsers.add_parser("write-report", help="write a bound evidence report")
    report.add_argument("--check", required=True, choices=list(release.ALL_CHECKS))
    report.add_argument("--status", required=True, choices=list(release.EVIDENCE_STATUSES))
    report.add_argument("--source-commit", required=True)
    report.add_argument("--arch", choices=sorted(release.ARCH_PLATFORMS))
    report.add_argument("--image-id")
    report.add_argument("--archive-sha256")
    report.add_argument("--detail", action="append", default=[])
    report.add_argument("--output", required=True)
    report.set_defaults(func=cmd_write_report)

    evidence = subparsers.add_parser("record-evidence", help="bind a report to the candidate")
    evidence.add_argument("--candidate", required=True)
    evidence.add_argument("--candidate-dir")
    evidence.add_argument("--check", required=True, choices=list(release.ALL_CHECKS))
    evidence.add_argument("--status", required=True, choices=list(release.EVIDENCE_STATUSES))
    evidence.add_argument("--report", required=True)
    evidence.add_argument("--arch", choices=sorted(release.ARCH_PLATFORMS))
    evidence.set_defaults(func=cmd_record_evidence)

    bind_report = subparsers.add_parser(
        "record-report", help="bind a report using its own check/status/arch fields"
    )
    bind_report.add_argument("--candidate", required=True)
    bind_report.add_argument("--candidate-dir")
    bind_report.add_argument("--report", required=True)
    bind_report.set_defaults(func=cmd_record_report)

    audit = subparsers.add_parser(
        "audit-tree", help="reject local overrides, credentials and state in a tree"
    )
    audit.add_argument("--root", required=True)
    audit.add_argument("--mode", choices=list(release.PACKAGING_MODES), default="publish")
    audit.set_defaults(func=cmd_audit_tree)

    qualify = subparsers.add_parser("qualify", help="mark a fully evidenced candidate qualified")
    qualify.add_argument("--candidate", required=True)
    qualify.add_argument("--candidate-dir")
    qualify.set_defaults(func=cmd_qualify)

    finalize = subparsers.add_parser("finalize", help="validate and canonicalize a candidate")
    finalize.add_argument("--candidate", required=True)
    finalize.add_argument("--candidate-dir")
    finalize.add_argument("--source-dir")
    finalize.add_argument("--archive-dir", action="append", default=[])
    finalize.add_argument("--archive-arch", action="append", default=[])
    finalize.add_argument("--require-files", action="store_true")
    finalize.add_argument("--require-archives", action="store_true")
    finalize.set_defaults(func=cmd_finalize)

    verify = subparsers.add_parser("verify", help="validate a candidate manifest")
    verify.add_argument("--candidate", required=True)
    verify.add_argument("--candidate-dir")
    verify.add_argument("--source-dir")
    verify.add_argument("--archive-dir", action="append", default=[])
    verify.add_argument("--archive-arch", action="append", default=[])
    verify.add_argument("--require-files", action="store_true")
    verify.add_argument("--require-archives", action="store_true")
    verify.add_argument("--require-all-arches", action="store_true")
    verify.add_argument("--require-qualified", action="store_true")
    verify.add_argument("--require-unpublished", action="store_true")
    verify.set_defaults(func=cmd_verify)

    show = subparsers.add_parser("show", help="print one candidate field")
    show.add_argument("--candidate", required=True)
    show.add_argument("--field", required=True)
    show.add_argument("--json", action="store_true")
    show.set_defaults(func=cmd_show)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    # A default script keeps the canonical candidate tied to the service verifier.
    if args.command == "init" and not args.script:
        args.script = ["scripts/verify-service.sh"]
    try:
        return int(args.func(args))
    except release.CandidateError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
