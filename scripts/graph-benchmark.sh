#!/usr/bin/env bash
# The synthetic-graph benchmark (s2-l13, s2-l14, s3-l12): PostgreSQL 17 and Neo4j 5.26 Community in
# throwaway containers, then tools/graphgen, which builds a power-law follow graph, times each SQL
# shape, loads the graph into Neo4j and times the adapter's WhoToFollow under several caps beside the
# degree distribution. Every number it prints is a local measurement, printed with its setup.
#   scripts/graph-benchmark.sh [graphgen options, default --accounts 100000 --readers 100]
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
name="social-graphbench-$$"
cleanup() { docker rm -fv "$name-pg" "$name-neo4j" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker run -d --name "$name-pg" -e POSTGRES_PASSWORD=bench -e POSTGRES_DB=bench -p 127.0.0.1::5432 postgres:17 >/dev/null
docker run -d --name "$name-neo4j" -e NEO4J_AUTH=none -e NEO4J_server_memory_heap_max__size=2G -e NEO4J_server_memory_pagecache_size=2G \
  -p 127.0.0.1::7687 neo4j:5.26-community >/dev/null
port() { docker port "$1" "$2/tcp" | head -1 | sed 's/.*://'; }
pg=$(port "$name-pg" 5432); bolt=$(port "$name-neo4j" 7687)
for _ in $(seq 1 60); do docker exec "$name-pg" pg_isready -U postgres >/dev/null 2>&1 && break; sleep 1; done
for _ in $(seq 1 90); do docker logs "$name-neo4j" 2>&1 | grep -q "Started." && break; sleep 1; done

args=("$@")
[[ ${#args[@]} -gt 0 ]] || args=(--accounts 100000 --readers 100)
dotnet run --project "$here/tools/graphgen" -c Release -- \
  --connection "Host=127.0.0.1;Port=$pg;Database=bench;Username=postgres;Password=bench" "${args[@]}" --neo4j "bolt://127.0.0.1:$bolt"
