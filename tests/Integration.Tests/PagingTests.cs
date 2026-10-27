using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests;

public sealed class PagingTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static async Task<JsonElement> Feed(HttpClient client, int limit, string? cursor = null) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/feed?limit={limit}{(cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor))}", Ct);

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid())];

    private static string? Next(JsonElement page) =>
        page.GetProperty("next_cursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next_cursor").GetString();

    private static async Task<List<Guid>> Write(People people, Guid author, int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(await people.Post(author, $"post {i}"));
            await Task.Delay(2, Ct);
        }

        return ids;
    }

    [Fact]
    public async Task Paging_while_50_posts_arrive_at_the_top_shows_no_repeat_and_no_gap()
    {
        var people = new People(StartHost());
        var timelines = new Timelines(Db.Valkey);
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        var before = await Write(people, ada, 60);

        var seen = new List<Guid>();
        var page = await Feed(people.As(reader), 20);
        seen.AddRange(Ids(page));
        var newer = await Write(people, ada, 50); // arrive at the top while the reader scrolls
        await Eventually(() => timelines.Holds(reader, newer[^1]));
        for (var cursor = Next(page); cursor is not null; cursor = Next(page))
        {
            page = await Feed(people.As(reader), 20, cursor);
            seen.AddRange(Ids(page));
        }

        Assert.Equal(before.AsEnumerable().Reverse(), seen);
    }

    [Fact]
    public async Task Two_posts_in_the_same_millisecond_are_both_shown_once()
    {
        var people = new People(StartHost());
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await people.Post(ada, "the oldest");
        // Two posts written in the same millisecond, as two tasks could: same score, different ids.
        var moment = DateTimeOffset.UtcNow;
        var twins = new[] { Guid.CreateVersion7(moment), Guid.CreateVersion7(moment) };
        foreach (var twin in twins)
        {
            await Db.Execute("INSERT INTO posts.posts (id, author_id, text, status, created_at) VALUES (@id, @author, 'twin', 'ACTIVE', @at)",
                ("id", twin), ("author", ada), ("at", moment));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Feed(people.As(reader), 1, cursor);
            seen.AddRange(Ids(page));
            cursor = Next(page);
        }
        while (cursor is not null);

        Assert.Equal(3, seen.Count);
        Assert.Equal(twins.Order(), seen.Take(2).Order());
        Assert.Equal(seen.Distinct(), seen);
    }

    [Fact]
    public async Task A_cursor_edited_by_hand_or_from_another_reader_is_400_invalid_cursor()
    {
        var people = new People(StartHost());
        var (reader, other, ada) = (await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        await people.Follow(other, ada);
        await Write(people, ada, 5);
        var cursor = Next(await Feed(people.As(reader), 2))!;
        var edited = (cursor[0] == 'A' ? 'B' : 'A') + cursor[1..];

        foreach (var (client, bad) in new[] { (people.As(reader), edited), (people.As(other), cursor), (people.As(reader), "garbage") })
        {
            var reply = await client.GetAsync($"/v1/feed?cursor={Uri.EscapeDataString(bad)}", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
            Assert.Equal("invalid_cursor", (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task A_cursor_signed_with_the_previous_key_is_still_accepted()
    {
        var people = new People(StartHost());
        var (reader, ada) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, ada);
        var posts = await Write(people, ada, 4);
        var cursor = Next(await Feed(people.As(reader), 2))!;

        // The key is rotated: the old one becomes Previous.
        var rotated = new People(StartHost(settings: web => web
            .UseSetting("Timeline:Cursor:Keys:Current", "the-next-cursor-key-0123456789abcdef")
            .UseSetting("Timeline:Cursor:Keys:Previous", TestApp.CursorKey)));
        var page = await Feed(rotated.As(reader), 2, cursor);

        Assert.Equal([posts[1], posts[0]], Ids(page));
    }
}
