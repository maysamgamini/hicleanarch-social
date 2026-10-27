using Social.BuildingBlocks.Infrastructure.Persistence;

using Timeline.Application;

namespace Timeline.Infrastructure.Persistence;

/// <summary>Each statement is its own transaction: progress is a record of what happened, not part of it.</summary>
internal sealed class SqlFanoutProgress(TimelineDbContext db) : IFanoutProgress
{
    private const string Columns = "post_id, author_id, stored_at, followers_reached, completed_at";

    public async Task<Delivery> Start(Guid postId, Guid authorId, DateTimeOffset storedAt, CancellationToken cancellationToken)
    {
        await db.Execute("""
            INSERT INTO timeline.fanout_progress (post_id, author_id, stored_at, followers_reached) VALUES (@post, @author, @stored, 0)
            ON CONFLICT (post_id) DO NOTHING
            """, cancellationToken, ("post", postId), ("author", authorId), ("stored", storedAt));
        return (await Find(postId, cancellationToken))!;
    }

    public Task Reached(Guid postId, long followers, CancellationToken cancellationToken) =>
        db.Execute("UPDATE timeline.fanout_progress SET followers_reached = @n WHERE post_id = @post",
            cancellationToken, ("post", postId), ("n", followers));

    public async Task<bool> Complete(Guid postId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // Only the call that moves completed_at from null completes it: a redelivery measures nothing.
        var completed = await db.Read($"""
            UPDATE timeline.fanout_progress SET completed_at = @at WHERE post_id = @post AND completed_at IS NULL
            RETURNING {Columns}
            """, Row, cancellationToken, ("post", postId), ("at", at));
        foreach (var delivery in completed)
        {
            TimelineMetrics.FanoutLag.Record((delivery.FannedOutAt!.Value - delivery.StoredAt).TotalSeconds);
        }

        return completed.Count == 1;
    }

    public async Task<Delivery?> Find(Guid postId, CancellationToken cancellationToken) =>
        (await db.Read($"SELECT {Columns} FROM timeline.fanout_progress WHERE post_id = @post", Row, cancellationToken, ("post", postId)))
            .SingleOrDefault();

    private static Delivery Row(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.GetInt64(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4));
}
