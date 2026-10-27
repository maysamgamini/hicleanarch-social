using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Timeline.Application;

namespace Integration.Tests;

/// <summary>The returning user: page 1 on the request, the timeline from a rebuild job just after.</summary>
public sealed class RebuildTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static async Task<JsonElement> Page(HttpClient client, int limit = 20, string? cursor = null) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/feed?limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"), Ct);

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid())];

    private static string? Next(JsonElement page) =>
        page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;

    /// <summary>Authors with posts, each in its own millisecond, oldest first; and the reader following them all.</summary>
    private static async Task<List<Guid>> Followed(People people, Guid reader, int authors, int postsEach)
    {
        var ids = await people.SignUp(authors);
        foreach (var author in ids)
        {
            await people.Follow(reader, author);
        }

        var posts = new List<Guid>();
        for (var round = 0; round < postsEach; round++)
        {
            foreach (var author in ids)
            {
                posts.Add(await people.Post(author, $"post {round}"));
                await Task.Delay(2, Ct);
            }
        }

        return posts;
    }

    [Fact]
    public async Task A_reader_back_after_30_days_gets_page_1_within_the_feed_budget()
    {
        var people = new People(StartHost());
        var reader = await people.SignUp();
        var posts = await Followed(people, reader, 30, 2);
        // 270 more followed accounts that never posted, as a long-time reader has.
        await Db.Execute("INSERT INTO relationships.follows (follower_id, followee_id) SELECT @r, gen_random_uuid() FROM generate_series(1, 270)", ("r", reader));
        await Delivered();
        await using (var timelines = new Timelines(Db.Valkey))
        {
            await timelines.Db.KeyDeleteAsync(Timelines.Key(reader)); // expired: the reader has been away
        }

        await Page(people.As(await people.SignUp())); // warm: the first request of a process is not the reader's
        var clock = Stopwatch.StartNew();
        var page = await Page(people.As(reader));
        clock.Stop();

        Assert.Equal(posts.AsEnumerable().Reverse().Take(20), Ids(page));
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(200)); // the feed's budget, a design objective
        await TimelineBuilt(reader);
    }

    /// <summary>Counts the timelines each rebuild creates.</summary>
    private sealed class Counting(ITimelines inner, ConcurrentDictionary<Guid, int> created) : ITimelines
    {
        public async Task<bool> CreateEmpty(Guid owner, CancellationToken cancellationToken)
        {
            var made = await inner.CreateEmpty(owner, cancellationToken);
            if (made)
            {
                created.AddOrUpdate(owner, 1, (_, n) => n + 1);
            }

            return made;
        }

        public Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken) => inner.AddToExisting(owners, postId, cancellationToken);

        public Task<bool> Exists(Guid owner, CancellationToken cancellationToken) => inner.Exists(owner, cancellationToken);

        public Task Fill(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => inner.Fill(owner, postIds, cancellationToken);

        public Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken) =>
            inner.Read(owner, maxScore, skip, count, cancellationToken);

        public Task Touch(Guid owner, CancellationToken cancellationToken) => inner.Touch(owner, cancellationToken);

        public Task Remove(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => inner.Remove(owner, postIds, cancellationToken);
    }

    [Fact]
    public async Task Two_simultaneous_returns_make_one_rebuild()
    {
        var created = new ConcurrentDictionary<Guid, int>();
        var host = StartHost(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ITimelines));
            services.Remove(real);
            services.AddSingleton<ITimelines>(sp => new Counting((ITimelines)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!), created));
        });
        var people = new People(host);
        var reader = await people.SignUp();
        var posts = await Followed(people, reader, 3, 2);

        var pages = await Task.WhenAll(Page(people.As(reader)), Page(people.As(reader)));
        await TimelineBuilt(reader);
        await Eventually(async () => await Db.Scalar<long>("SELECT count(*) FROM timeline.rebuild_jobs") == 0);

        Assert.All(pages, p => Assert.Equal(posts.AsEnumerable().Reverse(), Ids(p)));
        Assert.Equal(1, created.GetValueOrDefault(reader));
    }

    [Fact]
    public async Task Page_2_comes_from_the_rebuilt_timeline_with_no_repeat_and_no_gap()
    {
        var people = new People(StartHost());
        var reader = await people.SignUp();
        // 25 followed authors: more than page 1's 20, so page 1 stops at the 21st author's last post.
        var posts = await Followed(people, reader, 25, 2);
        await Delivered();

        var first = await Page(people.As(reader), limit: 20);
        var seen = Ids(first);
        Assert.NotEmpty(seen);
        await TimelineBuilt(reader);
        await using (var timelines = new Timelines(Db.Valkey))
        {
            Assert.Equal(posts.Count, (int)await timelines.Db.SortedSetLengthAsync(Timelines.Key(reader)) - 1); // the sentinel
        }

        for (var cursor = Next(first); cursor is not null;)
        {
            var page = await Page(people.As(reader), limit: 20, cursor);
            seen.AddRange(Ids(page));
            cursor = Next(page);
        }

        Assert.Equal(posts.AsEnumerable().Reverse(), seen);
    }
}
