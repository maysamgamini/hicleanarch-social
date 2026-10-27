using System.Net.Http.Json;
using System.Text.Json;

using StackExchange.Redis;

namespace Integration.Tests;

/// <summary>Permissions hold at read time, whatever the timelines still contain.</summary>
public sealed class ReadFilterTests(Postgres postgres) : IntegrationTest(postgres)
{
    private People _people = null!;
    private Timelines _timelines = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _people = new People(StartHost());
        _timelines = new Timelines(Db.Valkey);
    }

    private async Task<List<Guid>> Page(Guid reader) =>
        [.. (await _people.As(reader).GetFromJsonAsync<JsonElement>("/v1/feed", Ct)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("post_id").GetGuid())];

    /// <summary>Puts the posts back into the timeline, as if the best-effort removal had not run.</summary>
    private async Task PutBack(Guid owner, params Guid[] posts) =>
        await Task.WhenAll(posts.Select(p => _timelines.Db.SortedSetAddAsync(Timelines.Key(owner), Timelines.Member(p), Convert.ToInt64(p.ToString("N")[..12], 16))));

    [Fact]
    public async Task After_a_block_neither_page_holds_the_others_posts_even_with_the_ids_still_in_the_timeline()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(ada, bob);
        await _people.Follow(bob, ada);
        await Page(ada);
        await Page(bob);
        await TimelineBuilt(ada, bob);
        var byAda = await _people.Post(ada, "from ada");
        var byBob = await _people.Post(bob, "from bob");
        await Eventually(async () => await _timelines.Holds(ada, byBob) && await _timelines.Holds(bob, byAda));

        await _people.As(ada).PutAsync($"/v1/accounts/{bob}/block", null, Ct);
        await PutBack(ada, byBob);
        await PutBack(bob, byAda);

        Assert.Equal([byAda], await Page(ada));
        Assert.Equal([byBob], await Page(bob));
    }

    [Fact]
    public async Task A_mute_hides_only_one_way()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(ada, bob);
        await _people.Follow(bob, ada);
        var byAda = await _people.Post(ada, "from ada");
        var byBob = await _people.Post(bob, "from bob");

        await _people.As(ada).PutAsync($"/v1/accounts/{bob}/mute", null, Ct);

        Assert.DoesNotContain(byBob, await Page(ada));
        Assert.Contains(byAda, await Page(bob));
    }

    [Fact]
    public async Task An_unfollowed_authors_posts_are_gone_from_the_next_page_even_when_the_removal_has_not_run()
    {
        var (reader, ada) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(reader, ada);
        var post = await _people.Post(ada, "soon unfollowed");
        Assert.Equal([post], await Page(reader));

        await _people.Unfollow(reader, ada);
        await PutBack(reader, post);

        Assert.Empty(await Page(reader));
    }

    [Fact]
    public async Task The_removal_on_unfollow_cleans_the_timeline_within_one_dispatch()
    {
        var (reader, ada) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(reader, ada);
        var post = await _people.Post(ada, "cleaned up");
        await Page(reader);
        await TimelineBuilt(reader);
        Assert.True(await _timelines.Holds(reader, post));

        await _people.Unfollow(reader, ada);

        await Eventually(async () => !await _timelines.Holds(reader, post));
    }

    [Fact]
    public async Task A_new_follow_shows_the_followees_recent_posts()
    {
        var (reader, ada) = (await _people.SignUp(), await _people.SignUp());
        await Page(reader);
        await TimelineBuilt(reader); // the reader's timeline exists, empty
        var posts = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            posts.Add(await _people.Post(ada, $"before the follow {i}"));
            await Task.Delay(2, Ct);
        }

        await _people.Follow(reader, ada);

        await Eventually(async () => await _timelines.Holds(reader, posts[^1]));
        Assert.Equal(posts.AsEnumerable().Reverse(), await Page(reader));
    }

    [Fact]
    public async Task A_deactivated_authors_posts_leave_every_feed()
    {
        var (reader, ada) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(reader, ada);
        await _people.Post(ada, "about to leave");

        await _people.As(ada).PostAsync("/v1/me/deactivate", null, Ct);

        Assert.Empty(await Page(reader));
    }
}
