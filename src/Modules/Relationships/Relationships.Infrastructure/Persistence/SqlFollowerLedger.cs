using Relationships.Application;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Relationships.Infrastructure.Persistence;

internal sealed class SqlFollowerLedger(RelationshipsDbContext db) : IFollowerLedger
{
    // Takes a batch of ledger rows (SKIP LOCKED, so two workers take different rows), sums them per
    // account and adds the sums to the counts, accounts in id order so two workers never deadlock.
    private const string FoldSql = """
        WITH taken AS (
            DELETE FROM relationships.follow_changes
            WHERE id IN (SELECT id FROM relationships.follow_changes ORDER BY id LIMIT @batch FOR UPDATE SKIP LOCKED)
            RETURNING account_id, delta),
        sums AS (SELECT account_id, sum(delta) AS delta FROM taken GROUP BY account_id)
        INSERT INTO relationships.relationship_counts (account_id, followers, following)
        SELECT account_id, delta, 0 FROM sums ORDER BY account_id
        ON CONFLICT (account_id) DO UPDATE SET followers = relationships.relationship_counts.followers + excluded.followers
        RETURNING account_id, followers
        """;

    // Counts plus the ledger rows still to fold, against the follows rows themselves.
    private const string MismatchesSql = """
        WITH facts AS (
            SELECT a.account_id,
                   (SELECT count(*) FROM relationships.follows f WHERE f.followee_id = a.account_id) AS followers,
                   (SELECT count(*) FROM relationships.follows f WHERE f.follower_id = a.account_id) AS following
            FROM (SELECT account_id FROM relationships.relationship_counts
                  UNION SELECT followee_id FROM relationships.follows
                  UNION SELECT follower_id FROM relationships.follows) a)
        SELECT count(*) FROM facts
        LEFT JOIN relationships.relationship_counts c ON c.account_id = facts.account_id
        WHERE coalesce(c.followers, 0) + coalesce((SELECT sum(delta) FROM relationships.follow_changes l WHERE l.account_id = facts.account_id), 0) <> facts.followers
           OR coalesce(c.following, 0) <> facts.following
        """;

    public async Task<IReadOnlyList<(Guid Account, long Followers)>> Fold(int batch, CancellationToken cancellationToken) =>
        await db.Read(FoldSql, r => (r.GetGuid(0), r.GetInt64(1)), cancellationToken, ("batch", batch));

    public async Task<long> Mismatches(CancellationToken cancellationToken) =>
        (await db.Read(MismatchesSql, r => r.GetInt64(0), cancellationToken)).Single();
}
