#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
PRINTS="$REPO_ROOT/DeChimexPrints"
DOTNET_BIN="${DOTNET_BIN:-/var/tmp/dotnet/dotnet}"
HOST_DLL="$PRINTS/src/DeChimex.Host/bin/Debug/net9.0/DeChimex.Host.dll"
PYTHON_BIN="${PYTHON_BIN:-python3}"
if [[ -d /var/tmp/ms-playwright ]]; then export PLAYWRIGHT_BROWSERS_PATH="${PLAYWRIGHT_BROWSERS_PATH:-/var/tmp/ms-playwright}"; fi
if [[ -d /var/tmp/pw-libs/root/usr/lib/x86_64-linux-gnu ]]; then
  export LD_LIBRARY_PATH="/var/tmp/pw-libs/root/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi
APP_PORT="${APP_PORT:-5127}"
STUB_PORT="${STUB_PORT:-9998}"
E2E_TAG="${E2E_TAG:-$(date -u +%Y%m%d%H%M%S)}"
WIP="${E2E_WIP_DIR:-/tmp/marketing-builder-e2e-$E2E_TAG}"
SHOTS="${E2E_SHOTS_DIR:-$WIP/screenshots}"
CAPTURE="$WIP/provider-capture.jsonl"
STUB_KEY="marketing-builder-e2e-dummy"
mkdir -p "$WIP" "$SHOTS"
: > "$CAPTURE"

if [[ ! -x "$DOTNET_BIN" ]]; then echo "Missing dotnet executable: $DOTNET_BIN" >&2; exit 2; fi
if [[ ! -f "$HOST_DLL" ]]; then echo "Missing host assembly: $HOST_DLL (build the solution first)" >&2; exit 2; fi

STUB_PORT="$STUB_PORT" STUB_API_KEY="$STUB_KEY" STUB_CAPTURE_PATH="$CAPTURE" \
  "$PYTHON_BIN" "$SCRIPT_DIR/brevo_stub.py" >"$WIP/provider-stub.log" 2>&1 &
STUB_PID=$!

(
  cd "$PRINTS/src/DeChimex.Host"
  env \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="http://127.0.0.1:$APP_PORT" \
    Database__UseInMemory=true \
    ConnectionStrings__DefaultConnection="Server=localhost;Database=DeChimexE2ETest;User Id=test;Password=e2e-only;TrustServerCertificate=True;Encrypt=False" \
    BREVO__ENABLED=true \
    BREVO__APIKEY="$STUB_KEY" \
    BREVO__APIBASEURL="http://127.0.0.1:$STUB_PORT" \
    Brevo__Enabled=true \
    Brevo__ApiKey="$STUB_KEY" \
    Brevo__ApiBaseUrl="http://127.0.0.1:$STUB_PORT" \
    App__FrontendBaseUrl="http://127.0.0.1:$APP_PORT" \
    Marketing__AllowBroadcasts=true \
    Marketing__PollIntervalSeconds=5 \
    Marketing__BatchSize=50 \
    "$DOTNET_BIN" "$HOST_DLL"
) >"$WIP/application.log" 2>&1 &
APP_PID=$!

cleanup() {
  set +e
  kill "$APP_PID" "$STUB_PID" 2>/dev/null
  wait "$APP_PID" "$STUB_PID" 2>/dev/null
}
trap cleanup EXIT INT TERM

wait_http() {
  local url="$1"
  local label="$2"
  "$PYTHON_BIN" - "$url" "$label" <<'PY'
import sys, time, urllib.error, urllib.request
url, label = sys.argv[1], sys.argv[2]
deadline = time.monotonic() + 90
last = "not ready"
while time.monotonic() < deadline:
    try:
        with urllib.request.urlopen(url, timeout=2) as response:
            if response.status < 500:
                print(f"{label} ready (HTTP {response.status})", flush=True)
                raise SystemExit(0)
    except urllib.error.HTTPError as error:
        if error.code < 500:
            print(f"{label} ready (HTTP {error.code})", flush=True)
            raise SystemExit(0)
        last = f"HTTP {error.code}"
    except Exception as error:
        last = type(error).__name__
    time.sleep(0.5)
print(f"{label} did not become ready: {last}", file=sys.stderr)
raise SystemExit(1)
PY
}

if ! wait_http "http://127.0.0.1:$STUB_PORT/health" "Email-provider stub"; then
  tail -80 "$WIP/provider-stub.log" >&2
  exit 1
fi
if ! wait_http "http://127.0.0.1:$APP_PORT/Account/Login" "Marketing app"; then
  tail -120 "$WIP/application.log" >&2
  exit 1
fi

E2E_BASE_URL="http://127.0.0.1:$APP_PORT" \
E2E_STUB_CONTROL="http://127.0.0.1:$STUB_PORT/control/captured" \
E2E_SHOTS_DIR="$SHOTS" \
  "$PYTHON_BIN" "$SCRIPT_DIR/marketing_builder_e2e.py" "$E2E_TAG"

# Assert that capture artifacts contain only redacted key metadata and no request headers.
"$PYTHON_BIN" - "$CAPTURE" <<'PY'
import json, sys
from pathlib import Path
p = Path(sys.argv[1])
for line in p.read_text(encoding="utf-8").splitlines():
    item = json.loads(line)
    assert "headers" not in item and "api-key" not in item and "ApiKey" not in item
    assert item.get("providerKeyPresent") is True
    assert item.get("providerKeyMatchesExpected") is True
print(f"sanitized provider capture verified ({sum(1 for _ in p.open(encoding='utf-8'))} messages)", flush=True)
PY
