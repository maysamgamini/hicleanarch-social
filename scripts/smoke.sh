#!/usr/bin/env bash
# Smoke test against a running API: health, a refusal without a token, a profile registered and read.
# Usage: TOKEN=<access token> smoke.sh <base url>
set -euo pipefail
base="${1:?usage: TOKEN=<token> smoke.sh <base url>}"
token="${TOKEN:?set TOKEN to an access token}"

for _ in $(seq 1 30); do
  curl -sf "$base/health/live" >/dev/null && break
  sleep 5
done
curl -sf "$base/health/ready" | grep -q '"status":"ready"'

test "$(curl -s -o /dev/null -w '%{http_code}' -X PUT "$base/v1/me" -H 'Content-Type: application/json' -d '{}')" = "401"

handle="smoke_$(od -An -tx1 -N4 /dev/urandom | tr -d ' \n')"
curl -sf -X PUT "$base/v1/me" -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
  -d "{\"handle\":\"$handle\",\"display_name\":\"Smoke test\"}" >/dev/null
curl -sf "$base/v1/accounts/$handle" | grep -q "\"handle\":\"$handle\""
echo "smoke test passed: $handle"
