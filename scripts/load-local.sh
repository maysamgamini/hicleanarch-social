#!/usr/bin/env bash
# The local baseline (s3-l01): PostgreSQL 17, Valkey 8.1 and Neo4j 5.26 in throwaway containers, one
# web task and worker tasks as local processes of the same build, and k6 (grafana/k6, a separate
# tool) driving the API. A worker is added at a third of the run and the first one stopped at two
# thirds: the run must have no errors. Prints k6's summary: a local measurement.
#   scripts/load-local.sh [duration, default 60s]
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
duration=${1:-60s}
run=$(mktemp -d)
name="social-load-$$"
pids=()
cleanup() {
  # LOAD_LOGS=<folder> keeps the hosts' logs of this run there.
  if [[ -n "${LOAD_LOGS:-}" ]]; then mkdir -p "$LOAD_LOGS"; cp "$run"/*.log "$LOAD_LOGS"/ 2>/dev/null || true; fi
  for pid in ${pids[@]+"${pids[@]}"}; do kill "$pid" 2>/dev/null || true; done
  docker rm -f "$name-pg" "$name-valkey" "$name-neo4j" >/dev/null 2>&1 || true
  rm -rf "$run"
}
trap cleanup EXIT

for p in 5181 5182 5183; do
  if lsof -nP -iTCP:$p -sTCP:LISTEN >/dev/null 2>&1; then echo "port $p is in use: stop what holds it first"; exit 1; fi
done
docker run -d --name "$name-pg" -e POSTGRES_PASSWORD=load -e POSTGRES_DB=social -p 127.0.0.1::5432 postgres:17 >/dev/null
docker run -d --name "$name-valkey" -p 127.0.0.1::6379 valkey/valkey:8.1 >/dev/null
docker run -d --name "$name-neo4j" -e NEO4J_AUTH=none -p 127.0.0.1::7687 neo4j:5.26-community >/dev/null
port() { docker port "$1" "$2/tcp" | head -1 | sed 's/.*://'; }
pg=$(port "$name-pg" 5432); valkey=$(port "$name-valkey" 6379); bolt=$(port "$name-neo4j" 7687)
for _ in $(seq 1 60); do docker exec "$name-pg" pg_isready -U postgres >/dev/null 2>&1 && break; sleep 1; done
for _ in $(seq 1 90); do docker logs "$name-neo4j" 2>&1 | grep -q "Started." && break; sleep 1; done
sleep 2

# Development settings (the local token issuer), with this run's stores. Throwaway values only.
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Owner="Host=127.0.0.1;Port=$pg;Database=social;Username=postgres;Password=load"
for module in Accounts Relationships Posts Timeline Recommendations Notifications; do
  lower=$(echo "$module" | tr '[:upper:]' '[:lower:]')
  export "ConnectionStrings__$module=Host=127.0.0.1;Port=$pg;Database=social;Username=${lower}_app;Password=${lower}_load;Maximum Pool Size=30"
done
export ConnectionStrings__Valkey="127.0.0.1:$valkey,abortConnect=false"
export Graph__Uri="bolt://127.0.0.1:$bolt"
export Logging__LogLevel__Default=Warning

dotnet publish "$here/src/Host/Social.Host.csproj" -c Release -o "$run/app" -v:q >/dev/null
cd "$run/app" # the content root: the published appsettings.json files are read from here
host() { dotnet "$run/app/Social.Host.dll" "$@"; }
host migrate

# 50 people, each with a local token.
python3 - "$run" <<'PY'
import json, subprocess, sys, uuid
run = sys.argv[1]
people = []
for i in range(50):
    pid = str(uuid.uuid4())
    token = subprocess.check_output(["dotnet", f"{run}/app/Social.Host.dll", "token", pid], text=True).strip().splitlines()[-1]
    people.append({"id": pid, "handle": f"load_{i}_{pid[:6]}", "token": token})
json.dump(people, open(f"{run}/people.json", "w"))
PY

# Started directly, not through the host function: $! must be the dotnet process itself, or the
# cleanup kills a subshell and leaves the host running.
Host__Role=Web ASPNETCORE_URLS=http://0.0.0.0:5181 dotnet "$run/app/Social.Host.dll" >"$run/web.log" 2>&1 & pids+=($!)
Host__Role=Worker ASPNETCORE_URLS=http://127.0.0.1:5182 dotnet "$run/app/Social.Host.dll" >"$run/worker-a.log" 2>&1 & worker_a=$!; pids+=($worker_a)
ready=0
for _ in $(seq 1 60); do curl -sf http://127.0.0.1:5181/health/ready >/dev/null && { ready=1; break; }; sleep 1; done
if [[ $ready -ne 1 ]]; then echo "the web task never became ready; its log:"; tail -20 "$run/web.log"; exit 1; fi

seconds=${duration%s}
( sleep $((seconds / 3)); Host__Role=Worker ASPNETCORE_URLS=http://127.0.0.1:5183 exec dotnet "$run/app/Social.Host.dll" >"$run/worker-b.log" 2>&1 ) & pids+=($!) # exec: the subshell becomes the host
( sleep $((2 * seconds / 3)); kill -TERM "$worker_a" ) &

# The script on standard input and the people in a variable: no folder to share with Docker's VM.
docker run --rm -i -e BASE=http://host.docker.internal:5181 -e DURATION="$duration" -e PEOPLE="$(cat "$run/people.json")" \
  grafana/k6:1.3.0 run --quiet --summary-trend-stats "p(50),p(99),max" - <"$here/tests/load/baseline.js"
sleep 5 # the remaining worker hands on what the run committed last
echo "undelivered events left: $(docker exec "$name-pg" psql -U postgres -d social -tAc \
  "SELECT (SELECT count(*) FROM relationships.outbox WHERE delivered_at IS NULL) + (SELECT count(*) FROM posts.outbox WHERE delivered_at IS NULL)")"
