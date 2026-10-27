using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Relationships.Infrastructure.Persistence;

using Social.BuildingBlocks.Infrastructure;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Relationships.Infrastructure;

/// <summary>
/// Relationships' invariant gauge, measured every minute from its own schema: follows that sit beside a
/// block of the same two accounts. It must read zero; anything else is an alarm.
/// </summary>
internal sealed class RelationshipsMonitor(IServiceScopeFactory scopes, ILogger<RelationshipsMonitor> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured FollowsAcrossABlock = new Operations.Measured(
        "social.invariant.follows_across_block", "{follow}", "Follows beside a block of the same pair: must be 0").Register();

    /// <summary>
    /// The age of the oldest follower change not yet folded into the counts (s3-l17): the aggregator
    /// folds every second, so it stays near zero; it grows when no worker is folding. The nightly check
    /// counts the unfolded rows in and so cannot see a stopped aggregator; this gauge does.
    /// </summary>
    public static readonly Operations.Measured LedgerOldestAge = new Operations.Measured(
        "social.relationships.ledger_oldest_age", "s", "Seconds since the oldest follower change still to fold: near 0 while the aggregator runs").Register();

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken)
    {
        await MeasureOnce(services, cancellationToken);
        await MeasureLedger(services, cancellationToken);
    }

    public static async Task<double> MeasureLedger(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<RelationshipsDbContext>();
        var age = (await db.Read("""
            SELECT coalesce(extract(epoch FROM now() - min(created_at)), 0)::float8 FROM relationships.follow_changes
            """, r => r.GetDouble(0), cancellationToken)).Single();
        LedgerOldestAge.Set((long)age);
        return age;
    }

    public static async Task<long> MeasureOnce(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<RelationshipsDbContext>();
        var count = (await db.Read("""
            SELECT count(*) FROM relationships.follows f JOIN relationships.blocks b
              ON (b.blocker_id = f.follower_id AND b.blocked_id = f.followee_id) OR (b.blocker_id = f.followee_id AND b.blocked_id = f.follower_id)
            """, r => r.GetInt64(0), cancellationToken)).Single();
        FollowsAcrossABlock.Set(count);
        return count;
    }
}
