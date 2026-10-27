// Every query the graph adapter runs, one block per query, each named by its first line.
// Only the openCypher subset that runs on both Neo4j and Amazon Neptune (spec §6.3): the lint test
// in Graph.Tests reads this file and refuses anything outside it. Ids are UUID strings.

// name: UpsertAccount
// The one writer of an Account node: MERGE on the id, so a repeat finds it.
MERGE (a:Account {id: $id})
  ON CREATE SET a.handle = $handle, a.active = true, a.followers = 0, a.following = 0
  ON MATCH SET a.handle = $handle
RETURN a.id AS id

// name: Deactivate
MATCH (a:Account {id: $id})
SET a.active = false
RETURN a.id AS id

// name: ApplyFollow
// The two nodes are locked first (a property written and removed), so two projections of one pair
// run one after the other under Read Committed; then the edge's version is compared. A pair whose
// node is missing matches nothing: the caller fails and delivery retries. Degrees move only on a flip.
MATCH (a:Account {id: $from}), (b:Account {id: $to})
SET a.lock = true, b.lock = true
MERGE (a)-[r:FOLLOWS]->(b)
  ON CREATE SET r.v = 0, r.on = false, r.since = $since
WITH a, b, r, r.v < $v AS newer, r.on AS was
SET r.on = CASE WHEN newer THEN $on ELSE r.on END,
    r.since = CASE WHEN newer AND $on THEN $since ELSE r.since END,
    r.v = CASE WHEN newer THEN $v ELSE r.v END,
    a.following = a.following + CASE WHEN newer AND was <> $on THEN $step ELSE 0 END,
    b.followers = b.followers + CASE WHEN newer AND was <> $on THEN $step ELSE 0 END
REMOVE a.lock, b.lock
RETURN newer AS applied

// name: ApplyBlock
MATCH (a:Account {id: $from}), (b:Account {id: $to})
SET a.lock = true, b.lock = true
MERGE (a)-[r:BLOCKS]->(b)
  ON CREATE SET r.v = 0, r.on = false, r.since = $since
WITH a, b, r, r.v < $v AS newer
SET r.on = CASE WHEN newer THEN $on ELSE r.on END,
    r.since = CASE WHEN newer THEN $since ELSE r.since END,
    r.v = CASE WHEN newer THEN $v ELSE r.v END
REMOVE a.lock, b.lock
RETURN newer AS applied

// name: ApplyMute
MATCH (a:Account {id: $from}), (b:Account {id: $to})
SET a.lock = true, b.lock = true
MERGE (a)-[r:MUTES]->(b)
  ON CREATE SET r.v = 0, r.on = false, r.since = $since
WITH a, b, r, r.v < $v AS newer
SET r.on = CASE WHEN newer THEN $on ELSE r.on END,
    r.since = CASE WHEN newer THEN $since ELSE r.since END,
    r.v = CASE WHEN newer THEN $v ELSE r.v END
REMOVE a.lock, b.lock
RETURN newer AS applied

// name: WhoToFollow
// Friends of friends, bounded per hop: the reader's newest follows, never through a supernode or an
// account following more than the cap; then every account those follow, counted.
MATCH (me:Account {id: $me})-[f1:FOLLOWS {on: true}]->(f:Account)
WHERE f.followers < $supernode AND f.following <= $maxFollowing
WITH me, f, f1 ORDER BY f1.since DESC LIMIT $intermediates
MATCH (f)-[:FOLLOWS {on: true}]->(c:Account {active: true})
WHERE c.id <> $me
  AND NOT (me)-[:FOLLOWS {on: true}]->(c)
  AND NOT (me)-[:BLOCKS {on: true}]->(c)
  AND NOT (c)-[:BLOCKS {on: true}]->(me)
  AND NOT (me)-[:MUTES {on: true}]->(c)
RETURN c.id AS id, c.handle AS handle, count(*) AS mutual
ORDER BY mutual DESC, id
LIMIT $results
