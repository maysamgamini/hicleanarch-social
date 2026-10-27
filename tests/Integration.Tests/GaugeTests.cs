using Microsoft.Extensions.DependencyInjection;

using Relationships.Infrastructure;

using Timeline.Infrastructure;

namespace Integration.Tests;

/// <summary>
/// Each invariant gauge reads 1 after its rule is broken by hand in the test's own stores, and 0
/// otherwise. Gauges are process-wide, so these run alone.
/// </summary>
[Collection(nameof(Measured))]
public sealed class GaugeTests(Postgres postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task Follows_across_a_block_reads_1_after_one_is_written_by_hand_and_0_otherwise()
    {
        var host = StartHost();
        var (ada, bob) = (Guid.NewGuid(), Guid.NewGuid());
        await using var scope = host.Services.CreateAsyncScope();

        Assert.Equal(0L, await RelationshipsMonitor.MeasureOnce(scope.ServiceProvider, Ct));

        // Behind the API's back: a follow and a block of the same pair, which no request can make.
        await Db.Execute("""
            INSERT INTO relationships.follows (follower_id, followee_id) VALUES (@a, @b);
            INSERT INTO relationships.blocks (blocker_id, blocked_id) VALUES (@b, @a);
            """, ("a", ada), ("b", bob));
        Assert.Equal(1L, await RelationshipsMonitor.MeasureOnce(scope.ServiceProvider, Ct));
        Assert.Equal(1, RelationshipsMonitor.FollowsAcrossABlock.Value);

        await Db.Execute("DELETE FROM relationships.blocks WHERE blocker_id = @b", ("b", bob));
        Assert.Equal(0L, await RelationshipsMonitor.MeasureOnce(scope.ServiceProvider, Ct));
    }

    [Fact]
    public async Task Posts_not_fanned_out_after_five_minutes_reads_1_for_a_stuck_post_and_0_otherwise()
    {
        var host = StartHost();
        await using var scope = host.Services.CreateAsyncScope();
        var post = Guid.CreateVersion7();

        Assert.Equal(0L, (await TimelineMonitor.MeasureOnce(scope.ServiceProvider, Ct)).PostsNotFannedOut);

        await Db.Execute("INSERT INTO timeline.fanout_progress (post_id, author_id, stored_at, followers_reached) VALUES (@p, @a, now() - interval '10 minutes', 0)",
            ("p", post), ("a", Guid.NewGuid()));
        Assert.Equal(1L, (await TimelineMonitor.MeasureOnce(scope.ServiceProvider, Ct)).PostsNotFannedOut);
        Assert.Equal(1, TimelineMonitor.PostsNotFannedOut.Value);

        await Db.Execute("UPDATE timeline.fanout_progress SET completed_at = now() WHERE post_id = @p", ("p", post));
        Assert.Equal(0L, (await TimelineMonitor.MeasureOnce(scope.ServiceProvider, Ct)).PostsNotFannedOut);
    }

    [Fact]
    public async Task Timelines_above_800_reads_1_for_an_overfull_timeline_and_0_otherwise()
    {
        var host = StartHost();
        await using var scope = host.Services.CreateAsyncScope();
        var timelines = new Timelines(Db.Valkey);
        var key = Timelines.Key(Guid.NewGuid());

        Assert.Equal(0L, (await TimelineMonitor.MeasureOnce(scope.ServiceProvider, Ct)).TimelinesAbove800);

        // 801 entries written straight into Valkey: the trim that keeps 800 never ran.
        await timelines.Db.SortedSetAddAsync(key, [.. Enumerable.Range(1, 801).Select(i => new StackExchange.Redis.SortedSetEntry(Guid.NewGuid().ToString("N"), i))]);
        Assert.Equal(1L, (await TimelineMonitor.MeasureOnce(scope.ServiceProvider, Ct)).TimelinesAbove800);
        Assert.Equal(1, TimelineMonitor.TimelinesAbove800.Value);

        await timelines.Db.KeyDeleteAsync(key);
        Assert.Equal(0L, (await TimelineMonitor.MeasureOnce(scope.ServiceProvider, Ct)).TimelinesAbove800);
    }
}
