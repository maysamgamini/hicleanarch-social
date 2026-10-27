// The Neo4j adapter's setup step, run once per database: Neo4j-only, so it is not in Queries.cypher.
// On Neptune, uniqueness of Account nodes rests on one writer per node (UpsertAccount, with MERGE)
// and on the nightly reconciliation, which reports duplicates.
CREATE CONSTRAINT account_id IF NOT EXISTS FOR (a:Account) REQUIRE a.id IS UNIQUE
