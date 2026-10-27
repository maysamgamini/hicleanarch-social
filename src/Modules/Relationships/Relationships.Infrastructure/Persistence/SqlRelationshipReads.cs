using Relationships.Application;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Relationships.Infrastructure.Persistence;

/// <summary>Keyset pages: the next page starts after the last id of this one, so a page costs the same at any depth.</summary>
internal sealed class SqlRelationshipReads(RelationshipsDbContext db) : IRelationshipReads
{
    public Task<RelationshipPage> Followers(Guid account, Guid? after, int limit, CancellationToken cancellationToken) =>
        Page("follower_id", "followee_id", account, after, limit, cancellationToken);

    public Task<RelationshipPage> Following(Guid account, Guid? after, int limit, CancellationToken cancellationToken) =>
        Page("followee_id", "follower_id", account, after, limit, cancellationToken);

    public async Task<IReadOnlyList<Guid>> AllFollowing(Guid account, CancellationToken cancellationToken) =>
        await db.Read("SELECT followee_id FROM relationships.follows WHERE follower_id = @account", r => r.GetGuid(0),
            cancellationToken, ("account", account));

    public async Task<(long Followers, long Following)> Counts(Guid account, CancellationToken cancellationToken)
    {
        var rows = await db.Read("SELECT followers, following FROM relationships.relationship_counts WHERE account_id = @account",
            r => (r.GetInt64(0), r.GetInt64(1)), cancellationToken, ("account", account));
        return rows.Count == 0 ? (0, 0) : rows[0];
    }

    public async Task<IReadOnlyList<(Guid Account, long Followers, long Following)>> CountsOf(IReadOnlyList<Guid> accounts, CancellationToken cancellationToken) =>
        await db.Read("SELECT account_id, followers, following FROM relationships.relationship_counts WHERE account_id = ANY(@ids)",
            r => (r.GetGuid(0), r.GetInt64(1), r.GetInt64(2)), cancellationToken, ("ids", accounts.ToArray()));

    // Every pair the account was ever part of, and for each the six relationships, on or off.
    public async Task<IReadOnlyList<(string Kind, Guid From, Guid To, bool On, long Version)>> PairsOf(Guid account, CancellationToken cancellationToken) =>
        await db.Read("""
            WITH pairs AS (
                SELECT low_id, high_id, version FROM relationships.pair_versions WHERE low_id = @a OR high_id = @a),
            directions AS (
                SELECT low_id AS from_id, high_id AS to_id, version FROM pairs
                UNION ALL SELECT high_id, low_id, version FROM pairs),
            kinds(kind) AS (VALUES ('Follow'), ('Block'), ('Mute'))
            SELECT k.kind, d.from_id, d.to_id,
                CASE k.kind
                    WHEN 'Follow' THEN EXISTS (SELECT 1 FROM relationships.follows f WHERE f.follower_id = d.from_id AND f.followee_id = d.to_id)
                    WHEN 'Block' THEN EXISTS (SELECT 1 FROM relationships.blocks b WHERE b.blocker_id = d.from_id AND b.blocked_id = d.to_id)
                    ELSE EXISTS (SELECT 1 FROM relationships.mutes m WHERE m.muter_id = d.from_id AND m.muted_id = d.to_id)
                END,
                d.version
            FROM directions d CROSS JOIN kinds k
            """, r => (r.GetString(0), r.GetGuid(1), r.GetGuid(2), r.GetBoolean(3), r.GetInt64(4)), cancellationToken, ("a", account));

    public async Task<IReadOnlyList<Guid>> HiddenFrom(Guid viewer, CancellationToken cancellationToken) =>
        await db.Read("""
            SELECT blocked_id FROM relationships.blocks WHERE blocker_id = @viewer
            UNION
            SELECT blocker_id FROM relationships.blocks WHERE blocked_id = @viewer
            UNION
            SELECT muted_id FROM relationships.mutes WHERE muter_id = @viewer
            """, r => r.GetGuid(0), cancellationToken, ("viewer", viewer));

    private async Task<RelationshipPage> Page(string listed, string owner, Guid account, Guid? after, int limit, CancellationToken cancellationToken)
    {
        // One more row than asked for says whether another page exists.
        var sql = $"""
            SELECT {listed}, created_at FROM relationships.follows
            WHERE {owner} = @account {(after is null ? "" : $"AND {listed} > @after")}
            ORDER BY {listed} LIMIT @take
            """;
        var parameters = new List<(string, object)> { ("account", account), ("take", limit + 1) };
        if (after is { } cursor)
        {
            parameters.Add(("after", cursor));
        }

        var rows = await db.Read(sql, r => new ListedAccount(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1)), cancellationToken, [.. parameters]);
        return rows.Count > limit
            ? new RelationshipPage(rows[..limit], rows[limit - 1].AccountId)
            : new RelationshipPage(rows, null);
    }
}
