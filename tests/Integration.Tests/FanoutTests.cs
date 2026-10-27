using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;

using Social.BuildingBlocks.Infrastructure.Events;

using StackExchange.Redis;

using Timeline.Application;

namespace Integration.Tests;

/// <summary>Fanout on write: a stored post reaches the existing timelines of the author and every follower.</summary>
public sealed class FanoutTests(Postgres postgres) : IntegrationTest(postgres)
{
    private WebApplicationFactory<Program> _host = null!;
    private People _people = null!;
    private Timelines _timelines = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _host = StartHost();
        _people = new People(_host);
        _timelines = new Timelines(Db.Valkey);
    }

    private ITimelines Port => _host.Services.GetRequiredService<ITimelines>();

    [Fact]
    public async Task A_post_reaches_the_authors_and_each_followers_timeline()
    {
        var (author, fan) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(fan, author);
        await _timelines.Create([author, fan]);

        var post = await _people.Post(author, "Hello followers");

        await Eventually(async () => await _timelines.Holds(fan, post) && await _timelines.Holds(author, post));
        Assert.Equal((double)TimelineScore(post), await _timelines.Db.SortedSetScoreAsync(Timelines.Key(fan), Timelines.Member(post)));
    }

    [Fact]
    public async Task A_duplicate_delivery_changes_nothing()
    {
        var (author, fan) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(fan, author);
        await _timelines.Create([fan]);
        var post = await _people.Post(author, "Once in effect");
        await Eventually(() => _timelines.Holds(fan, post));
        var before = await _timelines.Db.SortedSetRangeByRankWithScoresAsync(Timelines.Key(fan));

        // The same event again, as after a redelivery: Timeline has no inbox, the add itself is idempotent.
        var payload = await Db.Scalar<string>("SELECT payload::text FROM posts.outbox WHERE type = 'PostPublished'");
        var published = JsonSerializer.Deserialize<PostPublished>(payload, JsonSerializerOptions.Web)!;
        await _host.Services.GetRequiredService<OutboxDispatcher>().Deliver(
            new OutboxRecord("Posts", published.EventId, nameof(PostPublished), payload), Ct);

        Assert.Equal(before, await _timelines.Db.SortedSetRangeByRankWithScoresAsync(Timelines.Key(fan)));
    }

    [Fact]
    public async Task A_post_delivered_late_lands_by_its_time_not_its_arrival()
    {
        var reader = Guid.NewGuid();
        await _timelines.Create([reader]);
        var now = DateTimeOffset.UtcNow;
        var newer = Guid.CreateVersion7(now);
        var older = Guid.CreateVersion7(now.AddMinutes(-30));

        await Port.AddToExisting([reader], newer, Ct);
        await Port.AddToExisting([reader], older, Ct);

        var newestFirst = await _timelines.Db.SortedSetRangeByRankAsync(Timelines.Key(reader), order: Order.Descending);
        Assert.Equal([newer, older, null], newestFirst.Select(Timelines.PostOf));
    }

    [Fact]
    public async Task The_801st_entry_pushes_the_oldest_out()
    {
        var reader = Guid.NewGuid();
        await _timelines.Create([reader]);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var posts = Enumerable.Range(0, 801).Select(i => Guid.CreateVersion7(start.AddMilliseconds(i))).ToList();

        foreach (var post in posts)
        {
            await Port.AddToExisting([reader], post, Ct);
        }

        Assert.Equal(800, await _timelines.Db.SortedSetLengthAsync(Timelines.Key(reader)));
        Assert.False(await _timelines.Holds(reader, posts[0]));
        Assert.True(await _timelines.Holds(reader, posts[1]));
        Assert.True(await _timelines.Holds(reader, posts[800]));
    }

    [Fact]
    public async Task A_missing_timeline_is_not_created_by_fanout()
    {
        var (author, withTimeline, without) = (await _people.SignUp(), await _people.SignUp(), await _people.SignUp());
        await _people.Follow(withTimeline, author);
        await _people.Follow(without, author);
        await _timelines.Create([withTimeline]);

        var post = await _people.Post(author, "Only where a timeline exists");

        // Both followers are in the same page, written in the same round trip.
        await Eventually(() => _timelines.Holds(withTimeline, post));
        Assert.False(await _timelines.Db.KeyExistsAsync(Timelines.Key(without)));
        Assert.False(await _timelines.Db.KeyExistsAsync(Timelines.Key(author)));
    }

    [Fact]
    public async Task A_post_of_an_author_with_10000_followers_reaches_every_existing_timeline()
    {
        var author = await _people.SignUp();
        var followers = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).ToList();
        // The audience as the API would have left it; the followers need no profile for the fanout.
        await Db.Execute("""
            INSERT INTO relationships.follows (follower_id, followee_id) SELECT f, @author FROM unnest(@followers) AS f;
            INSERT INTO relationships.relationship_counts (account_id, followers, following) VALUES (@author, 10000, 0);
            """, ("author", author), ("followers", followers.ToArray()));
        await _timelines.Create(followers);

        var post = await _people.Post(author, "To ten thousand");

        await Eventually(async () => await _timelines.HoldingCount(followers, post) == followers.Count, seconds: 60);
    }

    private static long TimelineScore(Guid post) => Convert.ToInt64(post.ToString("N")[..12], 16);
}
