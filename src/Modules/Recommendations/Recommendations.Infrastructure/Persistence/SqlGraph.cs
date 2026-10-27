using Microsoft.EntityFrameworkCore;

using Recommendations.Application;
using Recommendations.Domain;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Recommendations.Infrastructure.Persistence;

/// <summary>
/// The graph in PostgreSQL: the first adapter of IGraphProjection and IGraphReads. Who to follow is a
/// two-hop join, bounded per hop as the domain's limits say.
/// </summary>
internal sealed class SqlGraph(RecommendationsDbContext db) : IGraphProjection, IGraphReads
{
    public Task UpsertAccount(Guid accountId, string handle, CancellationToken cancellationToken) =>
        db.Execute("""
            INSERT INTO recommendations.accounts (id, handle, active, followers, following) VALUES (@id, @handle, true, 0, 0)
            ON CONFLICT (id) DO UPDATE SET handle = excluded.handle
            """, cancellationToken, ("id", accountId), ("handle", handle));

    public Task Deactivate(Guid accountId, CancellationToken cancellationToken) =>
        db.Execute("UPDATE recommendations.accounts SET active = false WHERE id = @id", cancellationToken, ("id", accountId));

    public async Task<bool> Apply(EdgeChange change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var known = (await db.Read("SELECT count(*) FROM recommendations.accounts WHERE id IN (@a, @b)", r => r.GetInt64(0),
            cancellationToken, ("a", change.From), ("b", change.To))).Single();
        if (known < 2)
        {
            throw new InvalidOperationException($"An account of {change.Kind} {change.From} → {change.To} is not in the graph yet; delivery retries.");
        }

        var applied = change.Kind == EdgeKind.Follows ? await ApplyFollow(change, cancellationToken) : await ApplyHidden(change, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return applied;
    }

    // The edge row is created off at version 0, then locked; the change is applied only if its version
    // is higher. Degrees move only when the edge actually flips.
    private async Task<bool> ApplyFollow(EdgeChange change, CancellationToken cancellationToken)
    {
        var parameters = new (string, object)[] { ("from", change.From), ("to", change.To), ("v", change.Version), ("on", change.On), ("since", change.Since) };
        await db.Execute("""
            INSERT INTO recommendations.follow_edges (follower_id, followee_id, v, "on", since) VALUES (@from, @to, 0, false, @since)
            ON CONFLICT DO NOTHING
            """, cancellationToken, parameters);
        var (version, wasOn) = (await db.Read("""
            SELECT v, "on" FROM recommendations.follow_edges WHERE follower_id = @from AND followee_id = @to FOR UPDATE
            """, r => (r.GetInt64(0), r.GetBoolean(1)), cancellationToken, parameters)).Single();
        if (change.Version <= version)
        {
            return false;
        }

        await db.Execute("""
            UPDATE recommendations.follow_edges SET v = @v, "on" = @on, since = CASE WHEN @on THEN @since ELSE since END
            WHERE follower_id = @from AND followee_id = @to
            """, cancellationToken, parameters);
        if (wasOn != change.On)
        {
            await db.Execute("""
                UPDATE recommendations.accounts
                SET following = following + CASE WHEN id = @from THEN @d ELSE 0 END,
                    followers = followers + CASE WHEN id = @to THEN @d ELSE 0 END
                WHERE id IN (@from, @to)
                """, cancellationToken, ("from", change.From), ("to", change.To), ("d", change.On ? 1 : -1));
        }

        return true;
    }

    private async Task<bool> ApplyHidden(EdgeChange change, CancellationToken cancellationToken)
    {
        var kind = change.Kind == EdgeKind.Blocks ? "block" : "mute";
        return await db.Execute("""
            INSERT INTO recommendations.hidden_edges (owner_id, target_id, kind, v, "on", since) VALUES (@from, @to, @kind, @v, @on, @since)
            ON CONFLICT (owner_id, target_id, kind) DO UPDATE SET v = excluded.v, "on" = excluded."on", since = excluded.since
            WHERE recommendations.hidden_edges.v < excluded.v
            """, cancellationToken, ("from", change.From), ("to", change.To), ("kind", kind), ("v", change.Version), ("on", change.On), ("since", change.Since)) == 1;
    }

    // Friends of friends in two hops. The first hop takes the reader's newest follows, and only
    // intermediates that are not supernodes and follow few enough accounts; the second hop counts how
    // many intermediates lead to each candidate. The reader, accounts already followed, accounts
    // blocked either way and accounts the reader muted are excluded.
    public const string WhoToFollowSql = """
        WITH first_hop AS (
            SELECT f.followee_id AS id
            FROM recommendations.follow_edges f
            JOIN recommendations.accounts i ON i.id = f.followee_id
            WHERE f.follower_id = @me AND f."on" AND i.followers < @supernode AND i.following <= @maxFollowing
            ORDER BY f.since DESC
            LIMIT @intermediates
        )
        SELECT c.id, c.handle, count(*) AS mutual
        FROM first_hop h
        JOIN recommendations.follow_edges s ON s.follower_id = h.id AND s."on"
        JOIN recommendations.accounts c ON c.id = s.followee_id AND c.active
        WHERE c.id <> @me
          AND NOT EXISTS (SELECT 1 FROM recommendations.follow_edges x WHERE x.follower_id = @me AND x.followee_id = c.id AND x."on")
          AND NOT EXISTS (SELECT 1 FROM recommendations.hidden_edges b WHERE b.kind = 'block' AND b."on"
                          AND ((b.owner_id = @me AND b.target_id = c.id) OR (b.owner_id = c.id AND b.target_id = @me)))
          AND NOT EXISTS (SELECT 1 FROM recommendations.hidden_edges m WHERE m.kind = 'mute' AND m."on" AND m.owner_id = @me AND m.target_id = c.id)
        GROUP BY c.id, c.handle
        ORDER BY mutual DESC, c.id
        LIMIT @results
        """;

    public async Task<IReadOnlyList<Suggestion>> WhoToFollow(Guid reader, TraversalLimits limits, CancellationToken cancellationToken) =>
        await db.Read(WhoToFollowSql, r => new Suggestion(r.GetGuid(0), r.GetString(1), r.GetInt64(2)), cancellationToken,
            ("me", reader), ("supernode", limits.SupernodeFollowers), ("maxFollowing", limits.MaxIntermediateFollowing),
            ("intermediates", limits.Intermediates), ("results", limits.Results));
}
