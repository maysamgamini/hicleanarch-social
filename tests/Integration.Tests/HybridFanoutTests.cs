using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;

using Timeline.Application;
using Timeline.Domain;

namespace Integration.Tests;

/// <summary>Celebrities: pushed for most authors, pulled at read time for authors with very many followers.</summary>
public sealed class HybridFanoutTests(Postgres postgres) : IntegrationTest(postgres)
{
    private Timelines _timelines = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _timelines = new Timelines(Db.Valkey);
    }

    public override async ValueTask DisposeAsync()
    {
        await _timelines.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>A generous budget, so a cold test database never skips a pull; the slow-pull test keeps 25 ms.</summary>
    private WebApplicationFactory<Program> Start(Action<IServiceCollection>? services = null, bool realBudget = false) =>
        StartHost(services, web => web.UseSetting("Timeline:Pull:Budget", realBudget ? "00:00:00.025" : "00:00:05"));

    private static async Task<List<Guid>> Feed(HttpClient client, int limit = 20, string? cursor = null) =>
        Ids(await Page(client, limit, cursor));

    private static async Task<JsonElement> Page(HttpClient client, int limit = 20, string? cursor = null) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/feed?limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"), Ct);

    private static List<Guid> Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post_id").GetGuid())];

    /// <summary>Puts the author in the registry as Relationships' counts would have: promoted that long ago.</summary>
    private Task<bool> Promote(Guid author, TimeSpan ago, TimeSpan? demotedAgo = null) =>
        _timelines.Db.HashSetAsync("fanout:registry", author.ToString("N"),
            $"{(DateTimeOffset.UtcNow - ago).ToUnixTimeMilliseconds()}:{(demotedAgo is { } d ? (DateTimeOffset.UtcNow - d).ToUnixTimeMilliseconds() : "")}");

    private Task<bool> ForgetPullList(Guid reader) => _timelines.Db.KeyDeleteAsync("pulls:" + reader.ToString("N"));

    private Task FannedOut(Guid post) =>
        Eventually(async () => await Db.Scalar<bool>("SELECT count(*) = 1 FROM timeline.fanout_progress WHERE post_id = @p AND completed_at IS NOT NULL", ("p", post)));

    [Fact]
    public async Task A_pulled_authors_post_reaches_the_feed_with_no_timeline_write_and_is_in_the_authors_own_feed()
    {
        var people = new People(Start());
        var (star, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, star);
        await Delivered(); // the follow's backfill cannot race the posts below
        await Feed(people.As(fan));
        await Feed(people.As(star)); // both timelines exist
        await Promote(star, ago: PullRules.PushAfterPromotion + TimeSpan.FromMinutes(1));
        await ForgetPullList(fan); // the fan's list, cached before the promotion

        var post = await people.Post(star, "To millions");
        await FannedOut(post);

        Assert.False(await _timelines.Holds(fan, post));
        Assert.Equal(0L, await Db.Scalar<long>("SELECT count(*) FROM timeline.fanout_jobs WHERE post_id = @p", ("p", post)));
        Assert.True(await _timelines.Holds(star, post));
        Assert.Equal(new[] { post }, await Feed(people.As(fan)));
        Assert.Equal(new[] { post }, await Feed(people.As(star)));
    }

    [Fact]
    public async Task Within_twelve_minutes_of_promotion_an_author_is_still_pushed()
    {
        var people = new People(Start());
        var (star, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, star);
        await Delivered(); // the follow's backfill cannot race the posts below
        await Feed(people.As(fan));
        await Promote(star, ago: TimeSpan.FromMinutes(5));
        await ForgetPullList(fan); // the fan's list, cached before the promotion

        var post = await people.Post(star, "Still pushed");
        await FannedOut(post);

        Assert.True(await _timelines.Holds(fan, post));
    }

    [Fact]
    public async Task An_author_promoted_mid_post_is_seen_once()
    {
        var people = new People(Start());
        var (star, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, star);
        await Delivered(); // the follow's backfill cannot race the posts below
        await Feed(people.As(fan));
        var pushed = await people.Post(star, "Pushed before the promotion");
        await FannedOut(pushed);
        Assert.True(await _timelines.Holds(fan, pushed));

        await Promote(star, ago: PullRules.PushAfterPromotion + TimeSpan.FromMinutes(1));
        await ForgetPullList(fan);
        var pulled = await people.Post(star, "Pulled after it");
        await FannedOut(pulled);

        // The first post is in the timeline and among the star's newest: shown once.
        Assert.Equal(new[] { pulled, pushed }, await Feed(people.As(fan)));
    }

    [Fact]
    public async Task A_demoted_authors_earlier_posts_are_still_found_and_new_ones_are_pushed_again()
    {
        var people = new People(Start());
        var (star, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, star);
        await Delivered(); // the follow's backfill cannot race the posts below
        await Feed(people.As(fan));
        await Promote(star, ago: TimeSpan.FromDays(1));
        await ForgetPullList(fan); // the fan's list, cached before the promotion
        var whilePulled = await people.Post(star, "While pulled");
        await FannedOut(whilePulled);
        Assert.False(await _timelines.Holds(fan, whilePulled));

        await Promote(star, ago: TimeSpan.FromDays(1), demotedAgo: TimeSpan.FromMinutes(1));
        await ForgetPullList(fan);
        var afterDemotion = await people.Post(star, "Pushed again");
        await FannedOut(afterDemotion);

        Assert.True(await _timelines.Holds(fan, afterDemotion));
        Assert.Equal(new[] { afterDemotion, whilePulled }, await Feed(people.As(fan)));
    }

    [Fact]
    public async Task Pulled_posts_page_with_the_timeline_with_no_repeats_and_no_gaps()
    {
        var people = new People(Start());
        var (star, friend, fan) = (await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(fan, star);
        await Delivered(); // the follow's backfill cannot race the posts below
        await people.Follow(fan, friend);
        await Feed(people.As(fan));
        await Promote(star, ago: TimeSpan.FromHours(1));
        await ForgetPullList(fan); // the fan's list, cached before the promotion
        var written = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            written.Add(await people.Post(i % 2 == 0 ? star : friend, $"post {i}"));
            await Task.Delay(2, Ct);
        }

        foreach (var post in written)
        {
            await FannedOut(post);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Page(people.As(fan), 7, cursor);
            seen.AddRange(Ids(page));
            cursor = page.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        Assert.Equal(written.AsEnumerable().Reverse(), seen);
    }

    /// <summary>Posts' answer for one author arrives after the budget.</summary>
    private sealed class Slow(IRecentPosts inner, System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> slowAuthors) : IRecentPosts
    {
        public async Task<IReadOnlyList<PostCard>> Of(Guid author, Guid? before, int limit, CancellationToken cancellationToken)
        {
            if (slowAuthors.ContainsKey(author))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            return await inner.Of(author, before, limit, cancellationToken);
        }
    }

    [Fact]
    public async Task A_slow_pull_is_skipped_and_the_page_is_still_served()
    {
        var slow = new System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>();
        var people = new People(Start(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IRecentPosts) && d.IsKeyedService);
            services.Remove(real);
            services.AddKeyedSingleton<IRecentPosts>(real.ServiceKey, (sp, _) => new Slow((IRecentPosts)ActivatorUtilities.CreateInstance(sp, real.KeyedImplementationType!), slow));
        }, realBudget: true));
        var (star, friend, fan) = (await people.SignUp(), await people.SignUp(), await people.SignUp());
        slow[star] = true;
        await people.Follow(fan, star);
        await Delivered(); // the follow's backfill cannot race the posts below
        await people.Follow(fan, friend);
        await Feed(people.As(fan));
        await Promote(star, ago: TimeSpan.FromHours(1));
        await ForgetPullList(fan); // the fan's list, cached before the promotion
        var late = await people.Post(star, "Late");
        var onTime = await people.Post(friend, "On time");
        await FannedOut(late);
        await FannedOut(onTime);

        var clock = Stopwatch.StartNew();
        var page = await Feed(people.As(fan));
        clock.Stop();

        Assert.Equal(new[] { onTime }, page);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_registry_follows_the_counts_promoted_at_25000_and_demoted_below_20000()
    {
        var host = Start();
        var author = Guid.NewGuid();

        async Task<PulledAuthor?> Observe(long followers)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<ObserveFollowerCount, PulledAuthor?>(new ObserveFollowerCount(author, followers), Ct)).Value;
        }

        Assert.Null(await Observe(24_999));
        var promoted = await Observe(25_000);
        Assert.NotNull(promoted);
        Assert.Equal(promoted, await Observe(20_000)); // between the two lines: unchanged
        Assert.NotNull((await Observe(19_999))!.DemotedAt);
        Assert.True(await _timelines.Db.HashExistsAsync("fanout:registry", author.ToString("N")));
    }
}
