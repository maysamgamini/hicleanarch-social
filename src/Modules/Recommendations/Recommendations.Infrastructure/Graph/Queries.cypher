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
// The follower's node is locked first (a property written and removed), so two projections of one
// pair run one after the other under Read Committed; then the edge's version is compared. A pair
// whose node is missing matches nothing: the caller fails and delivery retries. Only the follower's
// own degree moves here, on a flip: the followee's followers come from FollowerCountChanged
// (SetFollowers, s3-l12), so a celebrity's node is not written by every follow.
MATCH (a:Account {id: $from}), (b:Account {id: $to})
SET a.lock = true
MERGE (a)-[r:FOLLOWS]->(b)
  ON CREATE SET r.v = 0, r.on = false, r.since = $since
WITH a, r, r.v < $v AS newer, r.on AS was
SET r.on = CASE WHEN newer THEN $on ELSE r.on END,
    r.since = CASE WHEN newer THEN $since ELSE r.since END,
    r.v = CASE WHEN newer THEN $v ELSE r.v END,
    a.following = a.following + CASE WHEN newer AND was <> $on THEN $step ELSE 0 END
REMOVE a.lock
RETURN newer AS applied

// name: SetFollowers
// The one writer of an account's followers degree: the count Relationships announced, if it is newer
// than the one the node holds (announcements may arrive out of order).
MATCH (a:Account {id: $id})
WHERE coalesce(a.followersAt, 0) < $at
SET a.followers = $followers, a.followersAt = $at
RETURN a.id AS id

// name: ApplyBlock
MATCH (a:Account {id: $from}), (b:Account {id: $to})
SET a.lock = true
MERGE (a)-[r:BLOCKS]->(b)
  ON CREATE SET r.v = 0, r.on = false, r.since = $since
WITH a, r, r.v < $v AS newer
SET r.on = CASE WHEN newer THEN $on ELSE r.on END,
    r.since = CASE WHEN newer THEN $since ELSE r.since END,
    r.v = CASE WHEN newer THEN $v ELSE r.v END
REMOVE a.lock
RETURN newer AS applied

// name: ApplyMute
MATCH (a:Account {id: $from}), (b:Account {id: $to})
SET a.lock = true
MERGE (a)-[r:MUTES]->(b)
  ON CREATE SET r.v = 0, r.on = false, r.since = $since
WITH a, r, r.v < $v AS newer
SET r.on = CASE WHEN newer THEN $on ELSE r.on END,
    r.since = CASE WHEN newer THEN $since ELSE r.since END,
    r.v = CASE WHEN newer THEN $v ELSE r.v END
REMOVE a.lock
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
  AND NOT (me)-[:DISMISSED]->(c)
RETURN c.id AS id, c.handle AS handle, count(*) AS mutual
ORDER BY mutual DESC, id
LIMIT $results

// name: AccountsPage
// The reconciliation's walk over every account, in id order, a page at a time.
MATCH (a:Account)
WHERE a.id > $after
RETURN a.id AS id, a.followers AS followers, a.following AS following
ORDER BY id
LIMIT $limit

// name: RecountDegrees
// Degrees from the edges that are on: what the projection keeps, rebuilt from scratch. The node is
// locked first, as a projection locks it, so no edge flips between the count and the write.
MATCH (a:Account {id: $id})
SET a.lock = true
WITH a
OPTIONAL MATCH (a)<-[i:FOLLOWS {on: true}]-(:Account)
WITH a, count(i) AS followers
OPTIONAL MATCH (a)-[o:FOLLOWS {on: true}]->(:Account)
WITH a, followers, count(o) AS following
SET a.followers = followers, a.following = following
REMOVE a.lock
RETURN a.id AS id

// name: DuplicateAccounts
MATCH (a:Account)
WITH a.id AS id, count(*) AS nodes
WHERE nodes > 1
RETURN id

// name: CompactFollows
// Edges off since before the cutoff (epoch milliseconds: no datetime arithmetic), in bounded batches.
MATCH (:Account)-[r:FOLLOWS {on: false}]->(:Account)
WHERE r.since < $cutoff
WITH r LIMIT 10000
DELETE r
RETURN count(*) AS deleted

// name: CompactBlocks
MATCH (:Account)-[r:BLOCKS {on: false}]->(:Account)
WHERE r.since < $cutoff
WITH r LIMIT 10000
DELETE r
RETURN count(*) AS deleted

// name: CompactMutes
MATCH (:Account)-[r:MUTES {on: false}]->(:Account)
WHERE r.since < $cutoff
WITH r LIMIT 10000
DELETE r
RETURN count(*) AS deleted

// name: FriendsOfFriends
// People you may know: friends are mutual follows. The reader's newest friends (no supernode, capped
// following), then their friends, counted by how many friends they share with the reader.
MATCH (me:Account {id: $me})-[f1:FOLLOWS {on: true}]->(f:Account)-[:FOLLOWS {on: true}]->(me)
WHERE f.followers < $supernode AND f.following <= $maxFollowing
WITH me, f, f1 ORDER BY f1.since DESC LIMIT $intermediates
MATCH (f)-[:FOLLOWS {on: true}]->(c:Account {active: true})-[:FOLLOWS {on: true}]->(f)
WHERE c.id <> $me
  AND NOT (me)-[:FOLLOWS {on: true}]->(c)
  AND NOT (me)-[:BLOCKS {on: true}]->(c)
  AND NOT (c)-[:BLOCKS {on: true}]->(me)
  AND NOT (me)-[:MUTES {on: true}]->(c)
  AND NOT (me)-[:DISMISSED]->(c)
RETURN c.id AS id, c.handle AS handle, count(*) AS mutual
ORDER BY mutual DESC, id
LIMIT $results

// name: FollowersNotFollowedBack
// The reader's newest followers that the reader does not follow back: a bounded set however many
// followers the reader has.
MATCH (c:Account {active: true})-[r:FOLLOWS {on: true}]->(me:Account {id: $me})
WHERE NOT (me)-[:FOLLOWS {on: true}]->(c)
  AND NOT (me)-[:BLOCKS {on: true}]->(c)
  AND NOT (c)-[:BLOCKS {on: true}]->(me)
  AND NOT (me)-[:MUTES {on: true}]->(c)
  AND NOT (me)-[:DISMISSED]->(c)
RETURN c.id AS id, c.handle AS handle, r.since AS since
ORDER BY since DESC, id
LIMIT $limit

// name: HiddenNow
// The serve-time filter: of a cached list, who the reader may not be shown now.
MATCH (me:Account {id: $me})
UNWIND $ids AS id
MATCH (c:Account {id: id})
WHERE (me)-[:BLOCKS {on: true}]->(c)
   OR (c)-[:BLOCKS {on: true}]->(me)
   OR (me)-[:MUTES {on: true}]->(c)
   OR (me)-[:DISMISSED]->(c)
RETURN c.id AS id

// name: Dismiss
// The projection of a durable dismissal (recommendations.dismissals); until is epoch milliseconds.
MATCH (me:Account {id: $me}), (c:Account {id: $account})
MERGE (me)-[d:DISMISSED]->(c)
SET d.until = $until
RETURN c.id AS id

// name: CompactDismissals
// A dismissal whose 90 days are over is deleted by this job, not filtered by date in the walks.
MATCH (:Account)-[d:DISMISSED]->(:Account)
WHERE d.until < $now
WITH d LIMIT 10000
DELETE d
RETURN count(*) AS deleted
