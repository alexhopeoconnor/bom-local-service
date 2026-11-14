#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNNER="$ROOT/scripts/service_test_runner.py"
PURE_TESTS="$ROOT/scripts/test_service_runner.py"

# The coordinator safety hold gates every live entry point, including the
# user-facing selftest command.  Pure mocked tests are run directly with
# python3 scripts/test_service_runner.py and perform no live action.
if [[ -e "$ROOT/artifacts/execution/runner/SAFETY_HOLD" ]]; then
  echo "Runner disabled by coordinator safety hold; no live or cleanup actions permitted." >&2
  exit 3
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required" >&2
  exit 2
fi

usage() {
  cat <<'USAGE'
Usage: ./scripts/run-service-test.sh <command> [options]

Every executable run is launched as a dedicated transient user systemd unit
named bom-local-test-<32 hex>.service. There is no local or unmanaged fallback.

Commands:
  preflight                 Show read-only dedicated-unit capabilities.
  run                       Start a bounded dedicated-unit run.
  status --run-id ID        Read-only run state and completion observation.
  wait --run-id ID          Read-only wait; never stops or cleans up.
  cleanup --run-id ID       Verify the exact unit and explicitly stop it.
  logs --run-id ID          List evidence logs (use --tail N for excerpts).
  selftest                  Run pure mocked regression tests (blocked by hold).

Run examples:
  ./scripts/run-service-test.sh run --mode command -- \
      dotnet test tests/BomLocalService.Tests/BomLocalService.Tests.csproj
  ./scripts/run-service-test.sh run --mode native \
      --service-command "dotnet BomLocalService.dll" \
      --readiness-url "http://127.0.0.1:{port}/api/ready" \
      --test-command "dotnet test ..."
  ./scripts/run-service-test.sh run --mode container --image IMAGE \
      --test-command "dotnet test ..." --max-duration 30h10m \
      --outer-timeout 31h --detach

--max-duration is the workload/sampling lifetime after readiness (or from
command start); it excludes startup and teardown. --outer-timeout fixes the
whole-run systemd RuntimeMaxSec. Budget a 30h soak as 30h10m workload, which
leaves 10m of runner margin plus the default 3m startup and 20s teardown under
a 31h outer limit. The soak's own sample remains 30h.

The full option list is available with:
  python3 scripts/service_test_runner.py run --help
USAGE
}

if [[ $# -eq 0 ]]; then
  usage >&2
  exit 2
fi

cd "$ROOT"

if [[ "$1" == "selftest" ]]; then
  shift
  exec python3 "$PURE_TESTS" "$@"
fi

exec python3 "$RUNNER" "$@"
