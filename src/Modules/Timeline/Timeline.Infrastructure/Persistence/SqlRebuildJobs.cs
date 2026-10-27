using Social.BuildingBlocks.Infrastructure.Persistence;

using Timeline.Application;

namespace Timeline.Infrastructure.Persistence;

/// <summary>Rebuild jobs: asked for once per reader, claimed with SKIP LOCKED and a lease, deleted when answered.</summary>
internal sealed class SqlRebuildJobs(TimelineDbContext db) : IRebuildJobs
{
    public Task Request(Guid reader, CancellationToken cancellationToken) =>
        db.Execute("INSERT INTO timeline.rebuild_jobs (reader_id, created_at) VALUES (@reader, now()) ON CONFLICT (reader_id) DO NOTHING",
            cancellationToken, ("reader", reader));

    public async Task<Guid?> Claim(TimeSpan lease, CancellationToken cancellationToken)
    {
        var claimed = await db.Read("""
            UPDATE timeline.rebuild_jobs SET lease_until = now() + @lease
            WHERE reader_id = (
                SELECT reader_id FROM timeline.rebuild_jobs
                WHERE lease_until IS NULL OR lease_until < now()
                ORDER BY created_at LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING reader_id
            """, r => r.GetGuid(0), cancellationToken, ("lease", lease));
        return claimed.Count == 1 ? claimed[0] : null;
    }

    public Task Finish(Guid reader, CancellationToken cancellationToken) =>
        db.Execute("DELETE FROM timeline.rebuild_jobs WHERE reader_id = @reader", cancellationToken, ("reader", reader));
}
