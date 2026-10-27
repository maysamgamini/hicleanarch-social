using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Persistence;

using StackExchange.Redis;

using Timeline.Domain;
using Timeline.Infrastructure.Persistence;

namespace Timeline.Infrastructure;

/// <summary>
/// Timeline's invariant gauges, measured every minute, each of which must read zero: posts stored more
/// than five minutes ago whose fanout has not completed, and timelines holding more than 800 entries.
/// The second walks the tl:* keys with SCAN, a sample of at most <see cref="ScanLimit"/> keys a minute
/// in a large keyspace, so the gauge is a sample there, not a census.
/// </summary>
internal sealed class TimelineMonitor(IServiceScopeFactory scopes, ILogger<TimelineMonitor> logger) : ModuleMonitor(scopes, logger)
{
    public const int ScanLimit = 100_000;

    public static readonly TimeSpan FanoutObjective = TimeSpan.FromMinutes(5);

    public static readonly Operations.Measured PostsNotFannedOut = new Operations.Measured(
        "social.invariant.posts_not_fanned_out", "{post}", "Posts stored more than 5 minutes ago and not fanned out: must be 0").Register();

    public static readonly Operations.Measured TimelinesAbove800 = new Operations.Measured(
        "social.invariant.timelines_above_800", "{timeline}", "Timelines holding more than 800 entries: must be 0").Register();

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken) =>
        await MeasureOnce(services, cancellationToken);

    public static async Task<(long PostsNotFannedOut, long TimelinesAbove800)> MeasureOnce(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TimelineDbContext>();
        var late = (await db.Read("SELECT count(*) FROM timeline.fanout_progress WHERE completed_at IS NULL AND stored_at < @cutoff",
            r => r.GetInt64(0), cancellationToken, ("cutoff", DateTimeOffset.UtcNow - FanoutObjective))).Single();

        var valkey = services.GetRequiredService<IConnectionMultiplexer>();
        long above = 0, scanned = 0;
        foreach (var server in valkey.GetServers().Where(s => !s.IsReplica))
        {
            await foreach (var key in server.KeysAsync(pattern: "tl:*", pageSize: 1_000).WithCancellation(cancellationToken))
            {
                if (await valkey.GetDatabase().SortedSetLengthAsync(key) > TimelineRules.MaxEntries)
                {
                    above++;
                }

                if (++scanned >= ScanLimit)
                {
                    break;
                }
            }
        }

        PostsNotFannedOut.Set(late);
        TimelinesAbove800.Set(above);
        return (late, above);
    }
}
