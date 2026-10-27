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
