#!/usr/bin/env bash
# The timeline store's memory, measured (s3-l08): fills sample timelines as the program writes them
# (800 posts and no sentinel, scores in milliseconds) in a throwaway Valkey 8.1 set up as the timeline
# store, and prints MEMORY USAGE per entry for each member form and encoding. A local measurement.
#   scripts/memory-probe.sh [timelines per form, default 200]
set -euo pipefail
count=${1:-200}
name="social-memprobe-$$"
trap 'docker rm -fv "$name" >/dev/null 2>&1 || true' EXIT
docker run -d --name "$name" valkey/valkey:8.1 valkey-server --zset-max-listpack-entries 1024 --maxmemory-policy volatile-ttl >/dev/null
for _ in $(seq 1 30); do docker exec "$name" valkey-cli ping >/dev/null 2>&1 && break; sleep 1; done

# KEYS[1] prefix, ARGV: timelines, entries, form (bytes|hex). Members look like UUIDv7: 6 bytes of
# milliseconds then 10 random bytes.
fill='
local start = 1767225600000
for t = 1, tonumber(ARGV[1]) do
  local key = KEYS[1] .. t
  for i = 1, tonumber(ARGV[2]) do
    local ms = start + t * 100000 + i * 37
    local head = ""
    local v = ms
    for _ = 1, 6 do head = string.char(v % 256) .. head; v = math.floor(v / 256) end
    local tail = ""
    for _ = 1, 10 do tail = tail .. string.char(math.random(0, 255)) end
    local member = head .. tail
    if ARGV[3] == "hex" then
      member = string.gsub(member, ".", function(c) return string.format("%02x", string.byte(c)) end)
    end
    redis.call("ZADD", key, ms, member)
  end
  redis.call("PEXPIRE", key, 259200000)
end
return 1'

# KEYS[1] prefix, ARGV[1] timelines, ARGV[2] entries: total MEMORY USAGE / (timelines * entries), and the encoding.
measure='
local total = 0
for t = 1, tonumber(ARGV[1]) do
  total = total + redis.call("MEMORY", "USAGE", KEYS[1] .. t, "SAMPLES", 0)
end
return {tostring(total / (tonumber(ARGV[1]) * tonumber(ARGV[2]))), redis.call("OBJECT", "ENCODING", KEYS[1] .. "1")}'

probe() { # prefix form entries listpack-limit
  docker exec "$name" valkey-cli CONFIG SET zset-max-listpack-entries "$4" >/dev/null
  docker exec "$name" valkey-cli EVAL "$fill" 1 "$1" "$count" "$3" "$2" >/dev/null
  local out; out=$(docker exec "$name" valkey-cli EVAL "$measure" 1 "$1" "$count" "$3" | tr '\n' ' ')
  printf '%-34s %s\n' "$2 members, $3 entries, limit $4:" "$out(bytes per entry, encoding)"
}

echo "Valkey $(docker exec "$name" valkey-cli INFO server | grep -m1 valkey_version | cut -d: -f2 | tr -d '\r'); $count timelines per row"
probe hex: hex 800 128      # the old form, the old limit (Valkey's default 128): a skiplist
probe hexl: hex 800 1024    # the old form, compact
probe bin: bytes 800 1024   # the new form, compact
