# Round 2's numbers

Every number on this page says what it is, and `scripts/check-numbers.sh` refuses one that does not:

- a **local measurement**: run on a developer's machine, in containers, with its setup beside it. It
  tells what the code does on that machine, not what AWS will do;
- a **design estimate**: arithmetic from stated assumptions, shown in full, for what cannot be run
  locally;
- a **learner exercise**: a run in the learner's own AWS account. No AWS run is part of this course's
  reference; nobody here has run one.

## The machine the local measurements ran on

- Machine (local measurement setup): a MacBook with Apple silicon, 12 logical CPUs and 28 GiB given to the colima VM, Docker through colima, shared with other test suites while it ran; the numbers are noisy, and the order of magnitude is what they show.
- Software (local measurement setup): PostgreSQL 17, Valkey 8.1 and Neo4j 5.26 Community in containers; the program as local processes of a Release build, one web task and worker tasks; k6 1.3 in a container.

## Load runs (scripts/load-local.sh)

| Run | Setup | Result | Kind |
|---|---|---|---|
| Baseline (s3-l01) | 60 s, 20 VUs, 50 people each following 10, 30% posts and 70% feed reads; one web task, worker A then worker B | 0 of 92,153 requests failed; feed p50 9.7 ms, p99 25.8 ms; post p50 17.4 ms, p99 38.1 ms; about 1,430 requests a second | local measurement |

## Timeline memory (scripts/memory-probe.sh, s3-l08)

| Form | Setup | Bytes per entry (MEMORY USAGE ÷ entries) | Kind |
|---|---|---|---|
| 32 hex digits, skiplist (Valkey's default limit of 128) | 200 timelines of 800 entries, Valkey 8.1.10 | 100.1 | local measurement |
| 32 hex digits, listpack (limit 1,024) | the same | 51.3 | local measurement |
| 16 bytes, listpack (limit 1,024) | the same | 30.8 | local measurement |

## The graph (scripts/graph-benchmark.sh, tools/graphgen)

| Run | Setup | Result | Kind |
|---|---|---|---|
| SQL shapes (s2-l13) | 100,000 accounts, 1,678,657 follows, exponent 2.1, seed 42, 100 readers | p50/p99 ms: plain two-hop 1.4/4.2, capped two-hop 1.3/4.4, bounded LATERAL 1.1/2.3, three-hop CTE 22.6/49.7 | local measurement |
| Neo4j adapter (s2-l14) | the same graph and readers, no warm-up | p50 9.7 ms, p99 162.8 ms | local measurement |
| Larger graph (s2-l15) | 1,000,000 accounts, 18,647,912 follows, max followers 428,747, 128 supernodes; Neo4j with 2 GB heap and 2 GB page cache | SQL capped 11.4/13.9 ms, SQL LATERAL 1.0/2.3 ms, Neo4j adapter 4.0/22.2 ms (p50/p99) | local measurement |

## Round 2 on AWS: design estimates

| Quantity | Arithmetic | Value | Kind |
|---|---|---|---|
| Timelines kept | readers of the last 3 days: 1.1 × 300M daily users | ≈ 330M timelines | design estimate |
| Timeline memory, 16-byte members, from the measurement | 330M × (800 × 30.8 B + 80 B) = 330M × 24,720 B ≈ 8.16 TB; × 1.25 for fragmentation and replicas' slack | ≈ 10.2 TB | design estimate |
| The same with 32-hex members, listpack | 330M × (800 × 51.3 B + 80 B) ≈ 13.6 TB; × 1.25 | ≈ 17.0 TB | design estimate |
| The same with 32-hex members, skiplist | 330M × (800 × 100.1 B + 80 B) ≈ 26.5 TB; × 1.25 | ≈ 33.1 TB | design estimate |
| Spec's estimate before the measurement | per entry ≈ 28 B (member 1 + 16 + 1, score 9 + 1) → 9.3 TB; the measurement adds about 10% (the listpack's per-entry header bytes) | ≈ 9.3 TB → ≈ 10.2 TB | design estimate |
| Pull reads of one celebrity in a read storm | 8M refreshes a minute on one author ÷ 60 s ≈ 133,000 reads a second; with one query per task per second (s3-l06) and 200 web tasks | ≈ 200 queries a second on that author instead of ≈ 133,000 | design estimate |
| Follower counter of a celebrity | 10,000 follows a second as ledger rows (s3-l04), folded once a second per worker | one UPDATE a second on the hot row instead of 10,000 | design estimate |

## Runs on AWS

| Run | What to do | Kind |
|---|---|---|
| The Round 2 load run in the sandbox | deploy infra/env, point k6's BASE at the load balancer, run tests/load/round2.js, then destroy; read CloudWatch for fanout lag and feed p99 | learner exercise |
