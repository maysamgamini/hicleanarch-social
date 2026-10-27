using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests;

/// <summary>A deleted post disappears on the next load: the read filter, read repair, removal jobs, and the cache.</summary>
public sealed class DeleteVisibilityTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static async Task<JsonElement> Page(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/v1/feed?limit=20", Ct);

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid())];

    private async Task<(People People, Guid Reader, Guid Ada, Timelines Timelines)> Reading()
    {
        var people = new People(StartHost());
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await Page(people.As(reader));
        await TimelineBuilt(reader);
        return (people, reader, ada, new Timelines(Db.Valkey));
    }

    [Fact]
    public async Task A_deleted_post_never_appears_on_the_next_page_whatever_the_timeline_holds()
    {
        var (people, reader, ada, timelines) = await Reading();
        var kept = await people.Post(ada, "kept");
        var deleted = await people.Post(ada, "deleted");
        await Eventually(() => timelines.Holds(reader, deleted));
        Assert.Equal([deleted, kept], Ids(await Page(people.As(reader)))); // both now in the post cache

        (await people.As(ada).DeleteAsync($"/v1/posts/{deleted}", Ct)).EnsureSuccessStatusCode();
        await Delivered();
        // Whatever the removal job has done by now, the timeline holds the id again.
        await timelines.Db.SortedSetAddAsync(Timelines.Key(reader), Timelines.Member(deleted), Convert.ToInt64(deleted.ToString("N")[..12], 16));

        Assert.Equal([kept], Ids(await Page(people.As(reader))));
    }

    [Fact]
    public async Task A_read_repairs_the_readers_timeline()
    {
        var (people, reader, ada, timelines) = await Reading();
        var post = await people.Post(ada, "deleted, and missed by its removal");
        await Eventually(() => timelines.Holds(reader, post));
        (await people.As(ada).DeleteAsync($"/v1/posts/{post}", Ct)).EnsureSuccessStatusCode();
        await Delivered();
        await Eventually(async () => await Db.Scalar<bool>("SELECT count(*) = 0 FROM timeline.fanout_jobs WHERE post_id = @p AND kind = 'REMOVE' AND status <> 'DONE'", ("p", post)));
        // As a removal that missed this timeline would leave it.
        await timelines.Db.SortedSetAddAsync(Timelines.Key(reader), Timelines.Member(post), Convert.ToInt64(post.ToString("N")[..12], 16));

        Assert.Empty(Ids(await Page(people.As(reader))));
        await Eventually(async () => !await timelines.Holds(reader, post));
    }

    [Fact]
    public async Task A_deleted_post_leaves_every_followers_timeline_without_a_read()
    {
        var (people, reader, ada, timelines) = await Reading();
        var post = await people.Post(ada, "fanned out, then deleted");
        await Eventually(() => timelines.Holds(reader, post));

        (await people.As(ada).DeleteAsync($"/v1/posts/{post}", Ct)).EnsureSuccessStatusCode();

        await Eventually(async () => !await timelines.Holds(reader, post));
        Assert.Equal("DONE", await Db.Scalar<string>("SELECT status FROM timeline.fanout_jobs WHERE post_id = @p AND kind = 'REMOVE'", ("p", post)));
    }

    [Fact]
    public async Task An_edit_shows_on_the_next_load_despite_the_cache()
    {
        var (people, reader, ada, timelines) = await Reading();
        var post = await people.Post(ada, "Teh first");
        await Eventually(() => timelines.Holds(reader, post));
        await Page(people.As(reader)); // cached
        await using (var shortLived = new Timelines(Db.ValkeyShort))
        {
            Assert.True(await shortLived.Db.KeyExistsAsync("post:" + post.ToString("N")));
        }

        (await people.As(ada).PatchAsJsonAsync($"/v1/posts/{post}", new { text = "The first" }, Ct)).EnsureSuccessStatusCode();
        await Delivered();

        Assert.Equal("The first", (await Page(people.As(reader))).GetProperty("items")[0].GetProperty("text").GetString());
    }
}
