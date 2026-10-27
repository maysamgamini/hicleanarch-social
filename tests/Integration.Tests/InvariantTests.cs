using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Infrastructure.Events;

using Timeline.Application;

namespace Integration.Tests;

/// <summary>
/// The seven invariants of the build spec (s1-a13), one named test each, on real PostgreSQL 17, Valkey 8
/// and Neo4j 5.26. Each one fails when its guard is removed: scripts/mutation-check.sh removes them one
/// at a time and runs the matching test.
/// </summary>
public sealed class InvariantTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static async Task<List<Guid>> Page(HttpClient client, string? cursor = null, int limit = 20)
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/v1/feed?limit={limit}{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}", Ct);
        return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid())];
    }

    private Task<long> FollowsAcrossABlock() => Db.Scalar<long>("""
        SELECT count(*) FROM relationships.follows f JOIN relationships.blocks b
          ON (b.blocker_id = f.follower_id AND b.blocked_id = f.followee_id) OR (b.blocker_id = f.followee_id AND b.blocked_id = f.follower_id)
        """);

    [Fact]
    public async Task Invariant_1_no_follow_across_a_block()
    {
        var people = new People(StartHost());
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        await people.Follow(ada, bob);
        await people.Follow(bob, ada);

        await people.As(ada).PutAsync($"/v1/accounts/{bob}/block", null, Ct);
        var byBlocker = await people.Follow(ada, bob);
        var byBlocked = await people.Follow(bob, ada);

        Assert.Equal(HttpStatusCode.Conflict, byBlocker.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, byBlocked.StatusCode);
        Assert.Equal(0L, await FollowsAcrossABlock());
    }

    [Fact]
    public async Task Invariant_2_a_shown_post_is_allowed()
    {
        var people = new People(StartHost());
        var timelines = new Timelines(Db.Valkey);
        var reader = await people.SignUp();
        var (fine, blocked, blocking, muted, gone, unfollowed, deleter) =
            (await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp());
        foreach (var author in new[] { fine, blocked, blocking, muted, gone, unfollowed, deleter })
        {
            await people.Follow(reader, author);
        }

        var posts = new Dictionary<Guid, Guid>();
        foreach (var author in new[] { fine, blocked, blocking, muted, gone, unfollowed, deleter })
        {
            posts[author] = await people.Post(author, "post");
        }

        await Page(people.As(reader));
        await TimelineBuilt(reader); // the reader's timeline, with all seven
        await people.As(reader).PutAsync($"/v1/accounts/{blocked}/block", null, Ct);
        await people.As(blocking).PutAsync($"/v1/accounts/{reader}/block", null, Ct);
        await people.As(reader).PutAsync($"/v1/accounts/{muted}/mute", null, Ct);
        await people.As(gone).PostAsync("/v1/me/deactivate", null, Ct);
        await people.Unfollow(reader, unfollowed);
        await people.As(deleter).DeleteAsync($"/v1/posts/{posts[deleter]}", Ct);
        // Whatever the upkeep removed, every id is back in the timeline: only the read filter stands between them and the page.
        foreach (var post in posts.Values)
        {
            await timelines.Db.SortedSetAddAsync(Timelines.Key(reader), Timelines.Member(post), Convert.ToInt64(post.ToString("N")[..12], 16));
        }

        Assert.Equal([posts[fine]], await Page(people.As(reader)));
    }

    [Fact]
    public async Task Invariant_3_no_repeats_no_gaps()
    {
        var people = new People(StartHost());
        var timelines = new Timelines(Db.Valkey);
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        var before = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            before.Add(await people.Post(ada, $"before {i}"));
        }

        // Two of them in the same millisecond, the hardest boundary.
        var moment = DateTimeOffset.UtcNow.AddSeconds(1);
        foreach (var twin in new[] { Guid.CreateVersion7(moment), Guid.CreateVersion7(moment) })
        {
            await Db.Execute("INSERT INTO posts.posts (id, author_id, text, status, created_at) VALUES (@id, @a, 'twin', 'ACTIVE', @at)", ("id", twin), ("a", ada), ("at", moment));
            before.Add(twin);
        }

        var client = people.As(reader);
        var seen = new List<Guid>();
        var first = await client.GetFromJsonAsync<JsonElement>("/v1/feed?limit=7", Ct);
        seen.AddRange(first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid()));
        var arriving = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            arriving.Add(await people.Post(ada, $"arriving {i}"));
        }

        await Eventually(() => timelines.Holds(reader, arriving[^1]));
        var cursor = first.GetProperty("next_cursor").GetString();
        while (cursor is not null)
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"/v1/feed?limit=7&cursor={Uri.EscapeDataString(cursor)}", Ct);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid()));
            cursor = page.GetProperty("next_cursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next_cursor").GetString();
        }

        Assert.Equal(seen.Distinct(), seen);
        Assert.Equal(before.ToHashSet(), seen.ToHashSet());
    }

    [Fact]
    public async Task Invariant_4_a_stored_post_reaches_its_followers()
    {
        Guid fan, post;
        await using (var dying = TestApp.Start(Db, services => services.Configure<OutboxOptions>(o => o.RetryDelays = [TimeSpan.FromMilliseconds(10)])))
        {
            var people = new People(dying);
            var ada = await people.SignUp();
            fan = await people.SignUp();
            await people.Follow(fan, ada);
            await Page(people.As(fan));
            await TimelineBuilt(fan); // the fan's timeline exists
            // The follow's backfill is done first: only the post's own fanout can bring the post now.
            await Delivered();
            post = await people.Post(ada, "stored, then the program stops");
        }

        // Whether the stopped program got as far as the fanout or not, the next one finishes it.
        _ = StartHost().Services;
        var timelines = new Timelines(Db.Valkey);

        await Eventually(() => timelines.Holds(fan, post), seconds: 30);
    }

    [Fact]
    public async Task Invariant_5_position_by_time_not_arrival()
    {
        var host = StartHost();
        var timelines = new Timelines(Db.Valkey);
        var reader = Guid.NewGuid();
        await timelines.Create([reader]);
        var now = DateTimeOffset.UtcNow;
        var written = new[] { Guid.CreateVersion7(now.AddMinutes(-20)), Guid.CreateVersion7(now.AddMinutes(-10)), Guid.CreateVersion7(now) };

        // Delivered in the wrong order: newest first, oldest last.
        foreach (var post in written.Reverse())
        {
            await host.Services.GetRequiredService<ITimelines>().AddToExisting([reader], post, Ct);
            await Task.Delay(5, Ct);
        }

        var oldestFirst = await timelines.Db.SortedSetRangeByScoreAsync(Timelines.Key(reader), start: 1);
        Assert.Equal(written.Select(Timelines.Member), oldestFirst.Select(m => m.ToString()));
    }

    [Fact]
    public async Task Invariant_6_counts_equal_facts()
    {
        var people = new People(StartHost());
        var crowd = await people.SignUp(6);
        var moves = (from a in crowd from b in crowd where a != b select (a, b)).ToList();

        // Everyone follows everyone at once; then half the follows end while the other half are sent again.
        await Task.WhenAll(moves.Select(m => people.Follow(m.a, m.b)));
        await Task.WhenAll(moves.Select((m, i) => i % 2 == 0 ? people.Unfollow(m.a, m.b) : people.Follow(m.a, m.b)));
        // Followers are folded from the ledger every second: the invariant holds once it is folded.
        await LedgerFolded();

        var mismatches = await Db.Scalar<long>("""
            SELECT count(*) FROM relationships.relationship_counts c
            WHERE c.followers <> (SELECT count(*) FROM relationships.follows f WHERE f.followee_id = c.account_id)
               OR c.following <> (SELECT count(*) FROM relationships.follows f WHERE f.follower_id = c.account_id)
            """);
        Assert.Equal(0L, mismatches);
    }

    [Fact]
    public async Task Invariant_7_a_retry_is_not_a_second_post()
    {
        var people = new People(StartHost());
        var ada = await people.SignUp();

        var replies = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => people.Publish(ada, "Sent five times", "one-key")));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(replies.Select(r => r.Headers.Location!.ToString()).Distinct());
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM posts.posts WHERE author_id = @a", ("a", ada)));
    }
}
