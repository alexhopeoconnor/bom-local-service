#!/usr/bin/env python3
"""Bounded native-smoke wrapper for the verified container runner.

This is a test-command for the runner. It does not start, stop, remove or clean
any Docker resource. It only reads the runner ownership manifest, locates the
single labelled service container, inspects its immutable image ID, seeds the
runner-owned cache, exercises the service and runs Chromium inside the image.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.parse
import zlib
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import service_verification as sv  # noqa: E402
import service_test_runner as runner  # noqa: E402


RUN_ID_RE = re.compile(r"\d{8}T\d{6}Z-[0-9a-f]{8}")
IMAGE_ID_RE = re.compile(r"sha256:[0-9a-f]{64}")
CONTAINER_ID_RE = re.compile(r"[0-9a-f]{64}")
TOKEN_LABEL = "io.bom.test.owner-token"
RUN_LABEL = "io.bom.test.run-id"
PROJECT_LABEL = "com.docker.compose.project"
ROLE_LABEL = "io.bom.test.role"
IMAGE_CAP = 8 * 1024 * 1024
DOCKER_TIMEOUT = 20
BROWSER_TIMEOUT = 120
JS_PAYLOAD = r"""
const packagePath = '/app/.playwright/package';
const pkg = require(packagePath + '/package.json');
const { chromium } = require(packagePath);
(async () => {
  let browser;
  try {
    browser = await chromium.launch({
      headless: true,
      args: ['--no-sandbox', '--disable-dev-shm-usage']
    });
    const page = await browser.newPage({ viewport: { width: 64, height: 64 } });
    await page.setContent(
      '<!doctype html><html><body style="margin:0;background:#123456">' +
      '<div style="width:32px;height:32px;background:#f0a000"></div></body></html>'
    );
    const png = await page.screenshot({ type: 'png' });
    process.stdout.write(JSON.stringify({
      version: pkg.version,
      pngBase64: png.toString('base64')
    }));
  } finally {
    if (browser) await browser.close();
  }
})().catch((error) => {
  console.error(error && error.stack ? error.stack : String(error));
  process.exit(1);
});
"""


class SmokeError(RuntimeError):
    pass


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def utc_text(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def png_2x2(red: int, green: int, blue: int) -> bytes:
    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            len(payload).to_bytes(4, "big") + kind + payload
            + (zlib.crc32(kind + payload) & 0xFFFFFFFF).to_bytes(4, "big")
        )

    raw = b"".join(
        b"\x00" + bytes((red, green, blue, red, green, blue))
        for _ in range(2)
    )
    ihdr = (2).to_bytes(4, "big") + (2).to_bytes(4, "big") + bytes((8, 2, 0, 0, 0))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


def run_bounded(command: list[str], *, timeout: float, input_bytes: bytes | None = None, max_output: int = 32 * 1024 * 1024, env: dict[str, str] | None = None) -> subprocess.CompletedProcess:
    try:
        completed = subprocess.run(
            command,
            input=input_bytes,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=timeout,
            check=False,
            env=env,
        )
    except subprocess.TimeoutExpired as exc:
        raise SmokeError(f"command exceeded {timeout}s: {command[0]}") from exc
    except OSError as exc:
        raise SmokeError(f"cannot start {command[0]}: {type(exc).__name__}: {exc}") from exc
    if len(completed.stdout) > max_output or len(completed.stderr) > max_output:
        raise SmokeError(f"command output exceeded {max_output} bytes: {command[0]}")
    return completed


def docker_json(container_id: str, docker_env: dict[str, str]) -> dict:
    completed = run_bounded(["docker", "inspect", container_id], timeout=DOCKER_TIMEOUT, max_output=4 * 1024 * 1024, env=docker_env)
    if completed.returncode != 0:
        raise SmokeError(f"docker inspect failed for {container_id}: {completed.stderr.decode('utf-8', 'replace')[:500]}")
    try:
        data = json.loads(completed.stdout.decode("utf-8"))
    except ValueError as exc:
        raise SmokeError(f"docker inspect returned invalid JSON: {exc}") from exc
    if not isinstance(data, list) or len(data) != 1 or not isinstance(data[0], dict):
        raise SmokeError("docker inspect did not return exactly one container object")
    return data[0]


def find_owned_container(run_id: str, token: str, docker_env: dict[str, str]) -> tuple[str, dict]:
    completed = run_bounded(
        [
            "docker", "ps", "-aq", "--no-trunc",
            "--filter", f"label={RUN_LABEL}={run_id}",
            "--filter", f"label={TOKEN_LABEL}={token}",
        ],
        timeout=DOCKER_TIMEOUT,
        max_output=64 * 1024,
        env=docker_env,
    )
    if completed.returncode != 0:
        raise SmokeError(f"docker ps failed: {completed.stderr.decode('utf-8', 'replace')[:500]}")
    ids = [line.strip() for line in completed.stdout.decode("utf-8").splitlines() if line.strip()]
    if len(ids) != 1:
        raise SmokeError(f"expected exactly one owned container, found {len(ids)}")
    container_id = ids[0]
    if not CONTAINER_ID_RE.fullmatch(container_id):
        raise SmokeError(f"unexpected container id: {container_id!r}")
    return container_id, docker_json(container_id, docker_env)


def seed_snapshot(cache_dir: Path, folder: str, suburb: str, state: str, observation: datetime) -> dict:
    location = sv.assert_cache_folder_owned(folder, suburb, state)
    folder_path = cache_dir / folder
    if folder_path.exists():
        raise SmokeError(f"refusing to overwrite existing cache folder: {folder_path}")
    radar = folder_path / "radar"
    radar.mkdir(parents=True)
    frames = [observation - timedelta(minutes=30) + timedelta(minutes=5 * index) for index in range(7)]
    for index, _observed in enumerate(frames):
        png = png_2x2(20 + index * 17, 80 + index * 9, 140 - index * 7)
        sv.validate_png(png, url=f"{folder}/frame_{index}.png")
        (radar / f"frame_{index}.png").write_bytes(png)
    metadata = {
        "ObservationTime": utc_text(observation),
        "ForecastTime": utc_text(observation - timedelta(hours=1)),
        "WeatherStation": "Native Smoke Station",
        "Distance": "10 km",
    }
    (folder_path / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    frame_metadata = [
        {"FrameIndex": index, "ObservationTime": utc_text(observed)}
        for index, observed in enumerate(frames)
    ]
    (radar / "frames.json").write_text(json.dumps(frame_metadata, indent=2) + "\n", encoding="utf-8")
    (folder_path / ".complete").write_text("{}\n", encoding="utf-8")
    return {
        "folder": folder,
        "suburb": location["location"]["suburb"],
        "state": location["location"]["state"],
        "encoded": location["encoded"],
        "frames": len(frames),
        "firstObservation": utc_text(frames[0]),
        "lastObservation": utc_text(frames[-1]),
    }


def request_json(url: str) -> tuple[int, dict]:
    status, data, error, transport = sv.request_json(
        url, connect_timeout=5.0, total_timeout=30.0, max_bytes=4 * 1024 * 1024
    )
    if transport or status != 200 or not isinstance(data, dict):
        raise SmokeError(f"request failed: {url}: status={status} error={error}")
    return status, data


def main() -> int:
    run_id = (os.environ.get("BOM_TEST_RUN_ID") or "").strip()
    token = (os.environ.get("BOM_TEST_OWNER_TOKEN") or "").strip()
    cache_env = (os.environ.get("BOM_TEST_CACHE_DIR") or "").strip()
    base = (os.environ.get("BOM_TEST_BASE_URL") or os.environ.get("BOM_TEST_URL") or os.environ.get("BOM_BASE_URL") or "").strip().rstrip("/")
    if not RUN_ID_RE.fullmatch(run_id):
        print("invalid or missing BOM_TEST_RUN_ID", file=sys.stderr)
        return 2
    if not token or not cache_env or not base:
        print("missing BOM_TEST_OWNER_TOKEN, BOM_TEST_CACHE_DIR or base URL", file=sys.stderr)
        return 2
    parsed = urllib.parse.urlsplit(base)
    if parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost", "::1"):
        print(f"refusing non-loopback base URL: {base}", file=sys.stderr)
        return 2

    repo_root = Path(__file__).resolve().parent.parent
    run_dir = repo_root / "artifacts" / "execution" / "runner" / "runs" / run_id
    output = run_dir / "native-smoke"
    output.mkdir(parents=True, exist_ok=True)
    report_path = output / "report.json"
    png_path = output / "native-smoke.png"
    report: dict = {
        "schema": "native-smoke/v1",
        "runId": run_id,
        "generatedAt": utc_text(sv.utc_now()),
        "mode": "container",
        "status": "failed",
        "scope": {
            "name": "delivery-wrapper",
            "deliveryWrapper": True,
            "selfCertifiesCandidate": False,
            "fullCandidate": False,
            "cleanupPerformed": False,
            "qualification": False,
        },
        "image": {},
        "ownership": {},
        "checks": {},
        "artifacts": {"png": png_path.name, "report": report_path.name},
    }
    try:
        manifest_path = run_dir / "ownership.json"
        if not manifest_path.is_file() or manifest_path.is_symlink():
            raise SmokeError(f"ownership manifest is missing or unsafe: {manifest_path}")
        manifest_bytes = manifest_path.read_bytes()
        manifest = runner.load_manifest(run_dir, require_binding=True)
        if not isinstance(manifest, dict) or manifest.get("mode") != "container":
            raise SmokeError("ownership manifest is not a container run")
        if manifest.get("runId") != run_id or manifest.get("ownerToken") != token:
            raise SmokeError("ownership manifest run/token does not match environment")
        if manifest.get("cacheOwned") is not True:
            raise SmokeError("ownership manifest does not declare an owned cache")
        cache_dir = Path(manifest.get("cacheDir") or "").resolve()
        if cache_dir != Path(cache_env).resolve():
            raise SmokeError("BOM_TEST_CACHE_DIR does not match the ownership manifest")
        container_manifest = manifest.get("container")
        if not isinstance(container_manifest, dict):
            raise SmokeError("ownership manifest has no container identity")
        image_id = str(container_manifest.get("imageId") or "")
        project = str(container_manifest.get("project") or "")
        if not IMAGE_ID_RE.fullmatch(image_id):
            raise SmokeError("ownership manifest imageId is not exact")
        if not project:
            raise SmokeError("ownership manifest has no compose project")
        report["image"] = {"manifestImageId": image_id, "project": project}
        report["ownership"] = {
            "manifestSha256": sha256_bytes(manifest_bytes),
            "runId": run_id,
            "tokenMatches": True,
        }
        endpoint = runner.docker_endpoint_matches(manifest)
        if not endpoint.get("ok"):
            raise SmokeError(f"pinned Docker endpoint cannot be verified: {endpoint.get('error')}")
        docker_env = runner.docker_env_from_manifest(manifest)
        report["ownership"]["dockerEndpointVerified"] = True
        container_id, inspected = find_owned_container(run_id, token, docker_env)
        labels = (inspected.get("Config") or {}).get("Labels") or {}
        state = inspected.get("State") or {}
        if inspected.get("Id") != container_id:
            raise SmokeError("docker inspect id does not match the listed container")
        if inspected.get("Image") != image_id:
            raise SmokeError("container image ID does not match the immutable manifest")
        if labels.get(RUN_LABEL) != run_id or labels.get(TOKEN_LABEL) != token:
            raise SmokeError("container ownership labels do not match")
        if labels.get(PROJECT_LABEL) != project:
            raise SmokeError("container compose project label does not match")
        if labels.get("io.bom.test.runner-version") != manifest.get("runnerVersion"):
            raise SmokeError("container runner-version label does not match the ownership manifest")
        if labels.get(ROLE_LABEL) != "service":
            raise SmokeError("container role label is not service")
        if state.get("Running") is not True:
            raise SmokeError("owned container is not running")
        env_items = (inspected.get("Config") or {}).get("Env") or []
        container_env = {}
        for item in env_items:
            if isinstance(item, str) and "=" in item:
                name, value = item.split("=", 1)
                container_env[name] = value
        try:
            initial_delay = int(container_env.get("CACHEMANAGEMENT__INITIALDELAYSECONDS", ""))
        except ValueError:
            raise SmokeError("container does not declare the 3600s cache-management initial delay")
        if initial_delay < 3600:
            raise SmokeError("cache-management initial delay is below 3600s; BOM traffic is not excluded")
        report["image"].update({"containerId": container_id, "imageId": image_id})
        report["checks"]["managementDelaySeconds"] = initial_delay
        report["ownership"]["singleContainer"] = True
        report["ownership"]["labels"] = {
            "runId": labels.get(RUN_LABEL),
            "ownerTokenMatches": labels.get(TOKEN_LABEL) == token,
            "project": labels.get(PROJECT_LABEL),
            "role": labels.get(ROLE_LABEL),
        }

        health_status, health_body, _ = sv.fetch_bounded(
            base + "/api/health", connect_timeout=5.0, total_timeout=30.0, max_bytes=1024 * 1024
        )
        if health_status != 200:
            raise SmokeError(f"health returned HTTP {health_status}")
        report["checks"]["health"] = {
            "status": health_status,
            "body": health_body.decode("utf-8", "replace")[:500],
        }
        status, ready = request_json(base + "/api/ready")
        sv.validate_ready(ready)
        report["checks"]["readiness"] = {"status": status, "checks": ready.get("checks")}

        now = sv.utc_now()
        pomona_folder = "v2~" + base64.urlsafe_b64encode("Pomona".encode()).decode().rstrip("=") + f"~QLD~{now.strftime('%Y%m%d')}~{now.strftime('%H%M%S')}"
        sydney_folder = sv.expected_cache_folder_name("Sydney", "NSW", now)
        report["checks"]["cacheSeed"] = {
            "pomona": seed_snapshot(cache_dir, pomona_folder, "Pomona", "QLD", now - timedelta(minutes=1)),
            "sydney": seed_snapshot(cache_dir, sydney_folder, "Sydney", "NSW", now - timedelta(minutes=1)),
        }
        report["checks"]["identity"] = {
            "pomona": sv.assert_cache_folder_owned(pomona_folder, "Pomona", "QLD"),
            "sydney": sv.assert_cache_folder_owned(sydney_folder, "Sydney", "NSW"),
            "v2Exercised": True,
            "exactLocation": True,
        }

        latest = {}
        served_images = []
        for suburb, state in (("Pomona", "QLD"), ("Sydney", "NSW")):
            _status, body = request_json(f"{base}/api/radar/{suburb}/{state}")
            validated = sv.validate_latest(
                body,
                expected_frames=7,
                suburb=suburb,
                state=state,
                max_freshness_seconds=1800,
                future_skew_seconds=120,
            )
            latest[suburb.lower()] = {
                "observationTime": validated["observation"].isoformat(),
                "cacheFolder": validated["cacheFolder"],
                "frames": len(validated["frames"]),
            }
            for frame in validated["frames"]:
                image_url = sv.frame_url(base, frame["imageUrl"])
                image_status, image_bytes, _ = sv.fetch_bounded(
                    image_url, connect_timeout=5.0, total_timeout=30.0,
                    max_bytes=sv.DEFAULT_MAX_IMAGE_BYTES,
                )
                if image_status != 200:
                    raise SmokeError(f"seeded frame returned HTTP {image_status}: {image_url}")
                decoded = sv.validate_png(image_bytes, url=image_url)
                served_images.append({"location": suburb, "frameIndex": frame["frameIndex"], **decoded})
        report["checks"]["latest"] = latest
        report["checks"]["servedImages"] = served_images

        wrong_location_url = (
            f"{base}/api/radar/Sydney/NSW/frame/0?cacheFolder="
            + urllib.parse.quote(pomona_folder, safe="")
        )
        wrong_status, _, _ = sv.fetch_bounded(
            wrong_location_url, connect_timeout=5.0, total_timeout=30.0,
            max_bytes=1024 * 1024,
        )
        if wrong_status != 404:
            raise SmokeError(f"wrong-location pinned frame returned HTTP {wrong_status}, expected 404")
        report["checks"]["wrongLocationRejected"] = {"status": wrong_status}

        start = sv.parse_utc(report["checks"]["cacheSeed"]["pomona"]["firstObservation"], "history start")
        end = sv.parse_utc(report["checks"]["cacheSeed"]["pomona"]["lastObservation"], "history end")
        history_url = (
            f"{base}/api/radar/Pomona/QLD/timeseries?startTime="
            f"{urllib.parse.quote(utc_text(start), safe='')}&endTime={urllib.parse.quote(utc_text(end), safe='')}"
        )
        _status, history = request_json(history_url)
        groups = sv.validate_history(
            history, start=start, end=end, expected_frames=7, suburb="Pomona", state="QLD"
        )
        report["checks"]["history"] = {
            "startTime": utc_text(start),
            "endTime": utc_text(end),
            "folders": len(groups),
            "frames": sum(len(group["frames"]) for group in groups),
            "pinned": True,
        }

        node_paths = ["/app/.playwright/node/linux-x64/node", "/app/.playwright/node/linux-arm64/node"]
        browser_result = None
        last_error = None
        for node_path in node_paths:
            completed = run_bounded(
                ["docker", "exec", "-i", container_id, node_path],
                timeout=BROWSER_TIMEOUT,
                input_bytes=JS_PAYLOAD.encode("utf-8"),
                max_output=32 * 1024 * 1024,
                env=docker_env,
            )
            if completed.returncode in (126, 127):
                last_error = completed.stderr.decode("utf-8", "replace")[:500]
                continue
            if completed.returncode != 0:
                raise SmokeError(f"native browser exec failed: {completed.stderr.decode('utf-8', 'replace')[:800]}")
            browser_result = (node_path, completed.stdout)
            break
        if browser_result is None:
            raise SmokeError(f"no usable node in image: {last_error or 'not found'}")
        node_path, stdout = browser_result
        try:
            payload = json.loads(stdout.decode("utf-8"))
        except ValueError as exc:
            raise SmokeError(f"native browser returned invalid JSON: {exc}") from exc
        version = payload.get("version")
        encoded_png = payload.get("pngBase64")
        if not isinstance(version, str) or not version or not isinstance(encoded_png, str):
            raise SmokeError("native browser response lacks version or PNG")
        try:
            png = base64.b64decode(encoded_png, validate=True)
        except Exception as exc:
            raise SmokeError(f"native browser PNG base64 is invalid: {type(exc).__name__}") from exc
        if len(png) > IMAGE_CAP:
            raise SmokeError(f"native browser PNG exceeds {IMAGE_CAP} bytes")
        decoded = sv.validate_png(png, url="native-smoke.png", max_bytes=IMAGE_CAP)
        png_path.write_bytes(png)
        report["checks"]["nativeBrowser"] = {
            "nodePath": node_path,
            "playwrightVersion": version,
            "png": png_path.name,
            "bytes": len(png),
            "sha256": sha256_bytes(png),
            "width": decoded["width"],
            "height": decoded["height"],
            "decoded": True,
            "validation": decoded["validation"],
        }
        report["status"] = "passed"
    except Exception as exc:  # noqa: BLE001 - report the exact bounded failure
        report["status"] = "failed"
        report["error"] = f"{type(exc).__name__}: {exc}"

    report_path.write_text(json.dumps(report, indent=2, sort_keys=False) + "\n", encoding="utf-8")
    print(json.dumps({
        "schema": report["schema"],
        "status": report["status"],
        "imageId": report.get("image", {}).get("imageId"),
        "report": str(report_path),
        "png": str(png_path) if png_path.exists() else None,
        "error": report.get("error"),
    }, indent=2))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
