#!/usr/bin/env python3
"""Bounded service verification and soak helpers.

The shell entry points own process/container lifecycle. This module owns pure
validation, bounded HTTP/image handling, evidence writing, and soak accounting.
It intentionally contains no service startup, no browser startup and no BOM
calls outside the URLs supplied by the caller.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import json
import os
import re
import signal
import struct
import subprocess
import sys
import tempfile
import time
import unicodedata
import urllib.parse
import zlib
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_MAX_IMAGE_BYTES = 25 * 1024 * 1024
DEFAULT_MAX_PIXELS = 40_000_000
DEFAULT_MAX_RAW_BYTES = 256 * 1024 * 1024
DEFAULT_MAX_METADATA_DISTANCE_SECONDS = 7200
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
CACHE_FOLDER_RE = re.compile(r"^(?P<prefix>.+)_(?P<date>\d{8})_(?P<time>\d{6})$")
V2_FOLDER_RE = re.compile(r"^v2~(?P<suburb>[A-Za-z0-9_-]+)~(?P<state>[A-Z]{2,3})~(?P<date>\d{8})~(?P<time>\d{6})$")
SUPPORTED_STATES = ("QLD", "NSW", "VIC", "SA", "WA", "TAS", "NT", "ACT")
STATE_FULL_NAMES = {
    "queensland": "QLD",
    "new south wales": "NSW",
    "victoria": "VIC",
    "south australia": "SA",
    "western australia": "WA",
    "tasmania": "TAS",
    "northern territory": "NT",
    "australian capital territory": "ACT",
}
MEMORY_RE = re.compile(r"\s*([0-9]+(?:\.[0-9]+)?)\s*(B|KiB|MiB|GiB|TiB)\s*")
MEMORY_FACTORS = {"B": 1, "KiB": 1024, "MiB": 1024**2, "GiB": 1024**3, "TiB": 1024**4}
PANEL_HEADERS = {
    0: ("grayscale", {1, 2, 4, 8, 16}),
    2: ("rgb", {8, 16}),
    3: ("indexed", {1, 2, 4, 8}),
    4: ("gray-alpha", {8, 16}),
    6: ("rgba", {8, 16}),
}


class VerificationError(RuntimeError):
    """Raised when captured evidence does not meet the verification contract."""


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def parse_utc(value: Any, field: str = "timestamp") -> datetime:
    if not isinstance(value, str) or not value.strip():
        raise VerificationError(f"{field} is missing or not a string")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise VerificationError(f"{field} is not ISO-8601: {value!r}") from exc
    if parsed.tzinfo is None:
        raise VerificationError(f"{field} lacks an explicit offset: {value!r}")
    return parsed.astimezone(timezone.utc)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str | None:
    try:
        return sha256_bytes(path.read_bytes())
    except OSError:
        return None


def _utf16_length(value: str) -> int:
    return len(value.encode("utf-16-le")) // 2


def _normalize_whitespace(value: str) -> str:
    normalized = unicodedata.normalize("NFC", value)
    output: list[str] = []
    pending_space = False
    for char in normalized:
        if char.isspace():
            pending_space = bool(output)
            continue
        if pending_space:
            output.append(" ")
            pending_space = False
        output.append(char)
    return "".join(output)


def normalize_suburb(value: str) -> str:
    """Mirror LocationId.NormalizeSuburb/TryCreate validation."""
    if not isinstance(value, str) or not value.strip():
        raise VerificationError("suburb is required")
    normalized = _normalize_whitespace(value)
    if not normalized or normalized in (".", ".."):
        raise VerificationError(f"suburb is not a valid location name: {value!r}")
    if _utf16_length(normalized) > 120:
        raise VerificationError("suburb must be at most 120 UTF-16 characters")
    for char in normalized:
        if char == "\x00" or ord(char) < 32 or 0x7F <= ord(char) <= 0x9F:
            raise VerificationError("suburb must not contain control characters")
        if char in '*?/\\:"<>|':
            raise VerificationError("suburb contains characters that are not valid in a location name")
    return normalized


def normalize_state(value: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise VerificationError("state is required")
    candidate = " ".join(value.strip().split()).upper()
    if candidate in SUPPORTED_STATES:
        return candidate
    full_name = " ".join(value.strip().split()).lower()
    if full_name in STATE_FULL_NAMES:
        return STATE_FULL_NAMES[full_name]
    raise VerificationError(f"state must be one of {', '.join(SUPPORTED_STATES)}")


def normalize_location(suburb: str, state: str) -> dict[str, Any]:
    normalized_suburb = normalize_suburb(suburb)
    normalized_state = normalize_state(state)
    # LocationId uses invariant simple casing. Whole-string Python lower()
    # applies contextual final sigma and expands dotted I, unlike that contract.
    canonical = "".join(
        lowered if len(lowered := char.lower()) == 1 else char
        for char in normalized_suburb
    )
    return {
        "suburb": normalized_suburb,
        "state": normalized_state,
        "canonicalSuburb": canonical,
        "key": f"{canonical}|{normalized_state}",
    }


def _legacy_prefix(location: dict[str, Any]) -> str:
    # Mirrors LocationHelper.SanitizeFileName/GetCacheFolderPrefix on Linux.
    value = f"{location['suburb']}_{location['state']}"
    return value.replace("\x00", "").replace("/", "_").replace(" ", "_").replace(",", "")


def _encode_v2(location: dict[str, Any], timestamp: datetime) -> str:
    encoded = base64.urlsafe_b64encode(location["suburb"].encode("utf-8")).decode("ascii").rstrip("=")
    return f"v2~{encoded}~{location['state']}~{timestamp.strftime('%Y%m%d')}~{timestamp.strftime('%H%M%S')}"


def _decode_base64url(value: str) -> str | None:
    if not value or not re.fullmatch(r"[A-Za-z0-9_-]+", value):
        return None
    padding = "=" * ((4 - len(value) % 4) % 4)
    try:
        raw = base64.urlsafe_b64decode(value + padding)
    except (ValueError, binascii.Error):
        return None
    if base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=") != value:
        return None
    try:
        text = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError:
        return None
    return text or None


def _parse_folder_timestamp(date_text: str, time_text: str) -> datetime:
    try:
        return datetime.strptime(f"{date_text}{time_text}", "%Y%m%d%H%M%S").replace(tzinfo=timezone.utc)
    except ValueError as exc:
        raise VerificationError(f"cache folder has an invalid timestamp: {date_text}{time_text}") from exc


def parse_cache_folder_identity(folder: str) -> dict[str, Any]:
    """Parse exact v2 or legacy folder identity; reject collisions and aliases."""
    if not isinstance(folder, str) or not folder or folder != folder.strip():
        raise VerificationError(f"cache folder is empty or padded: {folder!r}")
    if folder in (".", "..") or folder.startswith(".") or "/" in folder or "\\" in folder or ":" in folder:
        raise VerificationError(f"cache folder is reserved or not a safe basename: {folder!r}")
    try:
        basename_bytes = len(folder.encode("utf-8"))
    except UnicodeEncodeError as exc:
        raise VerificationError(f"cache folder is not valid UTF-8: {folder!r}") from exc
    if basename_bytes > 255 or any(ord(char) < 32 for char in folder):
        raise VerificationError(f"cache folder exceeds 255 UTF-8 bytes or contains control characters: {folder!r}")

    if folder.startswith("v2~"):
        match = V2_FOLDER_RE.fullmatch(folder)
        if match:
            decoded = _decode_base64url(match.group("suburb"))
            try:
                if decoded is None:
                    raise VerificationError("invalid UTF-8/base64url suburb")
                location = normalize_location(decoded, match.group("state"))
                if location["suburb"] != decoded or location["state"] != match.group("state"):
                    raise VerificationError("non-canonical normalized case/whitespace")
                timestamp = _parse_folder_timestamp(match.group("date"), match.group("time"))
                if _encode_v2(location, timestamp) != folder:
                    raise VerificationError("non-canonical base64/state/date encoding")
                return {"folder": folder, "location": location, "canonicalKey": location["key"], "timestamp": timestamp.isoformat(), "encoded": True}
            except VerificationError:
                # LocationHelper.TryParseCore falls back to legacy when strict v2
                # parsing fails; this preserves old names whose suburb starts v2~.
                pass

    match = CACHE_FOLDER_RE.fullmatch(folder)
    if not match:
        raise VerificationError(f"cache folder does not match legacy or v2 grammar: {folder!r}")
    timestamp = _parse_folder_timestamp(match.group("date"), match.group("time"))
    # The state is part of the legacy prefix; parse it explicitly from the suffix.
    prefix_parts = match.group("prefix").split("_")
    if len(prefix_parts) < 2:
        raise VerificationError(f"legacy cache folder lacks state: {folder!r}")
    legacy_state = prefix_parts[-1]
    legacy_suburb = " ".join(prefix_parts[:-1]).replace("_", " ")
    location = normalize_location(legacy_suburb, legacy_state)
    return {"folder": folder, "location": location, "canonicalKey": location["key"], "timestamp": timestamp.isoformat(), "encoded": False}


def expected_cache_folder_name(suburb: str, state: str, timestamp: datetime) -> str:
    """Return the exact basename the current service would publish."""
    location = normalize_location(suburb, state)
    legacy = f"{_legacy_prefix(location)}_{timestamp.strftime('%Y%m%d_%H%M%S')}"
    parsed = None
    if not location["suburb"].startswith("."):
        try:
            parsed = parse_cache_folder_identity(legacy)
        except VerificationError:
            parsed = None
    if parsed is not None and parsed["canonicalKey"] == location["key"]:
        candidate = legacy
    else:
        candidate = _encode_v2(location, timestamp)
    if len(candidate.encode("utf-8")) > 255:
        raise VerificationError(f"cache folder would exceed 255 UTF-8 bytes: {candidate!r}")
    return candidate


def assert_cache_folder_owned(folder: str, suburb: str, state: str) -> dict[str, Any]:
    parsed = parse_cache_folder_identity(folder)
    expected = normalize_location(suburb, state)
    if parsed["canonicalKey"] != expected["key"]:
        raise VerificationError(
            f"cache folder {folder!r} belongs to {parsed['location']['suburb']}/{parsed['location']['state']}, "
            f"not {expected['suburb']}/{expected['state']}"
        )
    return parsed


def parse_memory(text: str | None) -> int | None:
    if not text:
        return None
    first = text.split("/")[0].strip()
    match = MEMORY_RE.fullmatch(first)
    if not match:
        return None
    return int(float(match.group(1)) * MEMORY_FACTORS[match.group(2)])


def _parse_header_bytes(raw: bytes) -> dict[str, str]:
    result: dict[str, str] = {}
    for line in raw.decode("iso-8859-1", errors="replace").splitlines():
        if ":" not in line:
            continue
        name, value = line.split(":", 1)
        result[name.strip()] = value.strip()
    return result


def fetch_bounded(
    url: str,
    *,
    method: str = "GET",
    body: bytes | None = None,
    headers: dict[str, str] | None = None,
    connect_timeout: float = 5.0,
    total_timeout: float = 30.0,
    max_bytes: int = DEFAULT_MAX_IMAGE_BYTES,
) -> tuple[int, bytes, dict[str, str]]:
    """Fetch through task-owned curl with a finite total operation budget.

    curl enforces connect time, whole-operation time, body size and no redirects.
    Body and headers are written to task-owned temporary files, read with an
    explicit byte cap, then removed. A finite subprocess timeout covers a stuck
    curl child. No shell is used.
    """
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme not in ("http", "https") or not parsed.hostname:
        raise VerificationError(f"unsupported URL: {url!r}")
    if total_timeout <= 0:
        raise VerificationError(f"invalid total request budget: {total_timeout}")
    header_cap = 1024 * 1024
    subprocess_timeout = total_timeout + 2.0
    with tempfile.TemporaryDirectory(prefix="bom-curl-") as directory:
        body_path = Path(directory) / "body"
        header_path = Path(directory) / "headers"
        command = [
            "curl",
            "--silent",
            "--show-error",
            "--proto", "=http,https",
            "--connect-timeout", f"{connect_timeout:g}",
            "--max-time", f"{total_timeout:g}",
            "--max-filesize", str(max_bytes),
            "--max-redirs", "0",
            "--request", method,
            "--dump-header", str(header_path),
            "--output", str(body_path),
            "--write-out", "%{http_code}",
            "--url", url,
        ]
        for name, value in (headers or {}).items():
            command[1:1] = ["-H", f"{name}: {value}"]
        if body is not None:
            command[1:1] = ["--data-binary", "@-"]
        try:
            completed = subprocess.run(
                command,
                input=body if body is not None else None,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=subprocess_timeout,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            raise VerificationError(f"curl exceeded the {total_timeout}s total budget: {url}") from exc
        except OSError as exc:
            raise VerificationError(f"cannot start curl: {type(exc).__name__}: {exc}") from exc

        if completed.returncode != 0:
            detail = completed.stderr.decode("utf-8", errors="replace").strip()
            detail = detail[:500]
            if "maximum file size" in detail.lower() or completed.returncode == 63:
                raise VerificationError(f"response exceeds {max_bytes} bytes: {url}")
            raise VerificationError(f"curl exited {completed.returncode}: {detail or 'no stderr'}: {url}")
        status_text = completed.stdout.decode("ascii", errors="replace").strip()
        if not re.fullmatch(r"[1-5][0-9][0-9]", status_text):
            raise VerificationError(f"curl returned no valid HTTP status: {status_text!r}: {url}")
        status = int(status_text)
        if 300 <= status < 400:
            raise VerificationError(f"redirect response is not followed: HTTP {status}: {url}")
        if not body_path.is_file():
            raise VerificationError(f"curl produced no response body file: {url}")
        if body_path.stat().st_size > max_bytes:
            raise VerificationError(f"response body exceeds {max_bytes} bytes: {url}")
        payload = body_path.read_bytes()
        if len(payload) > max_bytes:
            raise VerificationError(f"response body exceeds {max_bytes} bytes: {url}")
        if header_path.is_file():
            if header_path.stat().st_size > header_cap:
                raise VerificationError(f"response headers exceed {header_cap} bytes: {url}")
            response_headers = _parse_header_bytes(header_path.read_bytes())
        else:
            response_headers = {}
        return status, payload, response_headers


def request_json(
    url: str,
    *,
    method: str = "GET",
    connect_timeout: float = 5.0,
    total_timeout: float = 30.0,
    max_bytes: int = 16 * 1024 * 1024,
) -> tuple[int | None, Any | None, str | None, bool]:
    """Return status, decoded JSON, error and whether transport failed."""
    try:
        status, body, _ = fetch_bounded(
            url,
            method=method,
            connect_timeout=connect_timeout,
            total_timeout=total_timeout,
            max_bytes=max_bytes,
        )
    except Exception as exc:  # noqa: BLE001 - callers record the exact failure
        return None, None, f"{type(exc).__name__}: {exc}", True
    try:
        return status, json.loads(body.decode("utf-8")), None, False
    except Exception as exc:  # noqa: BLE001
        return status, None, f"invalid JSON: {type(exc).__name__}: {exc}", False


def validate_ready(data: Any) -> dict[str, Any]:
    if not isinstance(data, dict):
        raise VerificationError("readiness response is not a JSON object")
    if data.get("status") != "ready":
        raise VerificationError(f"readiness status is not ready: {data!r}")
    checks = data.get("checks")
    if not isinstance(checks, dict) or checks.get("cacheWritable") is not True or checks.get("refreshWorker") is not True:
        raise VerificationError(f"readiness checks are not satisfied: {data!r}")
    return data


def _chunk_crc(ctype: bytes, chunk: bytes) -> int:
    return binascii.crc32(ctype + chunk) & 0xFFFFFFFF


def _paeth(left: int, up: int, up_left: int) -> int:
    predictor = left + up - up_left
    distance_left = abs(predictor - left)
    distance_up = abs(predictor - up)
    distance_up_left = abs(predictor - up_left)
    if distance_left <= distance_up and distance_left <= distance_up_left:
        return left
    if distance_up <= distance_up_left:
        return up
    return up_left


def _palette_index(row: bytes, x: int, depth: int) -> int:
    if depth == 8:
        return row[x]
    per_byte = 8 // depth
    byte = row[x // per_byte]
    shift = 8 - depth * ((x % per_byte) + 1)
    return (byte >> shift) & ((1 << depth) - 1)


def validate_png(
    data: bytes,
    *,
    url: str = "<bytes>",
    max_bytes: int = DEFAULT_MAX_IMAGE_BYTES,
    max_pixels: int = DEFAULT_MAX_PIXELS,
    max_raw_bytes: int = DEFAULT_MAX_RAW_BYTES,
) -> dict[str, Any]:
    """Decode a bounded, non-interlaced PNG and validate sample/palette data."""
    if not isinstance(data, (bytes, bytearray)) or len(data) < 8 or data[:8] != PNG_SIGNATURE:
        raise VerificationError(f"not a PNG: {url}")
    if len(data) > max_bytes:
        raise VerificationError(f"PNG exceeds {max_bytes} bytes: {url}")

    position = 8
    ihdr: bytes | None = None
    plte: bytes | None = None
    idat: list[bytes] = []
    ended = False
    plte_seen = False
    idat_started = False
    idat_ended = False
    while position < len(data):
        if len(data) - position < 12:
            raise VerificationError(f"truncated PNG chunk header: {url}")
        length = struct.unpack(">I", data[position:position + 4])[0]
        ctype = data[position + 4:position + 8]
        end = position + 12 + length
        if end > len(data):
            raise VerificationError(f"truncated PNG chunk: {url}")
        chunk = data[position + 8:position + 8 + length]
        crc = struct.unpack(">I", data[position + 8 + length:end])[0]
        if _chunk_crc(ctype, chunk) != crc:
            raise VerificationError(f"bad PNG CRC in {ctype!r}: {url}")
        if position == 8 and ctype != b"IHDR":
            raise VerificationError(f"PNG does not start with IHDR: {url}")
        if ctype == b"IHDR":
            if ihdr is not None or length != 13:
                raise VerificationError(f"invalid or duplicate IHDR: {url}")
            ihdr = chunk
        elif ctype == b"PLTE":
            if plte_seen or idat_started or ihdr is None:
                raise VerificationError(f"invalid PLTE order or duplicate: {url}")
            plte_seen = True
            plte = chunk
        elif ctype == b"IDAT":
            if ihdr is None or idat_ended:
                raise VerificationError(f"invalid IDAT order: {url}")
            idat_started = True
            idat.append(chunk)
        elif ctype == b"IEND":
            if length != 0 or not idat_started:
                raise VerificationError(f"invalid IEND order or length: {url}")
            ended = True
            position = end
            break
        elif ctype[0] & 0x20 == 0:
            raise VerificationError(f"unsupported critical PNG chunk {ctype!r}: {url}")
        else:
            if idat_started:
                idat_ended = True
        position = end

    if not ended or position != len(data) or ihdr is None or not idat:
        raise VerificationError(f"incomplete PNG: {url}")

    width, height, depth, color, compression, filter_method, interlace = struct.unpack(">IIBBBBB", ihdr)
    if width <= 0 or height <= 0 or width * height > max_pixels:
        raise VerificationError(f"PNG dimensions are invalid or too large: {width}x{height}: {url}")
    if color not in PANEL_HEADERS:
        raise VerificationError(f"unsupported PNG colour type {color}: {url}")
    if depth not in PANEL_HEADERS[color][1]:
        raise VerificationError(f"unsupported PNG bit depth {depth} for colour type {color}: {url}")
    if compression != 0 or filter_method != 0:
        raise VerificationError(f"unsupported PNG compression/filter method: {url}")
    if interlace != 0:
        raise VerificationError(f"interlaced PNG is not supported by this verifier: {url}")
    if color in (0, 4) and plte is not None:
        raise VerificationError(f"PLTE is not permitted for grayscale PNG: {url}")
    if plte is not None and (not plte or len(plte) % 3 != 0 or len(plte) > 768):
        raise VerificationError(f"PNG PLTE is malformed: {url}")
    palette_entries = 0
    if color == 3:
        if plte is None:
            raise VerificationError(f"indexed PNG lacks PLTE: {url}")
        palette_entries = len(plte) // 3
        if palette_entries == 0 or palette_entries > (1 << depth):
            raise VerificationError(f"indexed PNG palette exceeds bit-depth capacity: {url}")

    channels = {"grayscale": 1, "rgb": 3, "indexed": 1, "gray-alpha": 2, "rgba": 4}[PANEL_HEADERS[color][0]]
    row_bytes = (width * channels * depth + 7) // 8
    bpp = max(1, (channels * depth + 7) // 8)
    expected_raw = height * (row_bytes + 1)
    if expected_raw > max_raw_bytes:
        raise VerificationError(f"PNG decoded scanline size {expected_raw} exceeds {max_raw_bytes}: {url}")
    decompressor = zlib.decompressobj()
    try:
        raw = decompressor.decompress(b"".join(idat), expected_raw + 1)
    except zlib.error as exc:
        raise VerificationError(f"PNG IDAT cannot be inflated: {url}: {exc}") from exc
    if (
        len(raw) != expected_raw
        or not decompressor.eof
        or decompressor.unused_data
        or decompressor.unconsumed_tail
    ):
        raise VerificationError(f"PNG scanline data has the wrong size or trailing data: {url}")

    previous = bytearray(row_bytes)
    offset = 0
    filter_types: set[int] = set()
    for _ in range(height):
        filter_type = raw[offset]
        if filter_type > 4:
            raise VerificationError(f"PNG has an invalid filter byte: {url}")
        filter_types.add(filter_type)
        encoded_row = raw[offset + 1:offset + 1 + row_bytes]
        row = bytearray(row_bytes)
        for x in range(row_bytes):
            left = row[x - bpp] if x >= bpp else 0
            up = previous[x]
            up_left = previous[x - bpp] if x >= bpp else 0
            if filter_type == 0:
                value = encoded_row[x]
            elif filter_type == 1:
                value = (encoded_row[x] + left) & 0xFF
            elif filter_type == 2:
                value = (encoded_row[x] + up) & 0xFF
            elif filter_type == 3:
                value = (encoded_row[x] + ((left + up) // 2)) & 0xFF
            else:
                value = (encoded_row[x] + _paeth(left, up, up_left)) & 0xFF
            row[x] = value
        if color == 3:
            for x in range(width):
                if _palette_index(row, x, depth) >= palette_entries:
                    raise VerificationError(f"indexed PNG pixel references a palette entry outside PLTE: {url}")
        previous = row
        offset += row_bytes + 1
    return {
        "width": width,
        "height": height,
        "bitDepth": depth,
        "colorType": color,
        "interlace": interlace,
        "bytes": len(data),
        "paletteEntries": palette_entries,
        "filterTypes": sorted(filter_types),
        "decoded": True,
        "validation": "chunks+crc+inflate+unfilter+palette-index",
    }


def frame_url(base: str, image_url: str) -> str:
    if not isinstance(image_url, str) or not image_url:
        raise VerificationError("frame imageUrl is missing")
    if image_url.startswith("/"):
        return base.rstrip("/") + image_url
    parsed = urllib.parse.urlsplit(image_url)
    if parsed.scheme in ("http", "https"):
        return image_url
    return base.rstrip("/") + "/" + image_url.lstrip("/")


def cache_folder_from_image_url(image_url: str) -> str:
    if not isinstance(image_url, str) or not image_url:
        raise VerificationError("frame imageUrl is missing")
    query = urllib.parse.parse_qs(urllib.parse.urlsplit(image_url).query, keep_blank_values=True)
    values = query.get("cacheFolder")
    if values is None or len(values) != 1 or not values[0]:
        raise VerificationError(f"imageUrl does not identify exactly one cacheFolder: {image_url!r}")
    return values[0]


def validate_latest(
    latest: Any,
    *,
    expected_frames: int,
    suburb: str,
    state: str,
    max_freshness_seconds: int,
    future_skew_seconds: int,
    now: datetime | None = None,
    max_metadata_distance_seconds: int = DEFAULT_MAX_METADATA_DISTANCE_SECONDS,
) -> dict[str, Any]:
    if not isinstance(latest, dict):
        raise VerificationError("latest response is not a JSON object")
    frames = latest.get("frames")
    if not isinstance(frames, list) or len(frames) != expected_frames:
        raise VerificationError(f"expected {expected_frames} frames, got {len(frames) if isinstance(frames, list) else 'non-list'}")
    observation = parse_utc(latest.get("observationTime"), "observationTime")
    current = now or utc_now()
    age_seconds = (current - observation).total_seconds()
    if age_seconds > max_freshness_seconds:
        raise VerificationError(f"latest observation is {age_seconds:.0f}s old; max {max_freshness_seconds}s")
    if age_seconds < -future_skew_seconds:
        raise VerificationError(f"latest observation is {-age_seconds:.0f}s in the future; max skew {future_skew_seconds}s")

    indexes: list[int] = []
    times: list[datetime] = []
    folders: list[str] = []
    for frame in frames:
        if not isinstance(frame, dict):
            raise VerificationError("latest frame is not an object")
        index = frame.get("frameIndex")
        if not isinstance(index, int) or isinstance(index, bool):
            raise VerificationError(f"frameIndex is not an integer: {index!r}")
        indexes.append(index)
        observed = parse_utc(frame.get("absoluteObservationTime"), f"frame {index} absoluteObservationTime")
        times.append(observed)
        if abs((observation - observed).total_seconds()) > max_metadata_distance_seconds:
            raise VerificationError(f"frame {index} observation is implausible relative to metadata")
        folder = cache_folder_from_image_url(frame.get("imageUrl"))
        assert_cache_folder_owned(folder, suburb, state)
        folders.append(folder)

    if sorted(indexes) != list(range(expected_frames)):
        raise VerificationError(f"frame indices are not exactly 0..{expected_frames - 1}: {indexes}")
    if times != sorted(times):
        raise VerificationError("latest frames are not chronological")
    if len(set(times)) != len(times):
        raise VerificationError("latest frames contain duplicate observation times")
    if len(set(folders)) != 1:
        raise VerificationError(f"latest frames identify multiple snapshots: {sorted(set(folders))}")
    return {"frames": frames, "observation": observation, "cacheFolder": folders[0]}


def validate_history(
    data: Any,
    *,
    start: datetime,
    end: datetime,
    expected_frames: int,
    suburb: str,
    state: str,
) -> list[dict[str, Any]]:
    if start > end:
        raise VerificationError("history start is after end")
    if not isinstance(data, dict):
        raise VerificationError("timeseries response is not a JSON object")
    folders = data.get("cacheFolders")
    if not isinstance(folders, list) or not folders:
        raise VerificationError("history cacheFolders is empty")
    total = data.get("totalFrames")
    actual_total = sum(len(folder.get("frames") or []) for folder in folders if isinstance(folder, dict))
    if total != actual_total:
        raise VerificationError(f"totalFrames mismatch: {total!r} != {actual_total}")
    if data.get("startTime") is not None and parse_utc(data["startTime"], "history startTime") != start:
        raise VerificationError("history response startTime differs from the requested start")
    if data.get("endTime") is not None and parse_utc(data["endTime"], "history endTime") != end:
        raise VerificationError("history response endTime differs from the requested end")

    all_observations: list[datetime] = []
    groups: list[dict[str, Any]] = []
    for folder in folders:
        if not isinstance(folder, dict):
            raise VerificationError("history cache folder is not an object")
        name = folder.get("cacheFolderName")
        assert_cache_folder_owned(name, suburb, state)
        frames = folder.get("frames")
        if not isinstance(frames, list) or not frames:
            raise VerificationError(f"history folder {name!r} has no frames")
        indexes: list[int] = []
        for frame in frames:
            if not isinstance(frame, dict):
                raise VerificationError(f"history frame in {name!r} is not an object")
            index = frame.get("frameIndex")
            if not isinstance(index, int) or isinstance(index, bool) or not 0 <= index < expected_frames:
                raise VerificationError(f"history frameIndex is invalid in {name!r}: {index!r}")
            indexes.append(index)
            observed = parse_utc(frame.get("absoluteObservationTime"), f"history frame {name}/{index}")
            if not start <= observed <= end:
                raise VerificationError(f"history frame {name}/{index} is outside inclusive bounds {start}..{end}")
            if cache_folder_from_image_url(frame.get("imageUrl")) != name:
                raise VerificationError(f"history frame {name}/{index} does not retain its origin folder")
            all_observations.append(observed)
        if len(set(indexes)) != len(indexes):
            raise VerificationError(f"history folder {name!r} has duplicate frame indices")
        groups.append({"cacheFolderName": name, "frames": frames})
    if len(set(all_observations)) != len(all_observations):
        raise VerificationError("history contains duplicate observation timestamps across folders")
    return groups


def collect_git_identity(repo_root: Path) -> dict[str, Any]:
    result: dict[str, Any] = {"head": None, "dirty": None, "error": None}
    try:
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=repo_root, capture_output=True, text=True, timeout=5, check=False
        )
        status = subprocess.run(
            ["git", "status", "--porcelain"], cwd=repo_root, capture_output=True, text=True, timeout=5, check=False
        )
        if head.returncode == 0:
            result["head"] = head.stdout.strip()
        else:
            result["error"] = head.stderr.strip() or "git rev-parse failed"
        if status.returncode == 0:
            result["dirty"] = bool(status.stdout.strip())
        else:
            result["error"] = status.stderr.strip() or result["error"] or "git status failed"
    except Exception as exc:  # noqa: BLE001 - evidence must retain the failure
        result["error"] = f"{type(exc).__name__}: {exc}"
    return result


def collect_script_identity(repo_root: Path) -> dict[str, Any]:
    paths = [repo_root / "scripts" / "verify-service.sh", repo_root / "scripts" / "soak-service.sh", repo_root / "scripts" / "service_verification.py"]
    return {str(path.relative_to(repo_root)): sha256_file(path) for path in paths}


def collect_tool_identity(repo_root: Path) -> dict[str, Any]:
    tools: dict[str, Any] = {"python": sys.version.split()[0]}
    try:
        result = subprocess.run(["curl", "--version"], capture_output=True, text=True, timeout=5, check=False)
        if result.returncode == 0 and result.stdout:
            tools["curl"] = result.stdout.splitlines()[0]
    except Exception as exc:  # noqa: BLE001
        tools["curlError"] = f"{type(exc).__name__}: {exc}"
    return tools


def _looks_like_sha256(value: Any) -> bool:
    return isinstance(value, str) and bool(re.fullmatch(r"[0-9a-fA-F]{64}", value))


def _looks_like_image_id(value: Any) -> bool:
    return isinstance(value, str) and bool(re.fullmatch(r"sha256:[0-9a-fA-F]{64}", value))


def load_candidate_identity(
    path: str | None,
    *,
    architecture: str | None = None,
    image_id: str | None = None,
    image_archive: str | None = None,
    repo_root: Path | None = None,
) -> dict[str, Any]:
    """Load candidate.json schema v1 and check the caller-supplied run identity.

    Missing fields remain missing and make the run unbound. A supplied value
    that conflicts with the candidate or the actual inspected artifact is a
    hard verification failure, not a guessed value.
    """
    if not path:
        return {
            "provided": False,
            "path": None,
            "fileSha256": None,
            "schemaVersion": None,
            "candidateId": None,
            "source": None,
            "version": None,
            "configuration": None,
            "policy": None,
            "scripts": None,
            "images": None,
            "evidence": None,
            "declaredQualification": None,
            "architecture": architecture,
            "selectedImage": None,
            "actualImageId": image_id,
            "imageIdMatches": None,
            "archiveCheck": None,
            "scriptChecks": None,
            "missing": ["candidateJson"],
            "reasons": ["candidate.json was not supplied"],
            "conflicts": [],
            "identityComplete": False,
        }
    candidate_path = Path(path)
    raw = candidate_path.read_bytes()
    data = json.loads(raw.decode("utf-8"))
    if not isinstance(data, dict):
        raise VerificationError("candidate.json must contain an object")
    missing: list[str] = []
    reasons: list[str] = []
    conflicts: list[str] = []
    if data.get("schemaVersion") != 1:
        missing.append("schemaVersion:1")
    candidate_id = data.get("candidateId")
    if not isinstance(candidate_id, str) or not candidate_id:
        missing.append("candidateId")
    source = data.get("source")
    if not isinstance(source, dict):
        missing.append("source")
        source = {}
    commit = source.get("commit")
    if not isinstance(commit, str) or not re.fullmatch(r"[0-9a-fA-F]{40,64}", commit):
        missing.append("source.commit")
    if source.get("dirty") is not False:
        missing.append("source.dirty:false")
    version = data.get("version")
    if not isinstance(version, str) or not version:
        missing.append("version")
    configuration = data.get("configuration")
    if not isinstance(configuration, dict) or not _looks_like_sha256(configuration.get("sha256")):
        missing.append("configuration.sha256")
    policy = data.get("policy")
    if not isinstance(policy, dict) or not _looks_like_sha256(policy.get("sha256")):
        missing.append("policy.sha256")
    scripts = data.get("scripts")
    if not isinstance(scripts, dict) or not scripts:
        missing.append("scripts")
    images = data.get("images")
    if not isinstance(images, dict):
        missing.append("images")
        images = {}
    evidence = data.get("evidence")
    if evidence is not None and not isinstance(evidence, dict):
        missing.append("evidence")

    selected_image = images.get(architecture) if architecture else None
    archive_check: dict[str, Any] | None = None
    if not architecture:
        missing.append("actualArchitecture")
    elif not isinstance(selected_image, dict):
        missing.append(f"images.{architecture}")
    else:
        expected_platform = f"linux/{architecture}"
        if selected_image.get("platform") != expected_platform:
            conflicts.append(f"images.{architecture}.platform is {selected_image.get('platform')!r}, expected {expected_platform!r}")
        declared_image_id = selected_image.get("imageId")
        if not _looks_like_image_id(declared_image_id):
            missing.append(f"images.{architecture}.imageId")
            declared_image_id = None
        if not _looks_like_image_id(image_id):
            missing.append("actualImageId")
        elif declared_image_id and image_id != declared_image_id:
            conflicts.append(f"actual image ID {image_id} does not match candidate images.{architecture}.imageId {declared_image_id}")
        archive_name = selected_image.get("archive")
        archive_sha = selected_image.get("archiveSha256")
        if not isinstance(archive_name, str) or not archive_name:
            missing.append(f"images.{architecture}.archive")
        if not _looks_like_sha256(archive_sha):
            missing.append(f"images.{architecture}.archiveSha256")
        if archive_name and _looks_like_sha256(archive_sha):
            archive_path = Path(image_archive) if image_archive else candidate_path.parent / archive_name
            if not archive_path.is_file():
                missing.append("actualImageArchive")
                archive_check = {"path": str(archive_path), "checked": False, "error": "archive file not found"}
            else:
                actual_archive_sha = sha256_file(archive_path)
                archive_check = {
                    "path": str(archive_path),
                    "checked": True,
                    "sha256": actual_archive_sha,
                    "matches": actual_archive_sha == archive_sha,
                }
                if actual_archive_sha != archive_sha:
                    conflicts.append("actual image archive SHA-256 does not match candidate archiveSha256")

    script_checks: dict[str, Any] = {}
    if isinstance(scripts, dict) and scripts:
        for relative, entry in scripts.items():
            if not isinstance(relative, str) or not isinstance(entry, dict) or not _looks_like_sha256(entry.get("sha256")):
                missing.append(f"scripts.{relative or '<empty>'}.sha256")
                continue
            candidates = [candidate_path.parent / relative]
            if repo_root is not None:
                candidates.append(Path(repo_root) / relative)
            actual_path = next((candidate for candidate in candidates if candidate.is_file()), None)
            if actual_path is None:
                script_checks[relative] = {"checked": False, "error": "script file not found"}
                missing.append(f"scripts.{relative}")
                continue
            actual = sha256_file(actual_path)
            script_checks[relative] = {"checked": True, "sha256": actual, "matches": actual == entry["sha256"]}
            if actual != entry["sha256"]:
                conflicts.append(f"actual script hash does not match candidate scripts.{relative}")

    if isinstance(configuration, dict) and isinstance(configuration.get("path"), str):
        config_path = candidate_path.parent / configuration["path"]
        if config_path.is_file():
            actual = sha256_file(config_path)
            configuration = {**configuration, "checked": True, "actualSha256": actual, "matches": actual == configuration.get("sha256")}
            if actual != configuration.get("sha256"):
                conflicts.append("actual configuration hash does not match candidate configuration.sha256")
        else:
            configuration = {**configuration, "checked": False}
    if isinstance(policy, dict) and isinstance(policy.get("path"), str):
        policy_path = candidate_path.parent / policy["path"]
        if policy_path.is_file():
            actual = sha256_file(policy_path)
            policy = {**policy, "checked": True, "actualSha256": actual, "matches": actual == policy.get("sha256")}
            if actual != policy.get("sha256"):
                conflicts.append("actual policy hash does not match candidate policy.sha256")
        else:
            policy = {**policy, "checked": False}

    if missing:
        reasons.append("candidate identity is incomplete: " + ", ".join(sorted(set(missing))))
    if conflicts:
        reasons.append("candidate identity conflicts: " + "; ".join(conflicts))
    return {
        "provided": True,
        "path": str(candidate_path),
        "fileSha256": sha256_bytes(raw),
        "schemaVersion": data.get("schemaVersion"),
        "candidateId": candidate_id,
        "source": source,
        "version": version,
        "configuration": configuration,
        "policy": policy,
        "scripts": scripts,
        "images": images,
        "evidence": evidence,
        "declaredQualification": data.get("qualification"),
        "architecture": architecture,
        "selectedImage": selected_image,
        "actualImageId": image_id,
        "imageIdMatches": bool(
            isinstance(image_id, str)
            and isinstance(selected_image, dict)
            and image_id == selected_image.get("imageId")
        ),
        "archiveCheck": archive_check,
        "scriptChecks": script_checks,
        "missing": sorted(set(missing)),
        "reasons": reasons,
        "conflicts": conflicts,
        "identityComplete": not missing and not conflicts,
    }


def source_matches_candidate(source: dict[str, Any], candidate: dict[str, Any]) -> bool | None:
    if not candidate.get("provided") or not isinstance(candidate.get("source"), dict) or not source.get("head"):
        return None
    return source["head"] == candidate["source"]["commit"]


def load_candidate_policy(candidate: dict[str, Any], repo_root: Path) -> tuple[dict[str, Any] | None, list[str]]:
    """Load the candidate's frozen policy JSON and verify its recorded hash."""
    errors: list[str] = []
    if not candidate.get("provided"):
        return None, ["candidate.json was not supplied"]
    entry = candidate.get("policy")
    if not isinstance(entry, dict) or not entry.get("path") or not entry.get("sha256"):
        return None, ["candidate policy path/hash is missing"]
    relative = Path(str(entry["path"]))
    candidates = [relative] if relative.is_absolute() else [
        Path(str(candidate.get("path"))).parent / relative,
        repo_root / relative,
    ]
    policy_path = next((path for path in candidates if path.is_file()), None)
    if policy_path is None:
        return None, ["candidate policy file is unavailable"]
    try:
        raw = policy_path.read_bytes()
    except OSError as exc:
        return None, [f"candidate policy file cannot be read: {type(exc).__name__}"]
    if sha256_bytes(raw) != entry.get("sha256"):
        errors.append("candidate policy file hash does not match candidate.json")
    try:
        policy = json.loads(raw.decode("utf-8"))
    except Exception as exc:  # noqa: BLE001
        return None, errors + [f"candidate policy JSON is invalid: {type(exc).__name__}"]
    if not isinstance(policy, dict):
        return None, errors + ["candidate policy JSON is not an object"]
    return policy, errors


def policy_mismatches(policy: dict[str, Any], expected: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    for key, value in expected.items():
        if key not in policy:
            errors.append(f"candidate policy is missing {key}")
        elif policy[key] != value:
            errors.append(f"candidate policy {key}={policy[key]!r} does not match runtime {value!r}")
    unresolved = policy.get("unresolvedCriteria")
    if isinstance(unresolved, list) and unresolved:
        errors.append("candidate policy has unresolved criteria: " + ", ".join(str(item) for item in unresolved))
    return errors


def validate_observation_freshness(
    observation: datetime,
    *,
    now: datetime,
    max_freshness_seconds: int,
    future_skew_seconds: int,
) -> list[str]:
    errors: list[str] = []
    age = (now - observation).total_seconds()
    if age > max_freshness_seconds:
        errors.append(f"observation age {age:.0f}s exceeds {max_freshness_seconds}s")
    if age < -future_skew_seconds:
        errors.append(f"observation is {-age:.0f}s in the future; max skew {future_skew_seconds}s")
    return errors


def validate_backoff(status: dict[str, Any], now: datetime) -> list[str]:
    """Check optional failure-status backoff consistency without inventing state."""
    errors: list[str] = []
    if status.get("updateFailed") is not True:
        return errors
    next_retry_text = status.get("nextRetryAt")
    retry_after = status.get("retryAfterSeconds")
    if next_retry_text is None and retry_after is None:
        errors.append("failed status has no nextRetryAt/retryAfterSeconds")
        return errors
    if next_retry_text is not None:
        try:
            next_retry = parse_utc(next_retry_text, "nextRetryAt")
        except VerificationError as exc:
            errors.append(str(exc))
        else:
            if next_retry > now and retry_after is None:
                errors.append("future nextRetryAt lacks retryAfterSeconds")
            if next_retry > now:
                expected = (next_retry - now).total_seconds()
                if retry_after is not None:
                    try:
                        retry_value = float(retry_after)
                    except (TypeError, ValueError):
                        errors.append("retryAfterSeconds is not numeric")
                    else:
                        if abs(expected - retry_value) > 10:
                            errors.append("retryAfterSeconds disagrees with nextRetryAt")
            last_attempt_text = status.get("lastUpdateAttempt")
            if last_attempt_text is not None:
                try:
                    last_attempt = parse_utc(last_attempt_text, "lastUpdateAttempt")
                except VerificationError as exc:
                    errors.append(str(exc))
                else:
                    delay = (next_retry - last_attempt).total_seconds()
                    if delay < 55 or delay > 910:
                        errors.append(f"configured backoff delay {delay:.0f}s is outside the 60..900s policy")
    if retry_after is not None:
        try:
            value = float(retry_after)
        except (TypeError, ValueError):
            errors.append("retryAfterSeconds is not numeric")
        else:
            if value < 1 or value > 900:
                errors.append("retryAfterSeconds is outside 1..900")
    return errors


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:  # noqa: BLE001
        raise VerificationError(f"cannot read JSON {path}: {type(exc).__name__}: {exc}") from exc


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=False) + "\n", encoding="utf-8")


def fetch_and_validate_images(
    frames: list[dict[str, Any]],
    *,
    base_url: str,
    output: Path,
    max_image_bytes: int,
    connect_timeout: float,
    total_timeout: float,
    history_folder: str | None = None,
) -> list[dict[str, Any]]:
    manifest: list[dict[str, Any]] = []
    for frame in frames:
        index = frame["frameIndex"]
        url = frame_url(base_url, frame.get("imageUrl"))
        status, payload, _headers = fetch_bounded(
            url,
            connect_timeout=connect_timeout,
            total_timeout=total_timeout,
            max_bytes=max_image_bytes,
        )
        if status != 200:
            raise VerificationError(f"frame {index} returned HTTP {status}: {url}")
        png = validate_png(payload, url=url, max_bytes=max_image_bytes)
        if history_folder is None:
            destination = output / "frames" / f"frame_{index}.png"
        else:
            destination = output / "history" / "frames" / history_folder / f"frame_{index}.png"
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(payload)
        manifest.append(
            {
                "cacheFolderName": history_folder,
                "frameIndex": index,
                "observationTime": frame.get("absoluteObservationTime"),
                "imageUrl": frame.get("imageUrl"),
                "bytes": len(payload),
                "sha256": sha256_bytes(payload),
                **png,
            }
        )
    return manifest


def _identity_qualification(
    *,
    smoke: bool,
    candidate: dict[str, Any],
    source: dict[str, Any],
    checks_passed: bool,
    policy_errors: list[str] | None = None,
) -> dict[str, Any]:
    reasons: list[str] = []
    if smoke:
        reasons.append("smoke run is not qualification evidence")
    if not candidate.get("provided"):
        reasons.append("candidate.json was not supplied")
    if candidate.get("provided") and not candidate.get("identityComplete"):
        reasons.extend(candidate.get("reasons") or ["candidate identity is incomplete"])
    match = source_matches_candidate(source, candidate)
    if match is False:
        reasons.append("candidate source.commit does not match the local checkout")
    elif match is None:
        reasons.append("local source identity could not be verified")
    if source.get("dirty") is not False:
        reasons.append("local checkout clean state could not be verified")
    if not checks_passed:
        reasons.append("verification checks failed")
    if policy_errors:
        reasons.extend(policy_errors)
    unresolved = list(candidate.get("missing") or []) if candidate.get("provided") else ["candidateJson"]
    return {
        "eligible": checks_passed and not reasons,
        "reasons": reasons,
        "unresolvedCriteria": sorted(set(unresolved)),
    }


def verify_command(args: argparse.Namespace) -> int:
    repo_root = Path(args.repo_root).resolve()
    output = Path(args.output).resolve()
    source = collect_git_identity(repo_root)
    candidate = load_candidate_identity(
        args.candidate_json,
        architecture=args.architecture,
        image_id=args.image_id,
        image_archive=args.image_archive,
        repo_root=repo_root,
    )
    scripts = collect_script_identity(repo_root)
    tools = collect_tool_identity(repo_root)
    candidate_policy, policy_errors = load_candidate_policy(candidate, repo_root)
    if candidate_policy is not None:
        policy_errors += policy_mismatches(candidate_policy, {
            "maxFreshnessSeconds": args.max_freshness_seconds,
            "futureSkewSeconds": args.future_skew_seconds,
            "requestConnectTimeoutSeconds": args.connect_timeout,
            "requestTimeoutSeconds": args.request_timeout,
            "captureWaitSeconds": args.wait_seconds,
        })
    checks: dict[str, Any] = {}
    report: dict[str, Any] = {
        "generatedAt": utc_now().isoformat(),
        "baseUrl": args.base_url,
        "location": {"suburb": args.suburb, "state": args.state},
        "config": {
            "expectedFrames": args.expected_frames,
            "waitSeconds": args.wait_seconds,
            "maxFreshnessSeconds": args.max_freshness_seconds,
            "futureSkewSeconds": args.future_skew_seconds,
            "maxImageBytes": args.max_image_bytes,
            "maxMetadataDistanceSeconds": args.max_metadata_distance_seconds,
            "connectTimeoutSeconds": args.connect_timeout,
            "requestTimeoutSeconds": args.request_timeout,
        },
        "candidate": candidate,
        "source": source,
        "scripts": scripts,
        "tools": tools,
        "policy": {"loaded": candidate_policy is not None, "errors": policy_errors},
        "checks": checks,
        "imageHashes": [],
    }
    try:
        if candidate.get("conflicts"):
            raise VerificationError("; ".join(candidate["conflicts"]))
        if args.health_status != "200":
            raise VerificationError(f"liveness returned HTTP {args.health_status}")
        checks["liveness"] = {"status": 200}
        if args.ready_status != "200":
            raise VerificationError(f"readiness returned HTTP {args.ready_status}")
        checks["readiness"] = validate_ready(read_json(output / "ready.json"))
        latest = read_json(output / "radar-latest.json")
        validated_latest = validate_latest(
            latest,
            expected_frames=args.expected_frames,
            suburb=args.suburb,
            state=args.state,
            max_freshness_seconds=args.max_freshness_seconds,
            future_skew_seconds=args.future_skew_seconds,
            max_metadata_distance_seconds=args.max_metadata_distance_seconds,
        )
        checks["latest"] = {
            "frames": len(validated_latest["frames"]),
            "observationTime": validated_latest["observation"].isoformat(),
            "cacheFolder": validated_latest["cacheFolder"],
        }
        latest_manifest = fetch_and_validate_images(
            validated_latest["frames"],
            base_url=args.base_url,
            output=output,
            max_image_bytes=args.max_image_bytes,
            connect_timeout=args.connect_timeout,
            total_timeout=args.request_timeout,
        )
        write_json(output / "frame-manifest.json", {
            "frames": latest_manifest,
            "observationTime": validated_latest["observation"].isoformat(),
            "cacheFolder": validated_latest["cacheFolder"],
        })

        range_data = read_json(output / "range.json")
        if not isinstance(range_data, dict) or not range_data.get("totalCacheFolders"):
            raise VerificationError("cache range reports no complete folders")
        checks["range"] = {"totalCacheFolders": range_data.get("totalCacheFolders")}

        start = parse_utc(validated_latest["frames"][0].get("absoluteObservationTime"), "history start")
        end = parse_utc(validated_latest["frames"][-1].get("absoluteObservationTime"), "history end")
        history = read_json(output / "history" / "timeseries.json")
        groups = validate_history(
            history,
            start=start,
            end=end,
            expected_frames=args.expected_frames,
            suburb=args.suburb,
            state=args.state,
        )
        history_manifest: list[dict[str, Any]] = []
        for group in groups:
            history_manifest.extend(
                fetch_and_validate_images(
                    group["frames"],
                    base_url=args.base_url,
                    output=output,
                    max_image_bytes=args.max_image_bytes,
                    connect_timeout=args.connect_timeout,
                    total_timeout=args.request_timeout,
                    history_folder=group["cacheFolderName"],
                )
            )
        if not history_manifest:
            raise VerificationError("no historical frames were validated")
        write_json(output / "history" / "frame-manifest.json", history_manifest)
        checks["history"] = {"folders": len(groups), "frames": len(history_manifest)}
        report["imageHashes"] = latest_manifest + history_manifest
        report["status"] = "passed"
    except Exception as exc:  # noqa: BLE001 - write a failed report with the exact error
        report["status"] = "failed"
        report["error"] = f"{type(exc).__name__}: {exc}"
        checks["error"] = report["error"]

    passed = report.get("status") == "passed"
    qualification = _identity_qualification(
        smoke=args.smoke,
        candidate=candidate,
        source=source,
        checks_passed=passed,
        policy_errors=policy_errors,
    )
    report["qualification"] = qualification
    report["smoke"] = bool(args.smoke)
    report["smokePassed"] = bool(args.smoke and passed)
    report["outcome"] = (
        "qualified"
        if qualification["eligible"]
        else "smoke_passed"
        if args.smoke and passed
        else "verification_passed_not_qualified"
        if passed
        else "failed"
    )
    write_json(output / "report.json", report)
    print(json.dumps({
        "status": report["status"],
        "outcome": report["outcome"],
        "qualificationEligible": qualification["eligible"],
        "reasons": qualification["reasons"],
        "report": str(output / "report.json"),
    }, indent=2))
    return 0 if passed else 1


class FailureTracker:
    def __init__(self) -> None:
        self.current: dict[str, Any] | None = None
        self.history: list[dict[str, Any]] = []

    def fail(self, wall: datetime, monotonic: float, message: str) -> None:
        if self.current is None:
            self.current = {"startedAt": wall.isoformat(), "startedMonotonic": monotonic, "lastError": message, "samples": 1}
            self.history.append(self.current)
        else:
            self.current["lastError"] = message
            self.current["samples"] += 1

    def succeed(self, wall: datetime, monotonic: float) -> bool:
        if self.current is None:
            return False
        self.current["recoveredAt"] = wall.isoformat()
        self.current["recoveredMonotonic"] = monotonic
        self.current["durationSeconds"] = max(0.0, monotonic - float(self.current["startedMonotonic"]))
        self.current = None
        return True

    def current_seconds(self, monotonic: float) -> float | None:
        if self.current is None:
            return None
        return max(0.0, monotonic - float(self.current["startedMonotonic"]))


def docker_value(container: str, command: list[str]) -> str | None:
    if not container:
        return None
    try:
        result = subprocess.run(
            ["docker", *command], check=False, capture_output=True, text=True, timeout=20
        )
        return result.stdout.strip() if result.returncode == 0 else None
    except Exception:  # noqa: BLE001 - a missing metric is recorded, not hidden
        return None


def sample_resources(container: str) -> tuple[int | None, int | None, int | None]:
    memory = parse_memory(docker_value(container, ["stats", "--no-stream", "--format", "{{.MemUsage}}", container]))
    processes_text = docker_value(container, ["exec", container, "sh", "-c", "ps -eo comm | grep -Ec '^(chrome|chromium|headless_shell)$' || true"])
    try:
        processes = int(processes_text) if processes_text is not None else None
    except ValueError:
        processes = None
    cache_text = docker_value(container, ["exec", container, "sh", "-c", "du -sb /app/cache 2>/dev/null | cut -f1"])
    try:
        cache_bytes = int(cache_text) if cache_text else None
    except ValueError:
        cache_bytes = None
    return memory, processes, cache_bytes


def evaluate_qualification(summary: dict[str, Any], policy: dict[str, Any], smoke: bool) -> dict[str, Any]:
    """Pure gate used by the soak and its unit tests."""
    reasons: list[str] = []
    unresolved: list[str] = []
    elapsed = summary.get("actualElapsedSeconds", summary.get("elapsedSeconds", 0))
    if smoke:
        reasons.append("smoke run is not qualification evidence")
    if summary.get("interrupted"):
        reasons.append("interrupted before completion")
    if elapsed < policy["requiredSeconds"]:
        reasons.append(f"actual elapsed {elapsed:.0f}s is below required {policy['requiredSeconds']}s")
    if elapsed < policy["requestedSeconds"]:
        reasons.append("actual elapsed is below requested duration")
    if summary.get("validatedSamples", 0) == 0:
        reasons.append("no validated frame")
    if summary.get("observationRegressions", 0) > 0:
        reasons.append("observation time regressed")
    since_forward = summary.get("secondsSinceLastForward")
    if since_forward is not None and since_forward > policy["maxNoProgressSeconds"]:
        reasons.append(f"no forward observation progress for {since_forward:.0f}s")
    if summary.get("maxForwardGapSeconds") is not None and summary["maxForwardGapSeconds"] > policy["maxNoProgressSeconds"]:
        reasons.append(f"historic forward-progress gap reached {summary['maxForwardGapSeconds']:.0f}s")
    if summary.get("backoffErrors"):
        reasons.extend(f"backoff: {error}" for error in summary["backoffErrors"])
    if summary.get("policyErrors"):
        reasons.extend(summary["policyErrors"])
    if summary.get("currentFailureSeconds") is not None and summary["currentFailureSeconds"] > policy["recoverySeconds"]:
        reasons.append("failure did not recover within the recovery window")
    if summary.get("maxRecoverySeconds") is not None and summary["maxRecoverySeconds"] > policy["recoverySeconds"]:
        reasons.append("a recorded failure took longer than the recovery window to recover")
    if policy.get("container"):
        if not summary.get("metricsComplete"):
            reasons.append("container resource metrics are incomplete")
        if summary.get("maxMemoryBytes") is None:
            reasons.append("container memory metric is missing")
        if summary.get("maxMemoryBytes") is not None and summary["maxMemoryBytes"] > 2 * 1024**3:
            reasons.append("container memory exceeded the configured 2 GiB cap")
    else:
        unresolved.append("container resource sampling not supplied")
    for key, label in (
        ("maxMemoryMiB", "maxMemoryMiB"),
        ("maxBrowserProcesses", "maxBrowserProcesses"),
        ("maxCacheGrowthMiB", "maxCacheGrowthMiB"),
    ):
        if policy.get(key) is None:
            unresolved.append(label)
    if policy.get("maxMemoryMiB") is not None and summary.get("maxMemoryBytes") is not None:
        if summary["maxMemoryBytes"] > policy["maxMemoryMiB"] * 1024**2:
            reasons.append("memory exceeded the supplied empirical maximum")
    if policy.get("maxBrowserProcesses") is not None and summary.get("maxBrowserProcesses") is not None:
        if summary["maxBrowserProcesses"] > policy["maxBrowserProcesses"]:
            reasons.append("browser process count exceeded the supplied empirical maximum")
    if policy.get("maxCacheGrowthMiB") is not None:
        start, end = summary.get("cacheBytesStart"), summary.get("cacheBytesEnd")
        if start is not None and end is not None and (end - start) > policy["maxCacheGrowthMiB"] * 1024**2:
            reasons.append("cache growth exceeded the supplied empirical maximum")
    if not summary.get("candidateIdentityComplete"):
        reasons.append("candidate identity is incomplete or not supplied")
        reasons.extend(summary.get("candidateReasons") or [])
    if summary.get("candidateConflicts"):
        reasons.extend(summary["candidateConflicts"])
    if summary.get("sourceMatchesCandidate") is False:
        reasons.append("candidate source.commit does not match the local checkout")
    elif summary.get("sourceMatchesCandidate") is not True:
        reasons.append("local source identity could not be verified")
    if summary.get("sourceDirty") is not False:
        reasons.append("local checkout clean state could not be verified")
    if unresolved:
        reasons.append("unresolved criteria: " + ", ".join(unresolved))
    return {
        "eligible": not reasons,
        "reasons": reasons,
        "unresolvedCriteria": unresolved,
        "checks": {
            "interrupted": bool(summary.get("interrupted")),
            "elapsedSeconds": summary.get("actualElapsedSeconds", summary.get("elapsedSeconds")),
            "validatedSamples": summary.get("validatedSamples"),
            "observationAdvances": summary.get("observationAdvances"),
            "observationRegressions": summary.get("observationRegressions"),
            "metricsComplete": summary.get("metricsComplete"),
        },
    }


def soak_command(args: argparse.Namespace) -> int:
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    repo_root = Path(args.repo_root).resolve()
    source = collect_git_identity(repo_root)
    candidate = load_candidate_identity(
        args.candidate_json,
        architecture=args.architecture,
        image_id=args.image_id,
        image_archive=args.image_archive,
        repo_root=repo_root,
    )
    source_match = source_matches_candidate(source, candidate)
    scripts = collect_script_identity(repo_root)
    tools = collect_tool_identity(repo_root)
    duration_seconds = float(args.duration_hours) * 3600.0
    required_seconds = max(30.0 * 3600.0, duration_seconds)
    candidate_policy, policy_errors = load_candidate_policy(candidate, repo_root)
    if candidate_policy is not None:
        policy_errors += policy_mismatches(candidate_policy, {
            "maxFreshnessSeconds": args.max_freshness_seconds,
            "futureSkewSeconds": args.future_skew_seconds,
            "recoverySeconds": args.recovery_seconds,
            "maxProgressGapSeconds": args.max_no_progress_seconds,
            "requestConnectTimeoutSeconds": args.connect_timeout,
            "requestTimeoutSeconds": args.request_timeout,
            "cpuLimit": 2.0,
            "memoryLimitBytes": 2 * 1024**3,
        })
        policy_duration = candidate_policy.get("qualificationDurationSeconds")
        if not isinstance(policy_duration, (int, float)) or policy_duration <= 0:
            policy_errors.append("candidate policy qualificationDurationSeconds is missing")
        elif duration_seconds < float(policy_duration):
            policy_errors.append("requested duration is below the candidate policy qualification duration")
    policy = {
        "requestedSeconds": duration_seconds,
        "requiredSeconds": required_seconds,
        "maxNoProgressSeconds": args.max_no_progress_seconds,
        "recoverySeconds": args.recovery_seconds,
        "maxFreshnessSeconds": args.max_freshness_seconds,
        "futureSkewSeconds": args.future_skew_seconds,
        "container": args.container or None,
        "maxMemoryMiB": args.max_memory_mib,
        "maxBrowserProcesses": args.max_browser_processes,
        "maxCacheGrowthMiB": args.max_cache_growth_mib,
    }
    stop = False

    def handle_signal(_signum: int, _frame: Any) -> None:
        nonlocal stop
        stop = True

    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)
    started_wall = utc_now()
    started_mono = time.monotonic()
    deadline = started_mono + duration_seconds
    samples_path = output / "samples.jsonl"
    summary_path = output / "summary.json"
    base = args.base_url.rstrip("/")
    encoded_suburb = urllib.parse.quote(args.suburb, safe="")
    encoded_state = urllib.parse.quote(args.state, safe="")
    urls = {
        "health": f"{base}/api/health",
        "ready": f"{base}/api/ready",
        "status": f"{base}/api/cache/{encoded_suburb}/{encoded_state}/status",
        "radar": f"{base}/api/radar/{encoded_suburb}/{encoded_state}",
        "refresh": f"{base}/api/cache/{encoded_suburb}/{encoded_state}/refresh",
    }
    tracker = FailureTracker()
    sample_index = 0
    radar_samples = 0
    validated_samples = 0
    failed_samples = 0
    frame_checks = 0
    frame_failures = 0
    transport_failures = 0
    observations: list[str] = []
    observation_set: set[str] = set()
    last_observation: datetime | None = None
    first_observation: str | None = None
    advances = 0
    regressions = 0
    first_validated_mono: float | None = None
    first_validated_wall: datetime | None = None
    last_forward_mono: float | None = None
    max_forward_gap_seconds = 0.0
    backoff_errors: list[str] = []
    max_queue_depth = 0
    max_memory: int | None = None
    max_processes: int | None = None
    cache_start: int | None = None
    cache_end: int | None = None
    metrics_complete = True
    metric_misses = 0
    current_error: str | None = None
    fatal_error: str | None = None
    samples_file = samples_path.open("w", encoding="utf-8")
    try:
        while not stop and time.monotonic() < deadline:
            sample_index += 1
            now_wall = utc_now()
            now_mono = time.monotonic()
            # Preserve the entire interval before a fresh observation can
            # reset the progression clock later in this sample.
            if last_forward_mono is not None:
                max_forward_gap_seconds = max(max_forward_gap_seconds, now_mono - last_forward_mono)
            transport_errors: list[str] = []
            health_status, _, health_error, health_transport = request_json(
                urls["health"], connect_timeout=args.connect_timeout, total_timeout=args.request_timeout, max_bytes=1024 * 1024
            )
            if health_transport:
                transport_failures += 1
                transport_errors.append(f"health: {health_error}")
            elif health_status != 200:
                transport_errors.append(f"health returned HTTP {health_status}")
            ready_status, ready, ready_error, ready_transport = request_json(
                urls["ready"], connect_timeout=args.connect_timeout, total_timeout=args.request_timeout, max_bytes=1024 * 1024
            )
            if ready_transport:
                transport_failures += 1
                transport_errors.append(f"ready: {ready_error}")
            elif ready_status != 200:
                transport_errors.append(f"ready returned HTTP {ready_status}")
            elif not isinstance(ready, dict) or ready.get("status") != "ready":
                transport_errors.append(f"ready payload is not ready: {ready!r}")
            status_code, status, status_error, status_transport = request_json(
                urls["status"], connect_timeout=args.connect_timeout, total_timeout=args.request_timeout, max_bytes=1024 * 1024
            )
            if status_transport:
                transport_failures += 1
                transport_errors.append(f"status: {status_error}")
            elif status_code != 200:
                transport_errors.append(f"status returned HTTP {status_code}")
            protocol_errors: list[str] = []
            if isinstance(status, dict):
                for error in validate_backoff(status, now_wall):
                    backoff_errors.append(error)
                    protocol_errors.append(f"backoff: {error}")
            radar_status, radar, radar_error, radar_transport = request_json(
                urls["radar"], connect_timeout=args.connect_timeout, total_timeout=args.request_timeout, max_bytes=4 * 1024 * 1024
            )
            if radar_transport:
                transport_failures += 1
                transport_errors.append(f"radar: {radar_error}")
            if radar_status == 404:
                refresh_status, _, refresh_error, refresh_transport = request_json(
                    urls["refresh"], method="POST", connect_timeout=args.connect_timeout,
                    total_timeout=args.request_timeout, max_bytes=1024 * 1024,
                )
                if refresh_transport:
                    transport_failures += 1
                    transport_errors.append(f"refresh: {refresh_error}")
                if refresh_status not in (None, 200, 201, 202):
                    transport_errors.append(f"refresh returned HTTP {refresh_status}")
                time.sleep(2)
                radar_status, radar, radar_error, radar_transport = request_json(
                    urls["radar"], connect_timeout=args.connect_timeout, total_timeout=args.request_timeout, max_bytes=4 * 1024 * 1024
                )
                if radar_transport:
                    transport_failures += 1
                    transport_errors.append(f"radar-retry: {radar_error}")
            frames = radar.get("frames") if isinstance(radar, dict) else None
            observation = radar.get("observationTime") if isinstance(radar, dict) else None
            radar_ok = radar_status == 200 and isinstance(frames, list) and bool(frames) and isinstance(observation, str)
            frame_check: dict[str, Any] | None = None
            frame_error: str | None = None
            if radar_ok:
                radar_samples += 1
                image_url = frames[-1].get("imageUrl") if isinstance(frames[-1], dict) else None
                frame_checks += 1
                try:
                    url = frame_url(base, image_url)
                    image_status, image_body, _ = fetch_bounded(
                        url, connect_timeout=args.connect_timeout, total_timeout=args.request_timeout,
                        max_bytes=args.max_image_bytes,
                    )
                    if image_status != 200:
                        frame_error = f"frame returned HTTP {image_status}"
                    else:
                        frame_check = validate_png(image_body, url=url, max_bytes=args.max_image_bytes)
                except Exception as exc:  # noqa: BLE001
                    frame_error = f"{type(exc).__name__}: {exc}"
                if frame_check is None:
                    frame_failures += 1
                    transport_errors.append(f"frame: {frame_error}")
            else:
                if radar_error:
                    transport_errors.append(f"radar response: {radar_error}")
                elif radar_status is not None:
                    transport_errors.append(f"radar returned HTTP {radar_status}")
            service_ok = not transport_errors and not protocol_errors
            validated = frame_check is not None and service_ok
            if validated:
                try:
                    observed = parse_utc(observation, "observationTime")
                except VerificationError as exc:
                    frame_error = str(exc)
                    validated = False
                else:
                    freshness_errors = validate_observation_freshness(
                        observed,
                        now=now_wall,
                        max_freshness_seconds=args.max_freshness_seconds,
                        future_skew_seconds=args.future_skew_seconds,
                    )
                    if freshness_errors:
                        frame_error = "; ".join(freshness_errors)
                        validated = False
                    else:
                        validated_samples += 1
                        if first_validated_mono is None:
                            first_validated_mono = now_mono
                            first_validated_wall = now_wall
                            last_forward_mono = now_mono
                        if last_observation is None:
                            first_observation = observation
                        elif observed > last_observation:
                            advances += 1
                            last_forward_mono = now_mono
                        elif observed < last_observation:
                            regressions += 1
                        if last_observation is None or observed >= last_observation:
                            last_observation = observed
                        if observation not in observation_set:
                            observation_set.add(observation)
                            observations.append(observation)
                        current_error = None
                        tracker.succeed(now_wall, now_mono)
            if not validated:
                failed_samples += 1
                current_error = frame_error or (transport_errors[-1] if transport_errors else
                                                protocol_errors[-1] if protocol_errors else "sample not validated")
                tracker.fail(now_wall, now_mono, current_error)
            memory, processes, cache_bytes = sample_resources(args.container or "")
            if args.container:
                if memory is None or processes is None or cache_bytes is None:
                    metric_misses += 1
                    metrics_complete = False
                if cache_start is None and cache_bytes is not None:
                    cache_start = cache_bytes
                if cache_bytes is not None:
                    cache_end = cache_bytes
                if memory is not None:
                    max_memory = memory if max_memory is None else max(max_memory, memory)
                if processes is not None:
                    max_processes = processes if max_processes is None else max(max_processes, processes)
            status_dict = status if isinstance(status, dict) else {}
            ready_dict = ready if isinstance(ready, dict) else {}
            max_queue_depth = max(max_queue_depth, int(status_dict.get("queueDepth") or ready_dict.get("queueDepth") or 0))
            observation_age_seconds = None
            if isinstance(observation, str):
                try:
                    observation_age_seconds = (now_wall - parse_utc(observation, "observationTime")).total_seconds()
                except VerificationError:
                    observation_age_seconds = None
            record = {
                "sampledAt": now_wall.isoformat(),
                "sample": sample_index,
                "healthStatus": health_status,
                "readyStatus": ready_status,
                "radarStatus": radar_status,
                "frameCount": len(frames) if isinstance(frames, list) else 0,
                "observationTime": observation,
                "observationAgeSeconds": observation_age_seconds,
                "cacheIsValid": radar.get("cacheIsValid") if isinstance(radar, dict) else None,
                "isUpdating": status.get("isUpdating") if isinstance(status, dict) else None,
                "updateFailed": status.get("updateFailed") if isinstance(status, dict) else None,
                "errorCode": status.get("errorCode") if isinstance(status, dict) else None,
                "nextRetryAt": status.get("nextRetryAt") if isinstance(status, dict) else None,
                "retryAfterSeconds": status.get("retryAfterSeconds") if isinstance(status, dict) else None,
                "lastUpdateAttempt": status.get("lastUpdateAttempt") if isinstance(status, dict) else None,
                "lastSuccessfulCaptureAt": status.get("lastSuccessfulCaptureAt") if isinstance(status, dict) else None,
                "queueDepth": status.get("queueDepth") if isinstance(status, dict) else None,
                "memoryBytes": memory,
                "browserProcesses": processes,
                "cacheBytes": cache_bytes,
                "frameCheck": frame_check,
                "frameError": frame_error,
                "validated": validated,
                "transportErrors": transport_errors,
                "protocolErrors": protocol_errors,
                "lastError": current_error,
            }
            samples_file.write(json.dumps(record) + "\n")
            samples_file.flush()
            print(json.dumps({k: record[k] for k in ("sample", "radarStatus", "observationTime", "updateFailed", "validated")}), flush=True)
            remaining = deadline - time.monotonic()
            if remaining > 0:
                time.sleep(min(max(5, int(args.interval_seconds)), remaining))
    except Exception as exc:  # noqa: BLE001 - preserve a failure summary and exit nonzero
        fatal_error = f"{type(exc).__name__}: {exc}"
        stop = True
    finally:
        samples_file.close()
    elapsed_seconds = max(0.0, time.monotonic() - started_mono)
    current_failure_seconds = tracker.current_seconds(time.monotonic())
    seconds_since_first = (time.monotonic() - first_validated_mono) if first_validated_mono is not None else None
    seconds_since_forward = (time.monotonic() - last_forward_mono) if last_forward_mono is not None else None
    if seconds_since_forward is not None:
        max_forward_gap_seconds = max(max_forward_gap_seconds, seconds_since_forward)
    completed_requested = bool(not stop and elapsed_seconds >= duration_seconds)
    summary: dict[str, Any] = {
        "status": "completed" if completed_requested else "interrupted",
        "startedAt": started_wall.isoformat(),
        "finishedAt": utc_now().isoformat(),
        "durationHours": round(elapsed_seconds / 3600.0, 3),
        "actualElapsedSeconds": round(elapsed_seconds, 3),
        "elapsedSeconds": round(elapsed_seconds, 3),
        "requestedHours": duration_seconds / 3600.0,
        "requiredHours": required_seconds / 3600.0,
        "interrupted": bool(stop),
        "completedRequestedDuration": completed_requested,
        "samples": sample_index,
        "radarSamples": radar_samples,
        "successfulSamples": validated_samples,
        "validatedSamples": validated_samples,
        "failedSamples": failed_samples,
        "uniqueObservations": len(observations),
        "firstObservation": first_observation,
        "lastObservation": last_observation.isoformat() if last_observation else None,
        "observationAdvances": advances,
        "observationRegressions": regressions,
        "firstValidatedAt": first_validated_wall.isoformat() if first_validated_wall else None,
        "secondsSinceFirstValidated": seconds_since_first,
        "secondsSinceLastForward": seconds_since_forward,
        "maxForwardGapSeconds": round(max_forward_gap_seconds, 3),
        "frameChecks": frame_checks,
        "frameFailures": frame_failures,
        "transportFailures": transport_failures,
        "backoffErrors": backoff_errors,
        "policyErrors": policy_errors,
        "candidatePolicy": candidate_policy,
        "recoveries": sum(1 for item in tracker.history if item.get("recoveredAt")),
        "failureHistory": tracker.history,
        "maxRecoverySeconds": max(
            (float(item["durationSeconds"]) for item in tracker.history if item.get("durationSeconds") is not None),
            default=None,
        ),
        "currentError": current_error,
        "currentFailureSeconds": current_failure_seconds,
        "maxQueueDepth": max_queue_depth,
        "maxMemoryBytes": max_memory,
        "maxBrowserProcesses": max_processes,
        "cacheBytesStart": cache_start,
        "cacheBytesEnd": cache_end,
        "metricMisses": metric_misses,
        "metricsComplete": metrics_complete if args.container else False,
        "resourceCaps": {"cpu": 2.0, "memoryBytes": 2 * 1024**3, "source": "compose defaults"},
        "candidate": candidate,
        "source": source,
        "scripts": scripts,
        "tools": tools,
        "policy": policy,
        "samplesFile": str(samples_path),
        "fatalError": fatal_error,
    }
    summary["sourceMatchesCandidate"] = source_match
    summary["sourceDirty"] = source.get("dirty")
    summary["candidateReasons"] = candidate.get("reasons") or []
    summary["candidateConflicts"] = candidate.get("conflicts") or []
    summary["candidateIdentityComplete"] = bool(candidate.get("identityComplete") and not candidate.get("conflicts"))
    qualification = evaluate_qualification(summary, policy, args.smoke)
    summary["qualification"] = qualification
    summary["smoke"] = bool(args.smoke)
    smoke_checks_ok = bool(
        not stop
        and elapsed_seconds >= duration_seconds
        and validated_samples > 0
        and regressions == 0
        and not backoff_errors
        and fatal_error is None
        and not candidate.get("conflicts")
    )
    summary["smokePassed"] = bool(args.smoke and smoke_checks_ok)
    summary["outcome"] = (
        "qualified"
        if qualification["eligible"]
        else "smoke_passed"
        if args.smoke and smoke_checks_ok
        else "not_qualified"
    )
    write_json(summary_path, summary)
    print(json.dumps({
        "outcome": summary["outcome"],
        "actualElapsedSeconds": summary["actualElapsedSeconds"],
        "requestedHours": summary["requestedHours"],
        "successfulSamples": summary["successfulSamples"],
        "observationAdvances": summary["observationAdvances"],
        "qualification": qualification,
        "summary": str(summary_path),
    }, indent=2))
    if fatal_error:
        return 1
    if args.smoke:
        return 0 if smoke_checks_ok else 1
    return 0 if qualification["eligible"] else 1


def parse_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--repo-root", default=str(Path(__file__).resolve().parent.parent))
    parser.add_argument("--candidate-json", default=None)
    parser.add_argument("--architecture", choices=("amd64", "arm64"), default=None)
    parser.add_argument("--image-id", default=None)
    parser.add_argument("--image-archive", default=None)
    parser.add_argument("--smoke", action="store_true")
    parser.add_argument("--connect-timeout", type=float, default=5.0)
    parser.add_argument("--request-timeout", type=float, default=30.0)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="BOM Local bounded verification and soak helpers")
    subparsers = parser.add_subparsers(dest="command", required=True)
    verify = subparsers.add_parser("verify")
    parse_common(verify)
    verify.add_argument("--base-url", required=True)
    verify.add_argument("--suburb", default="Pomona")
    verify.add_argument("--state", default="QLD")
    verify.add_argument("--output", default="artifacts/verification")
    verify.add_argument("--expected-frames", type=int, default=7)
    verify.add_argument("--wait-seconds", type=int, default=660)
    verify.add_argument("--max-freshness-seconds", type=int, default=1800)
    verify.add_argument("--future-skew-seconds", type=int, default=120)
    verify.add_argument("--max-image-bytes", type=int, default=DEFAULT_MAX_IMAGE_BYTES)
    verify.add_argument("--max-metadata-distance-seconds", type=int, default=DEFAULT_MAX_METADATA_DISTANCE_SECONDS)
    verify.add_argument("--health-status", default="200")
    verify.add_argument("--ready-status", default="200")
    soak = subparsers.add_parser("soak")
    parse_common(soak)
    soak.add_argument("--base-url", required=True)
    soak.add_argument("--suburb", default="Pomona")
    soak.add_argument("--state", default="QLD")
    soak.add_argument("--output", default="artifacts/soak")
    soak.add_argument("--duration-hours", type=float, default=30.0)
    soak.add_argument("--interval-seconds", type=int, default=60)
    soak.add_argument("--container", default="")
    soak.add_argument("--max-freshness-seconds", type=int, default=1800)
    soak.add_argument("--future-skew-seconds", type=int, default=120)
    soak.add_argument("--max-no-progress-seconds", type=int, default=3600)
    soak.add_argument("--recovery-seconds", type=int, default=1800)
    soak.add_argument("--max-image-bytes", type=int, default=DEFAULT_MAX_IMAGE_BYTES)
    soak.add_argument("--max-memory-mib", type=float, default=None)
    soak.add_argument("--max-browser-processes", type=int, default=None)
    soak.add_argument("--max-cache-growth-mib", type=float, default=None)
    args = parser.parse_args(argv)
    try:
        return verify_command(args) if args.command == "verify" else soak_command(args)
    except Exception as exc:  # noqa: BLE001
        print(f"ERROR: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
