#!/usr/bin/env bash
# Recreates all README and gallery screenshots (light mode only) from the built-in demo mode. Needs macOS or Linux with Google Chrome and the .NET SDK.
# The demo mode shows invented data only; look at every image before you commit it.
set -euo pipefail
cd "$(dirname "$0")/.."
CHROME="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
PORT="${PORT:-5091}"
OUT=docs/img
dotnet build src/Sss.Web -c Debug -v q >/dev/null
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$PORT" Sss__Auth__DevIsHelpdesk=true dotnet run --project src/Sss.Web --no-build >/dev/null 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do curl -fs "http://localhost:$PORT/healthz" >/dev/null 2>&1 && break; sleep 0.5; done
shot() { # name, query, height
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=1280,"$3" --virtual-time-budget=9000 \
    --screenshot="$OUT/$1-light.png" "http://localhost:$PORT/?lang=en&theme=light$2" >/dev/null 2>&1
  echo "$OUT/$1-light.png"
}
shot overview "" 900
shot expert "&shot=details" 1100
shot overtime "&shot=charts" 1300
shot profile "&shot=profile" 1100
shot processes "&shot=processes" 1000
shot environment "&shot=env" 1900
shot insights "&shot=insights" 3300
shot map "&shot=map" 1000
shot machine "&shot=machine" 1100
