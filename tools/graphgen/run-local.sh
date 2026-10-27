#!/usr/bin/env bash
# Runs graphgen against a throwaway postgres:17 container and prints its report: a local measurement.
#   tools/graphgen/run-local.sh [graphgen options, e.g. --accounts 100000 --seed 42]
set -euo pipefail
here=$(cd "$(dirname "$0")/../.." && pwd)
name="social-graphgen-$$"
trap 'docker rm -f "$name" "$name-neo4j" >/dev/null 2>&1 || true' EXIT
docker run -d --name "$name" -e POSTGRES_PASSWORD=graphgen -p 127.0.0.1::5432 postgres:17 >/dev/null
docker run -d --name "$name-neo4j" -e NEO4J_AUTH=none -p 127.0.0.1::7687 neo4j:5.26-community >/dev/null
port=$(docker port "$name" 5432/tcp | head -1 | sed 's/.*://')
bolt=$(docker port "$name-neo4j" 7687/tcp | head -1 | sed 's/.*://')
for _ in $(seq 1 60); do docker exec "$name" pg_isready -U postgres >/dev/null 2>&1 && break; sleep 1; done
for _ in $(seq 1 90); do docker logs "$name-neo4j" 2>&1 | grep -q "Started." && break; sleep 1; done
sleep 2
dotnet run --project "$here/tools/graphgen" -c Release -- \
  --connection "Host=127.0.0.1;Port=$port;Username=postgres;Password=graphgen;Database=postgres;Command Timeout=0" \
  --neo4j "bolt://127.0.0.1:$bolt" "$@"
