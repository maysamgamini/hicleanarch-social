using Relationships.Application;
using Relationships.Domain;

namespace Relationships.Infrastructure.Persistence;

/// <summary>
/// The rules of relationships, as the statements that keep them. Everything here runs inside the
/// transaction the use case opened, after <see cref="LockPair"/> has taken the pair's lock.
/// </summary>
internal sealed class SqlRelationshipStore(RelationshipsDbContext db) : IRelationshipStore
{
    // The first statement of every change to a pair. The upsert takes the row's lock until commit,
    // so a concurrent block and follow of the same two accounts run one after the other (no write
    // skew), and it numbers the change. The order of the two ids is PostgreSQL's own uuid order.
    private const string BumpPairVersion = """
        INSERT INTO relationships.pair_versions (low_id, high_id, version) VALUES (LEAST(@a, @b), GREATEST(@a, @b), 1)
        ON CONFLICT (low_id, high_id) DO UPDATE SET version = relationships.pair_versions.version + 1
        RETURNING version
        """;

    private const string EdgesOfPair = """
        SELECT 'Follow', follower_id, followee_id FROM relationships.follows
        WHERE (follower_id = @a AND followee_id = @b) OR (follower_id = @b AND followee_id = @a)
        UNION ALL
        SELECT 'Block', blocker_id, blocked_id FROM relationships.blocks
        WHERE (blocker_id = @a AND blocked_id = @b) OR (blocker_id = @b AND blocked_id = @a)
        UNION ALL
        SELECT 'Mute', muter_id, muted_id FROM relationships.mutes
        WHERE (muter_id = @a AND muted_id = @b) OR (muter_id = @b AND muted_id = @a)
        """;

    // Both accounts' counts rows exist, then are locked in account-id order: any two transactions
    // that touch the same counts rows take their locks in the same order, so they cannot deadlock.
    private const string LockCounts = """
        INSERT INTO relationships.relationship_counts (account_id, followers, following)
        SELECT id, 0, 0 FROM unnest(ARRAY[@a, @b]::uuid[]) AS id ORDER BY id
        ON CONFLICT (account_id) DO NOTHING;
        SELECT account_id FROM relationships.relationship_counts WHERE account_id IN (@a, @b) ORDER BY account_id FOR UPDATE;
        """;

    // The follow limit is a guarded counter: it affects one row, or the follow is refused.
    private const string CountFollowGuarded = """
        UPDATE relationships.relationship_counts SET following = following + 1
        WHERE account_id = @follower AND following < @max
        """;

    public async Task<Pair> LockPair(Guid first, Guid second, CancellationToken cancellationToken)
    {
        var version = (await db.Read(BumpPairVersion, r => r.GetInt64(0), cancellationToken, ("a", first), ("b", second))).Single();
        var edges = await db.Read(EdgesOfPair, r => new Edge(Enum.Parse<EdgeKind>(r.GetString(0)), r.GetGuid(1), r.GetGuid(2)),
            cancellationToken, ("a", first), ("b", second));
        return new Pair(version, edges);
    }

    public async Task<bool> AddFollow(Guid follower, Guid followee, CancellationToken cancellationToken)
    {
        await db.Execute(LockCounts, cancellationToken, ("a", follower), ("b", followee));
        if (await db.Execute(CountFollowGuarded, cancellationToken, ("follower", follower), ("max", Pair.MaxFollowing)) != 1)
        {
            return false;
        }

        await db.Execute("UPDATE relationships.relationship_counts SET followers = followers + 1 WHERE account_id = @followee",
            cancellationToken, ("followee", followee));
        await db.Execute("INSERT INTO relationships.follows (follower_id, followee_id) VALUES (@follower, @followee)",
            cancellationToken, ("follower", follower), ("followee", followee));
        return true;
    }

    public Task Add(EdgeKind kind, Guid from, Guid to, CancellationToken cancellationToken) => kind switch
    {
        EdgeKind.Block => db.Execute("INSERT INTO relationships.blocks (blocker_id, blocked_id) VALUES (@from, @to)", cancellationToken, ("from", from), ("to", to)),
        EdgeKind.Mute => db.Execute("INSERT INTO relationships.mutes (muter_id, muted_id) VALUES (@from, @to)", cancellationToken, ("from", from), ("to", to)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "A follow is added with its counts: AddFollow."),
    };

    public Task Remove(EdgeKind kind, Guid from, Guid to, CancellationToken cancellationToken) => kind switch
    {
        EdgeKind.Block => db.Execute("DELETE FROM relationships.blocks WHERE blocker_id = @from AND blocked_id = @to", cancellationToken, ("from", from), ("to", to)),
        EdgeKind.Mute => db.Execute("DELETE FROM relationships.mutes WHERE muter_id = @from AND muted_id = @to", cancellationToken, ("from", from), ("to", to)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "A follow is removed with its counts: RemoveFollow."),
    };

    public async Task RemoveFollow(Guid follower, Guid followee, CancellationToken cancellationToken)
    {
        await db.Execute(LockCounts, cancellationToken, ("a", follower), ("b", followee));
        await db.Execute("""
            UPDATE relationships.relationship_counts SET following = following - 1 WHERE account_id = @follower;
            UPDATE relationships.relationship_counts SET followers = followers - 1 WHERE account_id = @followee;
            DELETE FROM relationships.follows WHERE follower_id = @follower AND followee_id = @followee;
            """, cancellationToken, ("follower", follower), ("followee", followee));
    }
}
