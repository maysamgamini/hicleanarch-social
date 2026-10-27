using Recommendations.Application;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Recommendations.Infrastructure.Persistence;

internal sealed class SqlDismissals(RecommendationsDbContext db) : IDismissals
{
    public Task Save(Guid reader, Guid account, DateTimeOffset until, CancellationToken cancellationToken) =>
        db.Execute("""
            INSERT INTO recommendations.dismissals (reader_id, account_id, until) VALUES (@reader, @account, @until)
            ON CONFLICT (reader_id, account_id) DO UPDATE SET until = excluded.until
            """, cancellationToken, ("reader", reader), ("account", account), ("until", until));

    public async Task<IReadOnlyList<(Guid Reader, Guid Account, DateTimeOffset Until)>> InForce(IReadOnlyList<Guid> readers, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Read("SELECT reader_id, account_id, until FROM recommendations.dismissals WHERE reader_id = ANY(@readers) AND until > @now",
            r => (r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2)), cancellationToken, ("readers", readers.ToArray()), ("now", now));

    public Task<int> PurgeBefore(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        db.Execute("DELETE FROM recommendations.dismissals WHERE until < @cutoff", cancellationToken, ("cutoff", cutoff));
}
