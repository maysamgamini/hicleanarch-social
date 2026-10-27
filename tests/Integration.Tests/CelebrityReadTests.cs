using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Timeline.Application;

namespace Integration.Tests;

/// <summary>Eight million refreshes: a read storm on one pulled author, coalesced per task.</summary>
public sealed class CelebrityReadTests(Postgres postgres) : IntegrationTest(postgres)
{
    /// <summary>Posts' side of the pull, counted per author, and slowed on demand.</summary>
    private sealed class Counted(IRecentPosts inner, Counts counts) : IRecentPosts
    {
        public async Task<IReadOnlyList<PostCard>> Of(Guid author, Guid? before, int limit, CancellationToken cancellationToken)
        {
            counts.Calls.AddOrUpdate(author, 1, (_, n) => n + 1);
            if (counts.Delay > TimeSpan.Zero)
            {
                await Task.Delay(counts.Delay, CancellationToken.None);
            }

            return await inner.Of(author, before, limit, cancellationToken);
        }
    }

    private sealed class Counts
    {
        public ConcurrentDictionary<Guid, int> Calls { get; } = new();

        public TimeSpan Delay { get; set; }
    }

    private WebApplicationFactory<Program> Start(Counts counts, string budget) =>
        StartHost(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IRecentPosts) && d.IsKeyedService);
            services.Remove(real);
            services.AddKeyedSingleton<IRecentPosts>(real.ServiceKey, (sp, _) => new Counted((IRecentPosts)ActivatorUtilities.CreateInstance(sp, real.KeyedImplementationType!), counts));
        }, web => web.UseSetting("Timeline:Pull:Budget", budget));

    private async Task Promote(Guid author)
    {
        await using var timelines = new Timelines(Db.Valkey);
        await timelines.Db.HashSetAsync("fanout:registry", author.ToString("N"), $"{DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()}:");
    }

    private static async Task<List<Guid>> Feed(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/v1/feed?limit=20", Ct)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid())];

    [Fact]
    public async Task A_thousand_concurrent_reads_on_one_task_send_one_query_for_the_pulled_author()
    {
        var counts = new Counts();
        var people = new People(Start(counts, "00:00:05"));
        var star = await people.SignUp();
        var readers = await people.SignUp(50);
        await Task.WhenAll(readers.Select(r => people.Follow(r, star)));
        await Delivered();
        await Promote(star);
        var post = await people.Post(star, "Everyone refreshes");
        await Eventually(async () => await Db.Scalar<bool>("SELECT count(*) = 1 FROM timeline.fanout_progress WHERE post_id = @p AND completed_at IS NOT NULL", ("p", post)));
        // Every reader's timeline and pull list exist, so the storm below is only reads.
        foreach (var reader in readers)
        {
            await Feed(people.As(reader));
        }

        await TimelineBuilt(readers);

        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // the cached posts are past serving (three seconds)
        counts.Calls.Clear();

        var clock = Stopwatch.StartNew();
        var pages = await Task.WhenAll(Enumerable.Range(0, 1_000).Select(i => Feed(people.As(readers[i % readers.Length]))));
        clock.Stop();

        Assert.All(pages, p => Assert.Equal(new[] { post }, p));
        // One query, plus one background refresh for each further second the storm lasted.
        Assert.InRange(counts.Calls.GetValueOrDefault(star), 1, 1 + (int)clock.Elapsed.TotalSeconds);
    }

    [Fact]
    public async Task A_slow_refresh_never_holds_a_reader()
    {
        var counts = new Counts();
        var people = new People(Start(counts, "00:00:00.025"));
        var (star, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, star);
        await Delivered();
        await Promote(star);
        var post = await people.Post(star, "Served stale");
        await Eventually(async () => await Db.Scalar<bool>("SELECT count(*) = 1 FROM timeline.fanout_progress WHERE post_id = @p AND completed_at IS NOT NULL", ("p", post)));
        await Eventually(async () => (await Feed(people.As(fan))).Contains(post)); // loaded once, quickly

        await Task.Delay(TimeSpan.FromMilliseconds(1_200), Ct); // stale: older than a second, younger than three
        counts.Delay = TimeSpan.FromSeconds(2);
        counts.Calls.Clear();
        var clock = Stopwatch.StartNew();
        var page = await Feed(people.As(fan));
        clock.Stop();

        Assert.Equal(new[] { post }, page); // the cached posts, though older than a second
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, counts.Calls.GetValueOrDefault(star)); // the refresh, running behind the reader
    }
}
