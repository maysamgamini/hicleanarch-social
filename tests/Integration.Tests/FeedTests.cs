using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;

using Timeline.Application;
using Timeline.Infrastructure;

namespace Integration.Tests;

public sealed class FeedTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static async Task<JsonElement> Feed(HttpClient client, int limit = 20) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/feed?limit={limit}", Ct);

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid())];

    /// <summary>Posts in successive milliseconds, so their order is their time.</summary>
    private static async Task<List<Guid>> Write(People people, Guid author, int count, string prefix = "post")
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(await people.Post(author, $"{prefix} {i}"));
            await Task.Delay(2, Ct);
        }

        return ids;
    }

    [Fact]
    public async Task A_page_holds_the_followed_accounts_newest_posts_newest_first()
    {
        var people = new People(StartHost());
        var (reader, ada, bob, stranger) = (await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await people.Follow(reader, bob);
        var written = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            written.Add(await people.Post(i % 2 == 0 ? ada : bob, $"post {i}"));
            await people.Post(stranger, $"not followed {i}");
            await Task.Delay(2, Ct);
        }

        var page = await Feed(people.As(reader), 10);

        Assert.Equal(written.AsEnumerable().Reverse().Take(10), Ids(page));
        Assert.True(page.GetProperty("has_more").GetBoolean());
        var first = page.GetProperty("items")[0];
        Assert.Equal("post 11", first.GetProperty("text").GetString());
        Assert.Equal(bob, first.GetProperty("author").GetProperty("account_id").GetGuid());
    }

    [Fact]
    public async Task An_edited_post_shows_its_new_text()
    {
        var people = new People(StartHost());
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        var post = await people.Post(ada, "Teh first");
        await Feed(people.As(reader));

        await people.As(ada).PatchAsJsonAsync($"/v1/posts/{post}", new { text = "The first" }, Ct);
        var page = await Feed(people.As(reader));

        Assert.Equal("The first", page.GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task With_10_deleted_posts_among_the_newest_30_the_page_still_has_20()
    {
        var people = new People(StartHost());
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        var posts = await Write(people, ada, 30);
        await Feed(people.As(reader)); // builds the timeline with all 30
        var deleted = posts.Skip(10).Where((_, i) => i % 2 == 0).ToList();
        foreach (var post in deleted)
        {
            await people.As(ada).DeleteAsync($"/v1/posts/{post}", Ct);
        }

        var page = await Feed(people.As(reader));

        Assert.Equal(posts.Except(deleted).Reverse(), Ids(page));
    }

    /// <summary>Publishes a post in the middle of a build: after the timeline exists, before the fill reads Posts.</summary>
    private sealed class PublishDuringBuild(IPostSummaries inner, Func<Task> publish) : IPostSummaries
    {
        public Task<IReadOnlyList<PostCard>> Get(IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => inner.Get(postIds, cancellationToken);

        public async Task<IReadOnlyList<PostCard>> RecentByAuthors(IReadOnlyList<Guid> authors, int perAuthor, CancellationToken cancellationToken)
        {
            // What the build reads is the state before the post: only the fanout can bring it in.
            var before = await inner.RecentByAuthors(authors, perAuthor, cancellationToken);
            await publish();
            return before;
        }
    }

    [Fact]
    public async Task A_post_published_while_a_missing_timeline_is_being_built_is_in_it()
    {
        Guid reader = default, ada = default, late = default;
        People people = null!;
        var timelines = new Timelines(Db.Valkey);
        var host = StartHost(services => services.AddScoped<IPostSummaries>(sp => new PublishDuringBuild(
            new InProcessPostSummaries(sp.GetRequiredService<IDispatcher>()),
            async () =>
            {
                late = await people.Post(ada, "Published during the build");
                await Eventually(() => timelines.Holds(reader, late));
            })));
        people = new People(host);
        (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        var early = await people.Post(ada, "Before the build");

        var page = await Feed(people.As(reader));

        Assert.Equal([late, early], Ids(page));
    }

    [Fact]
    public async Task The_expiry_moves_on_a_read_and_not_on_a_fanout()
    {
        var people = new People(StartHost());
        var timelines = new Timelines(Db.Valkey);
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await Feed(people.As(reader));
        Assert.InRange((await timelines.Db.KeyTimeToLiveAsync(Timelines.Key(reader)))!.Value, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7));
        await timelines.Db.KeyExpireAsync(Timelines.Key(reader), TimeSpan.FromMinutes(10));

        var post = await people.Post(ada, "Fanned out");
        await Eventually(() => timelines.Holds(reader, post));
        var afterFanout = await timelines.Db.KeyTimeToLiveAsync(Timelines.Key(reader));
        await Feed(people.As(reader));
        var afterRead = await timelines.Db.KeyTimeToLiveAsync(Timelines.Key(reader));

        Assert.True(afterFanout <= TimeSpan.FromMinutes(10), $"A fanout moved the expiry to {afterFanout}.");
        Assert.True(afterRead > TimeSpan.FromDays(6.9), $"A read left the expiry at {afterRead}.");
    }

    [Fact]
    public async Task A_timeline_is_built_once()
    {
        var people = new People(StartHost());
        var timelines = new Timelines(Db.Valkey);
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await Write(people, ada, 3);

        await Feed(people.As(reader));
        await timelines.Db.SortedSetRemoveRangeByScoreAsync(Timelines.Key(reader), 1, double.PositiveInfinity);
        var second = await Feed(people.As(reader));

        // The timeline existed (its sentinel), so the second read did not build it again.
        Assert.Empty(Ids(second));
    }
}
