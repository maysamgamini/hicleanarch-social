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

    protected override Task Measure(IServiceProvider services, CancellationToken cancellationToken) => MeasureOnce(services, cancellationToken);

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
