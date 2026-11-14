#!/usr/bin/env python3
"""Promote an already-tested candidate image index.

The default is a read-only plan.  `--execute` requires an explicit matching
authorization value, loads the exact exported archives, verifies their recorded
image IDs and pushes those image IDs.  It never builds an image.  `latest` is
updated only when explicitly requested, only for stable qualified versions and
only when the published version advances.  Callers must hold the
repository-wide `image-promotion` serialization group for the whole run.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path
from typing import Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

import candidate_release as release

_REPOSITORY_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
_REGISTRY_RE = re.compile(r"^[A-Za-z0-9.-]+(?::[0-9]+)?$")


def _parse_archive_dirs(values: Sequence[str]) -> dict[str, Path]:
    result: dict[str, Path] = {}
    for value in values:
        arch, separator, directory = value.partition("=")
        if not separator:
            raise release.CandidateError(
                f"--archive-dir must be ARCH=DIR, got {value!r}"
            )
        if arch not in release.ARCH_PLATFORMS:
            raise release.CandidateError(f"unsupported archive architecture {arch!r}")
        result[arch] = Path(directory)
    return result


def _require_publication_lease() -> None:
    if os.environ.get("GITHUB_ACTIONS") == "true":
        return
    lease = os.environ.get("BOM_PUBLICATION_LEASE")
    if lease and Path(lease).is_file():
        return
    raise release.CandidateError(
        "publication executes registry writes; set BOM_PUBLICATION_LEASE to a "
        "coordinator lease file or run inside the authorized workflow"
    )


def _plan_latest_version(args: argparse.Namespace) -> str | None:
    if args.current_latest_version is not None:
        if args.no_current_latest:
            raise release.CandidateError(
                "provide only one of --current-latest-version and --no-current-latest"
            )
        if not release.is_valid_version(args.current_latest_version):
            raise release.CandidateError(
                f"invalid --current-latest-version {args.current_latest_version!r}"
            )
        return args.current_latest_version
    if args.registry_latest_manifest:
        version = release.parse_latest_version_from_manifest(
            Path(args.registry_latest_manifest).read_text(encoding="utf-8")
        )
        if version is None:
            raise release.CandidateError(
                "the supplied latest manifest has no version annotation; "
                "pass --no-current-latest or --current-latest-version explicitly"
            )
        return version
    if args.no_current_latest:
        return None
    raise release.CandidateError(
        "planning a latest update requires --current-latest-version, "
        "--registry-latest-manifest or --no-current-latest"
    )


def cmd_promote(args: argparse.Namespace) -> int:
    if not _REGISTRY_RE.match(args.registry):
        raise release.CandidateError(f"invalid registry {args.registry!r}")
    if not _REPOSITORY_RE.match(args.repository):
        raise release.CandidateError(f"invalid repository {args.repository!r}")

    candidate_path = Path(args.candidate)
    candidate_dir = Path(args.candidate_dir) if args.candidate_dir else candidate_path.parent
    source_dir = Path(args.source_dir) if args.source_dir else None
    archive_dirs = _parse_archive_dirs(args.archive_dir)
    manifest = release.load_manifest(
        candidate_path,
        base_dir=candidate_dir,
        source_dir=source_dir,
        archive_dirs=archive_dirs,
        require_files=True,
        require_archives=bool(archive_dirs),
        require_all_arches=True,
        require_qualified=True,
        require_unpublished=True,
    )
    if manifest["source"]["dirty"]:
        raise release.CandidateError("refusing to promote a candidate built from a dirty tree")
    release.assert_tag_matches(args.tag, manifest["version"])

    if source_dir is not None:
        commit, dirty = release.git_source_state(source_dir)
        if dirty:
            raise release.CandidateError("refusing to promote from a dirty checkout")
        if commit != manifest["source"]["commit"]:
            raise release.CandidateError(
                f"checkout HEAD {commit} does not match candidate source {manifest['source']['commit']}"
            )

    if args.execute:
        if args.authorize != args.tag:
            raise release.CandidateError(
                "execution requires --authorize to equal --tag"
            )
        if args.no_current_latest or args.registry_latest_manifest:
            raise release.CandidateError(
                "--no-current-latest and --registry-latest-manifest are plan-only; "
                "execution reads the published latest from the registry"
            )
        _require_publication_lease()
        missing_dirs = [arch for arch in manifest["images"] if arch not in archive_dirs]
        if missing_dirs:
            raise release.CandidateError(
                "execution requires --archive-dir for each architecture; missing "
                + ", ".join(missing_dirs)
            )
        current_latest: str | None = None
        if args.update_latest:
            runner = release.CommandRunner()
            current_latest = release.registry_latest_version(
                runner,
                registry=args.registry,
                repository=args.repository,
            )
            if (
                args.current_latest_version is not None
                and args.current_latest_version != current_latest
            ):
                raise release.CandidateError(
                    "supplied --current-latest-version does not match the registry: "
                    f"{args.current_latest_version!r} != {current_latest!r}"
                )
        plan = release.build_promotion_plan(
            manifest,
            tag=args.tag,
            registry=args.registry,
            repository=args.repository,
            update_latest=args.update_latest,
            current_latest_version=current_latest,
        )
        result_path = Path(args.result) if args.result else Path(
            "artifacts/promotion/promotion-result.json"
        )
        result = release.execute_promotion(
            plan,
            archive_dirs=archive_dirs,
            runner=release.CommandRunner(),
            result_path=result_path,
        )
        print(json.dumps(result, indent=2))
        return 0

    current_latest = _plan_latest_version(args) if args.update_latest else None
    plan = release.build_promotion_plan(
        manifest,
        tag=args.tag,
        registry=args.registry,
        repository=args.repository,
        update_latest=args.update_latest,
        current_latest_version=current_latest,
    )
    if args.result:
        release.write_json_atomic(Path(args.result), plan)
    print(json.dumps(plan, indent=2))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--candidate-dir")
    parser.add_argument("--source-dir")
    parser.add_argument("--tag", required=True)
    parser.add_argument("--registry", default="ghcr.io")
    parser.add_argument("--repository", required=True)
    parser.add_argument("--archive-dir", action="append", default=[])
    parser.add_argument("--update-latest", action="store_true")
    parser.add_argument("--current-latest-version")
    parser.add_argument(
        "--no-current-latest",
        action="store_true",
        help="plan-only assertion that the registry currently has no latest tag",
    )
    parser.add_argument(
        "--registry-latest-manifest",
        help="plan-only fixture containing the raw latest manifest JSON",
    )
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--authorize")
    parser.add_argument("--result")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return cmd_promote(args)
    except release.CandidateError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
