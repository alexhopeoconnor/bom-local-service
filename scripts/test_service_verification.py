#!/usr/bin/env python3
"""Pure unit tests for verification/soak helpers. No HTTP servers or services."""

from __future__ import annotations

import base64
import hashlib
import io
import json
import struct
import subprocess
import sys
import tempfile
import unittest
import zlib
from contextlib import ExitStack, redirect_stdout
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import service_verification as sv  # noqa: E402


def chunk(kind: bytes, payload: bytes) -> bytes:
    crc = zlib.crc32(kind + payload) & 0xFFFFFFFF
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", crc)


def make_png(
    width: int = 2,
    height: int = 2,
    *,
    depth: int = 8,
    color: int = 6,
    interlace: int = 0,
    filter_byte: int = 0,
) -> bytes:
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color]
    row_bytes = (width * channels * depth + 7) // 8
    raw = b"".join(bytes([filter_byte]) + bytes(row_bytes) for _ in range(height))
    ihdr = struct.pack(">IIBBBBB", width, height, depth, color, 0, 0, interlace)
    chunks = [chunk(b"IHDR", ihdr)]
    if color == 3:
        chunks.append(chunk(b"PLTE", b"\x00\x00\x00" * 2))
    chunks.extend([chunk(b"IDAT", zlib.compress(raw)), chunk(b"IEND", b"")])
    return b"\x89PNG\r\n\x1a\n" + b"".join(chunks)


def latest_payload(now: datetime, *, count: int = 3, folder: str = "Pomona_QLD_20260101_000000") -> dict:
    frames = []
    for index in range(count):
        observed = now - timedelta(minutes=5 - index)
        frames.append({
            "frameIndex": index,
            "absoluteObservationTime": observed.isoformat().replace("+00:00", "Z"),
            "imageUrl": f"/api/radar/Pomona/QLD/frame/{index}?cacheFolder={folder}",
        })
    return {"frames": frames, "observationTime": (now - timedelta(minutes=1)).isoformat().replace("+00:00", "Z")}


class PngTests(unittest.TestCase):
    def test_valid_png_decodes(self) -> None:
        result = sv.validate_png(make_png(), url="ok.png")
        self.assertEqual((result["width"], result["height"]), (2, 2))
        self.assertEqual(result["colorType"], 6)

    def test_truncated_signature_rejected(self) -> None:
        with self.assertRaises(sv.VerificationError):
            sv.validate_png(b"\x89PNG\r\n\x1a\n")

    def test_missing_iend_rejected(self) -> None:
        data = make_png()
        self.assertIn(b"IEND", data)
        with self.assertRaises(sv.VerificationError):
            sv.validate_png(data[:-12])

    def test_bad_crc_rejected(self) -> None:
        data = bytearray(make_png())
        data[-1] ^= 0xFF
        with self.assertRaises(sv.VerificationError):
            sv.validate_png(bytes(data))

    def test_bad_filter_rejected(self) -> None:
        with self.assertRaises(sv.VerificationError):
            sv.validate_png(make_png(filter_byte=5))

    def test_interlaced_rejected_explicitly(self) -> None:
        with self.assertRaisesRegex(sv.VerificationError, "interlaced"):
            sv.validate_png(make_png(interlace=1))

    def test_palette_index_out_of_bounds_rejected(self) -> None:
        ihdr = struct.pack(">IIBBBBB", 1, 1, 8, 3, 0, 0, 0)
        data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
        data += chunk(b"PLTE", b"\x00\x00\x00" * 2)
        data += chunk(b"IDAT", zlib.compress(b"\x00\x05")) + chunk(b"IEND", b"")
        with self.assertRaisesRegex(sv.VerificationError, "palette entry"):
            sv.validate_png(data)

    def test_plte_after_idat_rejected(self) -> None:
        ihdr = struct.pack(">IIBBBBB", 1, 1, 8, 3, 0, 0, 0)
        data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
        data += chunk(b"IDAT", zlib.compress(b"\x00\x00"))
        data += chunk(b"PLTE", b"\x00\x00\x00" * 2) + chunk(b"IEND", b"")
        with self.assertRaisesRegex(sv.VerificationError, "PLTE order"):
            sv.validate_png(data)

    def test_nonconsecutive_idat_rejected(self) -> None:
        ihdr = struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0)
        compressed = zlib.compress(b"\x00\x00\x00\x00\x00")
        split = len(compressed) // 2
        data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
        data += chunk(b"IDAT", compressed[:split])
        data += chunk(b"tEXt", b"note\x00value")
        data += chunk(b"IDAT", compressed[split:]) + chunk(b"IEND", b"")
        with self.assertRaisesRegex(sv.VerificationError, "IDAT order"):
            sv.validate_png(data)

    def test_bad_idat_rejected(self) -> None:
        data = make_png()
        ihdr_end = data.find(b"IHDR") + 4 + 13 + 4
        broken = data[:ihdr_end] + chunk(b"IDAT", b"not-zlib") + chunk(b"IEND", b"")
        with self.assertRaises(sv.VerificationError):
            sv.validate_png(broken)

    def test_oversized_dimensions_rejected(self) -> None:
        ihdr = struct.pack(">IIBBBBB", 100000, 100000, 8, 6, 0, 0, 0)
        data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
        data += chunk(b"IDAT", zlib.compress(b"")) + chunk(b"IEND", b"")
        with self.assertRaises(sv.VerificationError):
            sv.validate_png(data)


class FetchBudgetTests(unittest.TestCase):
    def fake_run(
        self,
        captured: dict,
        *,
        status: str = "200",
        body: bytes = b"ok",
        returncode: int = 0,
        stderr: bytes = b"",
    ):
        def run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess:
            captured["command"] = command
            captured["kwargs"] = kwargs
            body_path = Path(command[command.index("--output") + 1])
            header_path = Path(command[command.index("--dump-header") + 1])
            body_path.write_bytes(body)
            header_path.write_bytes(b"HTTP/1.1 " + status.encode() + b" OK\r\nContent-Type: image/png\r\n\r\n")
            return subprocess.CompletedProcess(command, returncode, stdout=status.encode() + b"\n", stderr=stderr)
        return run

    def test_curl_arguments_cover_connect_total_size_redirects_and_child_timeout(self) -> None:
        captured: dict = {}
        with mock.patch.object(sv.subprocess, "run", side_effect=self.fake_run(captured)):
            status, payload, headers = sv.fetch_bounded(
                "http://example.test/data", connect_timeout=3.0, total_timeout=7.0, max_bytes=1024
            )
        command = captured["command"]
        self.assertEqual(status, 200)
        self.assertEqual(payload, b"ok")
        self.assertEqual(headers["Content-Type"], "image/png")
        self.assertEqual(command[command.index("--connect-timeout") + 1], "3")
        self.assertEqual(command[command.index("--max-time") + 1], "7")
        self.assertEqual(command[command.index("--max-filesize") + 1], "1024")
        self.assertEqual(command[command.index("--max-redirs") + 1], "0")
        self.assertNotIn("-L", command)
        self.assertEqual(captured["kwargs"]["timeout"], 9.0)

    def test_subprocess_timeout_is_a_total_budget_failure(self) -> None:
        with mock.patch.object(sv.subprocess, "run", side_effect=subprocess.TimeoutExpired("curl", 9.0)):
            with self.assertRaisesRegex(sv.VerificationError, "total budget"):
                sv.fetch_bounded("http://example.test/data", total_timeout=7.0)

    def test_redirect_status_is_rejected(self) -> None:
        captured: dict = {}
        with mock.patch.object(sv.subprocess, "run", side_effect=self.fake_run(captured, status="302")):
            with self.assertRaisesRegex(sv.VerificationError, "redirect"):
                sv.fetch_bounded("http://example.test/data")

    def test_curl_size_status_maps_to_exceeded_payload(self) -> None:
        captured: dict = {}
        with mock.patch.object(
            sv.subprocess, "run",
            side_effect=self.fake_run(captured, returncode=63, stderr=b"curl: (63) Maximum file size exceeded"),
        ):
            with self.assertRaisesRegex(sv.VerificationError, "exceeds"):
                sv.fetch_bounded("http://example.test/data", max_bytes=1024)

    def test_explicit_body_cap_rejects_large_temp_file(self) -> None:
        captured: dict = {}
        with mock.patch.object(sv.subprocess, "run", side_effect=self.fake_run(captured, body=b"x" * 2048)):
            with self.assertRaisesRegex(sv.VerificationError, "body exceeds"):
                sv.fetch_bounded("http://example.test/data", max_bytes=1024)


class TimestampTests(unittest.TestCase):
    def test_offsetless_timestamp_rejected(self) -> None:
        with self.assertRaises(sv.VerificationError):
            sv.parse_utc("2026-01-01T00:00:00", "test")

    def test_freshness_accepts_recent_and_rejects_stale_and_future(self) -> None:
        now = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        self.assertEqual(sv.parse_utc("2026-01-01T11:50:00Z", "test"), now - timedelta(minutes=10))
        with self.assertRaisesRegex(sv.VerificationError, "old"):
            sv.validate_latest(
                latest_payload(now - timedelta(hours=2)),
                expected_frames=3, suburb="Pomona", state="QLD",
                max_freshness_seconds=1800, future_skew_seconds=120, now=now,
            )
        future = latest_payload(now + timedelta(minutes=10))
        with self.assertRaisesRegex(sv.VerificationError, "future"):
            sv.validate_latest(
                future, expected_frames=3, suburb="Pomona", state="QLD",
                max_freshness_seconds=1800, future_skew_seconds=120, now=now,
            )


class CacheIdentityTests(unittest.TestCase):
    def test_identity_matches_probed_dotnet_invariant_casing(self) -> None:
        for source, expected in [("École", "école"), ("ΟΣ", "οσ"),
                                 ("Οσ", "οσ"), ("Ος", "ος"),
                                 ("İ", "İ"), ("I", "i"), ("K", "k"),
                                 ("𐐀", "𐐨")]:
            with self.subTest(source=source):
                self.assertEqual(sv.normalize_location(source, "QLD")["key"],
                                 f"{expected}|QLD")

    TS = datetime(2026, 1, 1, 0, 0, tzinfo=timezone.utc)

    def test_lossless_name_uses_legacy_shape(self) -> None:
        folder = sv.expected_cache_folder_name("St. Lucia", "qld", self.TS)
        self.assertEqual(folder, "St._Lucia_QLD_20260101_000000")
        parsed = sv.assert_cache_folder_owned(folder, "st. lucia", "QLD")
        self.assertFalse(parsed["encoded"])
        self.assertEqual(parsed["canonicalKey"], "st. lucia|QLD")

    def test_v2_collision_is_exact_for_underscore_and_space(self) -> None:
        underscore = sv.expected_cache_folder_name("St_John", "QLD", self.TS)
        spaced = sv.expected_cache_folder_name("St John", "QLD", self.TS)
        self.assertTrue(underscore.startswith("v2~"))
        self.assertEqual(spaced, "St_John_QLD_20260101_000000")
        self.assertNotEqual(underscore, spaced)
        self.assertEqual(sv.assert_cache_folder_owned(underscore, "St_John", "QLD")["canonicalKey"], "st_john|QLD")
        self.assertEqual(sv.assert_cache_folder_owned(spaced, "St John", "QLD")["canonicalKey"], "st john|QLD")
        with self.assertRaisesRegex(sv.VerificationError, "belongs to"):
            sv.assert_cache_folder_owned(spaced, "St_John", "QLD")
        with self.assertRaisesRegex(sv.VerificationError, "belongs to"):
            sv.assert_cache_folder_owned(underscore, "St John", "QLD")

    def test_v2_normalizes_case_whitespace_and_rejects_aliases(self) -> None:
        folder = sv.expected_cache_folder_name(" St, John ", "qld", self.TS)
        self.assertTrue(folder.startswith("v2~"))
        sv.assert_cache_folder_owned(folder, "st, john", "QLD")
        alias = "v2~" + base64.urlsafe_b64encode("St  John".encode()).decode().rstrip("=") + "~QLD~20260101~000000"
        with self.assertRaises(sv.VerificationError):
            sv.assert_cache_folder_owned(alias, "St John", "QLD")

    def test_leading_dot_uses_v2_and_legacy_is_reserved(self) -> None:
        folder = sv.expected_cache_folder_name(".Foo", "QLD", self.TS)
        self.assertTrue(folder.startswith("v2~"))
        sv.assert_cache_folder_owned(folder, ".foo", "QLD")
        with self.assertRaisesRegex(sv.VerificationError, "reserved"):
            sv.assert_cache_folder_owned(".Foo_QLD_20260101_000000", ".Foo", "QLD")

    def test_wrong_location_rejected_exactly(self) -> None:
        with self.assertRaisesRegex(sv.VerificationError, "belongs to"):
            sv.assert_cache_folder_owned("Pomona_NSW_20260101_000000", "Pomona", "QLD")

    def test_mixed_latest_folders_rejected(self) -> None:
        now = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        payload = latest_payload(now)
        payload["frames"][1]["imageUrl"] = "/api/radar/Pomona/QLD/frame/1?cacheFolder=Pomona_QLD_20260101_000001"
        with self.assertRaisesRegex(sv.VerificationError, "multiple snapshots"):
            sv.validate_latest(
                payload, expected_frames=3, suburb="Pomona", state="QLD",
                max_freshness_seconds=1800, future_skew_seconds=120, now=now,
            )

    def test_latest_requires_exact_indices(self) -> None:
        now = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        payload = latest_payload(now)
        payload["frames"][2]["frameIndex"] = 7
        with self.assertRaisesRegex(sv.VerificationError, "indices"):
            sv.validate_latest(
                payload, expected_frames=3, suburb="Pomona", state="QLD",
                max_freshness_seconds=1800, future_skew_seconds=120, now=now,
            )


class HistoryTests(unittest.TestCase):
    def test_inclusive_bounds_and_origin_folder(self) -> None:
        folder = "Pomona_QLD_20260101_000000"
        start = datetime(2026, 1, 1, 11, 55, tzinfo=timezone.utc)
        end = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        data = {
            "cacheFolders": [{
                "cacheFolderName": folder,
                "cacheTimestamp": start.isoformat(),
                "observationTime": end.isoformat(),
                "frames": [
                    {"frameIndex": 0, "absoluteObservationTime": start.isoformat().replace("+00:00", "Z"),
                     "imageUrl": f"/api/radar/Pomona/QLD/frame/0?cacheFolder={folder}"},
                    {"frameIndex": 1, "absoluteObservationTime": end.isoformat().replace("+00:00", "Z"),
                     "imageUrl": f"/api/radar/Pomona/QLD/frame/1?cacheFolder={folder}"},
                ],
            }],
            "totalFrames": 2,
            "startTime": start.isoformat().replace("+00:00", "Z"),
            "endTime": end.isoformat().replace("+00:00", "Z"),
        }
        groups = sv.validate_history(data, start=start, end=end, expected_frames=7, suburb="Pomona", state="QLD")
        self.assertEqual(len(groups), 1)

    def test_out_of_bounds_rejected(self) -> None:
        folder = "Pomona_QLD_20260101_000000"
        start = datetime(2026, 1, 1, 11, 55, tzinfo=timezone.utc)
        end = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        data = {
            "cacheFolders": [{
                "cacheFolderName": folder,
                "frames": [{"frameIndex": 0, "absoluteObservationTime": "2026-01-01T12:01:00Z",
                            "imageUrl": f"/api/radar/Pomona/QLD/frame/0?cacheFolder={folder}"}],
            }],
            "totalFrames": 1,
        }
        with self.assertRaisesRegex(sv.VerificationError, "outside inclusive"):
            sv.validate_history(data, start=start, end=end, expected_frames=7, suburb="Pomona", state="QLD")

    def test_lost_origin_folder_rejected(self) -> None:
        folder = "Pomona_QLD_20260101_000000"
        start = datetime(2026, 1, 1, 11, 55, tzinfo=timezone.utc)
        end = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        data = {
            "cacheFolders": [{
                "cacheFolderName": folder,
                "frames": [{"frameIndex": 0, "absoluteObservationTime": "2026-01-01T11:55:00Z",
                            "imageUrl": "/api/radar/Pomona/QLD/frame/0?cacheFolder=Other_QLD_20260101_000000"}],
            }],
            "totalFrames": 1,
        }
        with self.assertRaisesRegex(sv.VerificationError, "origin folder"):
            sv.validate_history(data, start=start, end=end, expected_frames=7, suburb="Pomona", state="QLD")


class MemoryTests(unittest.TestCase):
    def test_units_parse(self) -> None:
        self.assertEqual(sv.parse_memory("123.4MiB / 2GiB"), int(123.4 * 1024**2))
        self.assertEqual(sv.parse_memory("512KiB / 2GiB"), 512 * 1024)
        self.assertEqual(sv.parse_memory("1.5GiB / 2GiB"), int(1.5 * 1024**3))
        self.assertEqual(sv.parse_memory("1048576B / 2GiB"), 1048576)
        self.assertIsNone(sv.parse_memory("not-a-size"))
        self.assertIsNone(sv.parse_memory(None))


class CandidateSchemaTests(unittest.TestCase):
    IMAGE_ID = "sha256:" + "a" * 64

    def make_candidate(self, root: Path, *, image_id: str | None = None, archive_name: str = "candidate-amd64.tar") -> tuple[Path, str]:
        (root / "scripts").mkdir()
        script = root / "scripts" / "verify-service.sh"
        script.write_text("#!/bin/sh\n", encoding="utf-8")
        config = root / "effective-config.json"
        config.write_text('{"CacheDirectory": "/app/cache"}\n', encoding="utf-8")
        policy = root / "qualification-policy.json"
        policy.write_text('{"requiredHours": 30}\n', encoding="utf-8")
        archive = root / archive_name
        archive.write_bytes(b"candidate archive bytes")
        candidate = {
            "schemaVersion": 1,
            "candidateId": "candidate-test",
            "source": {"commit": "a" * 40, "dirty": False},
            "version": "0.1.1",
            "configuration": {"path": config.name, "sha256": sv.sha256_file(config)},
            "policy": {"path": policy.name, "sha256": sv.sha256_file(policy)},
            "scripts": {"scripts/verify-service.sh": {"sha256": sv.sha256_file(script)}},
            "images": {"amd64": {
                "platform": "linux/amd64",
                "imageId": image_id or self.IMAGE_ID,
                "archive": archive.name,
                "archiveSha256": sv.sha256_file(archive),
                "registryDigest": None,
            }},
            "evidence": {},
            "qualification": {"status": "unqualified", "missingChecks": []},
        }
        path = root / "candidate.json"
        path.write_text(json.dumps(candidate), encoding="utf-8")
        return path, image_id or self.IMAGE_ID

    def test_complete_v1_identity(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path, image_id = self.make_candidate(root)
            result = sv.load_candidate_identity(
                str(path), architecture="amd64", image_id=image_id, image_archive=str(root / "candidate-amd64.tar"), repo_root=root
            )
        self.assertTrue(result["identityComplete"])
        self.assertEqual(result["candidateId"], "candidate-test")
        self.assertTrue(result["archiveCheck"]["checked"])

    def test_missing_image_id_is_unbound_not_fabricated(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path, _ = self.make_candidate(root)
            result = sv.load_candidate_identity(
                str(path), architecture="amd64", image_id=None, image_archive=str(root / "candidate-amd64.tar"), repo_root=root
            )
        self.assertFalse(result["identityComplete"])
        self.assertIsNone(result["actualImageId"])
        self.assertIn("actualImageId", result["missing"])

    def test_image_id_mismatch_is_conflict(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path, _ = self.make_candidate(root)
            result = sv.load_candidate_identity(
                str(path), architecture="amd64", image_id="sha256:" + "b" * 64,
                image_archive=str(root / "candidate-amd64.tar"), repo_root=root,
            )
        self.assertFalse(result["identityComplete"])
        self.assertTrue(result["conflicts"])

    def test_missing_candidate_does_not_create_identity(self) -> None:
        result = sv.load_candidate_identity(None)
        self.assertFalse(result["provided"])
        self.assertIsNone(result["source"])
        self.assertFalse(result["identityComplete"])


class BackoffTests(unittest.TestCase):
    def test_valid_backoff_and_bad_delay(self) -> None:
        now = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        good = {
            "updateFailed": True,
            "lastUpdateAttempt": now.isoformat(),
            "nextRetryAt": (now + timedelta(seconds=120)).isoformat(),
            "retryAfterSeconds": 120,
        }
        self.assertEqual(sv.validate_backoff(good, now), [])
        bad = dict(good, nextRetryAt=(now + timedelta(seconds=30)).isoformat(), retryAfterSeconds=30)
        self.assertTrue(sv.validate_backoff(bad, now))

    def test_freshness_policy_rejects_stale_and_future(self) -> None:
        now = datetime(2026, 1, 1, 12, 0, tzinfo=timezone.utc)
        self.assertTrue(sv.validate_observation_freshness(
            now - timedelta(seconds=2000), now=now, max_freshness_seconds=1800, future_skew_seconds=120
        ))
        self.assertTrue(sv.validate_observation_freshness(
            now + timedelta(seconds=200), now=now, max_freshness_seconds=1800, future_skew_seconds=120
        ))

    def test_failure_tracker_clears_current_but_retains_history(self) -> None:
        tracker = sv.FailureTracker()
        tracker.fail(datetime(2026, 1, 1, tzinfo=timezone.utc), 10.0, "bad image")
        self.assertIsNotNone(tracker.current)
        tracker.succeed(datetime(2026, 1, 1, 0, 1, tzinfo=timezone.utc), 70.0)
        self.assertIsNone(tracker.current)
        self.assertEqual(len(tracker.history), 1)
        self.assertEqual(tracker.history[0]["durationSeconds"], 60.0)


class SoakLoopTests(unittest.TestCase):
    """Drive the real CLI/loop/summary with a fake clock and transport only."""

    def run_soak(self, *, hours=30, interval=1800, mode="healthy"):
        elapsed = 0.0
        epoch = datetime(2026, 1, 1, tzinfo=timezone.utc)
        handlers = {}

        def sleep(seconds):
            nonlocal elapsed
            elapsed += seconds
            if mode == "interrupted":
                handlers[sv.signal.SIGTERM](sv.signal.SIGTERM, None)

        def now():
            return epoch + timedelta(seconds=elapsed)

        def request(url, **kwargs):
            if url.endswith("/api/ready"):
                payload = {"status": "ready"}
            elif url.endswith("/api/health") or url.endswith("/status"):
                payload = {"updateFailed": False}
            else:
                payload = latest_payload(now())
                if mode == "stale":
                    payload["observationTime"] = (now() - timedelta(seconds=1900)).isoformat()
                elif mode == "future":
                    payload["observationTime"] = (now() + timedelta(seconds=200)).isoformat()
            return 200, payload, None, False

        def image(*args, **kwargs):
            return 200, b"broken" if mode == "image-recovery" and elapsed == 0 else make_png(), {}

        policy = {
            "qualificationDurationSeconds": 108000,
            "maxFreshnessSeconds": 1800, "futureSkewSeconds": 120,
            "recoverySeconds": 1800, "maxProgressGapSeconds": 3600,
            "requestConnectTimeoutSeconds": 5, "requestTimeoutSeconds": 30,
            "cpuLimit": 2.0, "memoryLimitBytes": 2 * 1024**3,
            "unresolvedCriteria": [],
        }
        with tempfile.TemporaryDirectory() as tmp, ExitStack() as stack:
            mocks = {
                "collect_git_identity": {"return_value": {"dirty": False}},
                "load_candidate_identity": {"return_value": {"identityComplete": True}},
                "source_matches_candidate": {"return_value": True},
                "collect_script_identity": {"return_value": {}},
                "collect_tool_identity": {"return_value": {}},
                "load_candidate_policy": {"return_value": (policy, [])},
                "request_json": {"side_effect": request},
                "fetch_bounded": {"side_effect": image},
                "sample_resources": {"return_value": (1024**2, 3, 1024)},
                "utc_now": {"side_effect": now},
            }
            for name, options in mocks.items():
                stack.enter_context(mock.patch.object(sv, name, **options))
            stack.enter_context(mock.patch.object(sv.time, "monotonic", side_effect=lambda: elapsed))
            stack.enter_context(mock.patch.object(sv.time, "sleep", side_effect=sleep))
            stack.enter_context(mock.patch.object(sv.signal, "signal", side_effect=lambda sig, fn: handlers.update({sig: fn})))
            stack.enter_context(redirect_stdout(io.StringIO()))
            code = sv.main([
                "soak", "--base-url", "http://example.test", "--output", tmp,
                "--duration-hours", str(hours), "--interval-seconds", str(interval),
                "--container", "fixture", "--max-memory-mib", "1024",
                "--max-browser-processes", "10", "--max-cache-growth-mib", "10",
            ])
            summary = json.loads((Path(tmp) / "summary.json").read_text())
        return code, summary

    def test_actual_thirty_hour_summary_can_qualify(self):
        code, summary = self.run_soak()
        self.assertEqual(code, 0, summary["qualification"])
        self.assertEqual(summary["validatedSamples"], summary["successfulSamples"])
        self.assertTrue(summary["qualification"]["eligible"])
        self.assertGreaterEqual(summary["actualElapsedSeconds"], 108000)

    def test_gap_before_an_advancing_sample_is_retained(self):
        code, summary = self.run_soak(interval=3700)
        self.assertEqual(code, 1)
        self.assertEqual(summary["maxForwardGapSeconds"], 3700)
        self.assertTrue(any("historic" in reason for reason in summary["qualification"]["reasons"]))

    def test_stale_and_future_images_never_count_as_validated(self):
        for mode in ("stale", "future"):
            with self.subTest(mode=mode):
                code, summary = self.run_soak(hours=1, mode=mode)
                self.assertEqual(code, 1)
                self.assertEqual(summary["successfulSamples"], 0)

    def test_bad_image_is_failed_and_recovery_clears_current_error(self):
        code, summary = self.run_soak(interval=60, mode="image-recovery")
        self.assertEqual(code, 0, summary["qualification"])
        self.assertEqual(summary["frameFailures"], 1)
        self.assertEqual(summary["successfulSamples"], summary["samples"] - 1)
        self.assertIsNone(summary["currentError"])
        self.assertEqual(summary["recoveries"], 1)
        self.assertEqual(len(summary["failureHistory"]), 1)

    def test_short_and_interrupted_actual_loops_cannot_qualify(self):
        for args in ({"hours": 1}, {"mode": "interrupted"}):
            with self.subTest(args=args):
                code, summary = self.run_soak(**args)
                self.assertEqual(code, 1)
                self.assertFalse(summary["qualification"]["eligible"])
                if args.get("mode") == "interrupted":
                    self.assertTrue(summary["interrupted"])
                    self.assertFalse(summary["completedRequestedDuration"])


class QualificationTests(unittest.TestCase):
    def policy(self, **overrides: object) -> dict:
        value = {
            "requiredSeconds": 30 * 3600,
            "requestedSeconds": 30 * 3600,
            "maxNoProgressSeconds": 3600,
            "recoverySeconds": 1800,
            "container": "bom-candidate",
            "maxMemoryMiB": 1024,
            "maxBrowserProcesses": 20,
            "maxCacheGrowthMiB": 512,
        }
        value.update(overrides)
        return value

    def summary(self, **overrides: object) -> dict:
        value = {
            "interrupted": False,
            "actualElapsedSeconds": 30 * 3600,
            "validatedSamples": 10,
            "observationAdvances": 2,
            "observationRegressions": 0,
            "secondsSinceFirstValidated": 30 * 3600,
            "secondsSinceLastForward": 60,
            "maxForwardGapSeconds": 60,
            "currentFailureSeconds": None,
            "metricsComplete": True,
            "maxMemoryBytes": 1024 * 1024,
            "candidateIdentityComplete": True,
            "sourceMatchesCandidate": True,
            "sourceDirty": False,
            "backoffErrors": [],
            "policyErrors": [],
        }
        value.update(overrides)
        return value

    def test_qualified_summary(self) -> None:
        result = sv.evaluate_qualification(self.summary(), self.policy(), smoke=False)
        self.assertTrue(result["eligible"], result["reasons"])

    def test_production_summary_uses_actual_elapsed_seconds(self) -> None:
        result = sv.evaluate_qualification(self.summary(), self.policy(), smoke=False)
        self.assertTrue(result["eligible"], result["reasons"])

    def test_short_or_interrupted_never_qualifies(self) -> None:
        result = sv.evaluate_qualification(self.summary(actualElapsedSeconds=10), self.policy(), smoke=False)
        self.assertFalse(result["eligible"])
        result = sv.evaluate_qualification(self.summary(interrupted=True), self.policy(), smoke=False)
        self.assertFalse(result["eligible"])

    def test_gap_then_recovery_is_not_erased(self) -> None:
        result = sv.evaluate_qualification(
            self.summary(maxForwardGapSeconds=4000, secondsSinceLastForward=60),
            self.policy(), smoke=False,
        )
        self.assertFalse(result["eligible"])
        self.assertTrue(any("historic" in reason for reason in result["reasons"]))

    def test_policy_mismatches_fail_closed(self) -> None:
        policy = {
            "maxFreshnessSeconds": 1800,
            "futureSkewSeconds": 120,
            "recoverySeconds": 1800,
            "maxProgressGapSeconds": 3600,
            "requestConnectTimeoutSeconds": 5,
            "requestTimeoutSeconds": 30,
            "cpuLimit": 2.0,
            "memoryLimitBytes": 2 * 1024**3,
            "unresolvedCriteria": ["memory baseline"],
        }
        errors = sv.policy_mismatches(policy, {
            "maxFreshnessSeconds": 1800,
            "futureSkewSeconds": 120,
            "recoverySeconds": 1800,
            "maxProgressGapSeconds": 3600,
            "requestConnectTimeoutSeconds": 5,
            "requestTimeoutSeconds": 30,
            "cpuLimit": 2.0,
            "memoryLimitBytes": 2 * 1024**3,
        })
        self.assertTrue(errors)
        self.assertIn("unresolved criteria", errors[0])

    def test_no_progress_and_missing_metrics_fail(self) -> None:
        result = sv.evaluate_qualification(
            self.summary(observationAdvances=0, secondsSinceFirstValidated=4000, secondsSinceLastForward=4000),
            self.policy(), smoke=False,
        )
        self.assertFalse(result["eligible"])
        result = sv.evaluate_qualification(self.summary(metricsComplete=False), self.policy(), smoke=False)
        self.assertFalse(result["eligible"])

    def test_unresolved_resource_thresholds_do_not_silently_pass(self) -> None:
        result = sv.evaluate_qualification(
            self.summary(),
            self.policy(maxMemoryMiB=None, maxBrowserProcesses=None, maxCacheGrowthMiB=None),
            smoke=False,
        )
        self.assertFalse(result["eligible"])
        self.assertIn("maxMemoryMiB", result["unresolvedCriteria"])

    def test_smoke_is_not_qualification(self) -> None:
        result = sv.evaluate_qualification(self.summary(), self.policy(), smoke=True)
        self.assertFalse(result["eligible"])


if __name__ == "__main__":
    unittest.main()
