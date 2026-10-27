using Social.BuildingBlocks.Infrastructure.Persistence;

using Timeline.Application;
using Timeline.Domain;

namespace Timeline.Infrastructure.Persistence;

/// <summary>
/// The job table's statements. A claim takes the oldest free job of a pool with FOR UPDATE SKIP LOCKED,
/// so workers claiming at once each get a different job, and stamps it with a lease token; every later
/// step names that token, so a worker whose lease ran out can no longer move the job.
/// </summary>
internal sealed class SqlFanoutJobs(TimelineDbContext db) : IFanoutJobs
{
    public Task Create(Guid postId, Guid authorId, long audience, FanoutPool pool, CancellationToken cancellationToken) =>
        db.Execute("""
            INSERT INTO timeline.fanout_jobs (post_id, author_id, audience, pool, status, author_done, followers_reached, attempts, created_at)
            VALUES (@post, @author, @audience, @pool, 'WAITING', false, 0, 0, now())
            ON CONFLICT (post_id) DO NOTHING
            """, cancellationToken, ("post", postId), ("author", authorId), ("audience", audience), ("pool", pool.ToString()));

    public async Task<FanoutJob?> Claim(FanoutPool pool, TimeSpan lease, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        var claimed = await db.Read("""
            UPDATE timeline.fanout_jobs SET status = 'RUNNING', lease_token = @token, lease_until = now() + @lease, attempts = attempts + 1
            WHERE post_id = (
                SELECT post_id FROM timeline.fanout_jobs
                WHERE pool = @pool AND status <> 'DONE' AND (lease_until IS NULL OR lease_until < now())
                ORDER BY created_at LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING post_id, author_id, cursor, author_done, followers_reached
            """,
            r => new FanoutJob(r.GetGuid(0), r.GetGuid(1), pool, r.IsDBNull(2) ? null : r.GetGuid(2), r.GetBoolean(3), r.GetInt64(4), token),
            cancellationToken, ("token", token), ("lease", lease), ("pool", pool.ToString()));
        return claimed.SingleOrDefault();
    }

    public async Task<bool> Advance(FanoutJob job, TimeSpan lease, CancellationToken cancellationToken) =>
        await db.Execute("""
            UPDATE timeline.fanout_jobs SET cursor = @cursor, author_done = @authorDone, followers_reached = @reached, lease_until = now() + @lease
            WHERE post_id = @post AND lease_token = @token AND status = 'RUNNING'
            """, cancellationToken, ("cursor", (object?)job.Cursor ?? DBNull.Value), ("authorDone", job.AuthorDone), ("reached", job.Reached),
            ("lease", lease), ("post", job.PostId), ("token", job.LeaseToken)) == 1;

    public async Task<bool> Finish(FanoutJob job, CancellationToken cancellationToken) =>
        await db.Execute("""
            UPDATE timeline.fanout_jobs SET status = 'DONE', followers_reached = @reached, lease_token = NULL, lease_until = NULL
            WHERE post_id = @post AND lease_token = @token AND status = 'RUNNING'
            """, cancellationToken, ("reached", job.Reached), ("post", job.PostId), ("token", job.LeaseToken)) == 1;

    public Task Release(FanoutJob job, CancellationToken cancellationToken) =>
        db.Execute("UPDATE timeline.fanout_jobs SET lease_until = now() WHERE post_id = @post AND lease_token = @token AND status = 'RUNNING'",
            cancellationToken, ("post", job.PostId), ("token", job.LeaseToken));
}
