using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Timeline.Application;
using Timeline.Domain;

namespace Integration.Tests;

/// <summary>Fanout as durable jobs: page by page, through crashes, in two pools.</summary>
public sealed class DurableFanoutTests(Postgres postgres) : IntegrationTest(postgres)
{
    private WebApplicationFactory<Program> Host(int small, int large, string lease = "00:00:30") =>
        StartHost(settings: web => web
            .UseSetting("Timeline:Fanout:Workers:Small", small.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("Timeline:Fanout:Workers:Large", large.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .UseSetting("Timeline:Fanout:Lease", lease));

    /// <summary>An author with a seeded audience (no profiles needed for fanout), each with a timeline.</summary>
    private async Task<(Guid Author, List<Guid> Followers)> Audience(People people, int size, Timelines timelines)
    {
        var author = await people.SignUp();
        var followers = Enumerable.Range(0, size).Select(_ => Guid.NewGuid()).ToList();
        await Db.Execute("""
            INSERT INTO relationships.follows (follower_id, followee_id) SELECT f, @author FROM unnest(@followers) AS f;
            INSERT INTO relationships.relationship_counts (account_id, followers, following) VALUES (@author, @n, 0);
            """, ("author", author), ("followers", followers.ToArray()), ("n", (long)size));
        await timelines.Create(followers);
        return (author, followers);
    }

    /// <summary>Fails its third write, as a task that dies in the middle of a job.</summary>
    private sealed class DiesOnThirdWrite(ITimelines inner) : ITimelines
    {
        private int _calls;

        public Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 3 ? throw new IOException("the task died") : inner.AddToExisting(owners, postId, cancellationToken);

        public Task<bool> Exists(Guid owner, CancellationToken cancellationToken) => inner.Exists(owner, cancellationToken);

        public Task<bool> CreateEmpty(Guid owner, CancellationToken cancellationToken) => inner.CreateEmpty(owner, cancellationToken);

        public Task Fill(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => inner.Fill(owner, postIds, cancellationToken);

        public Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken) =>
            inner.Read(owner, maxScore, skip, count, cancellationToken);

        public Task Touch(Guid owner, CancellationToken cancellationToken) => inner.Touch(owner, cancellationToken);

        public Task Remove(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) => inner.Remove(owner, postIds, cancellationToken);
    }

    [Fact]
    public async Task A_worker_killed_mid_job_is_replaced_and_the_job_finishes_with_no_page_skipped()
    {
        var timelines = new Timelines(Db.Valkey);
        var idle = Host(small: 0, large: 0, lease: "00:00:02"); // no workers: the job waits
        var people = new People(idle);
        var (author, followers) = await Audience(people, 3_000, timelines);
        var post = await people.Post(author, "through a crash");
        await Eventually(async () => await Db.Scalar<long>("SELECT count(*) FROM timeline.fanout_jobs WHERE post_id = @p", ("p", post)) == 1);

        // A worker takes the job, writes the author and one page, and dies writing the next: no release.
        await using (var scope = idle.Services.CreateAsyncScope())
        {
            var runner = new FanoutRunner(scope.ServiceProvider.GetRequiredService<IFanoutJobs>(),
                new DiesOnThirdWrite(scope.ServiceProvider.GetRequiredService<ITimelines>()),
                scope.ServiceProvider.GetRequiredService<IFollowerPages>(), scope.ServiceProvider.GetRequiredService<IFanoutProgress>(),
                scope.ServiceProvider.GetRequiredService<IFanoutPace>(), TimeProvider.System);
            await Assert.ThrowsAsync<IOException>(() => runner.RunOne(FanoutPool.Large, TimeSpan.FromSeconds(2), null, Ct));
        }

        Assert.Equal(1_000L, await Db.Scalar<long>("SELECT followers_reached FROM timeline.fanout_jobs WHERE post_id = @p", ("p", post)));

        // A replacement starts; once the dead worker's lease runs out it resumes from the cursor.
        _ = Host(small: 1, large: 1, lease: "00:00:02").Services; // a factory starts its host on first use

        await Eventually(async () => await Db.Scalar<string>("SELECT status FROM timeline.fanout_jobs WHERE post_id = @p", ("p", post)) == "DONE", seconds: 30);
        Assert.Equal(followers.Count, await timelines.HoldingCount(followers, post));
        Assert.Equal(3_000L, await Db.Scalar<long>("SELECT followers_reached FROM timeline.fanout_jobs WHERE post_id = @p", ("p", post)));
    }

    [Fact]
    public async Task Two_workers_never_hold_the_same_job_at_once()
    {
        var timelines = new Timelines(Db.Valkey);
        var idle = Host(small: 0, large: 0);
        var people = new People(idle);
        var (author, _) = await Audience(people, 1_500, timelines);
        var post = await people.Post(author, "one holder");
        await Eventually(async () => await Db.Scalar<long>("SELECT count(*) FROM timeline.fanout_jobs WHERE post_id = @p", ("p", post)) == 1);

        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var scope = idle.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IFanoutJobs>().Claim(FanoutPool.Large, TimeSpan.FromSeconds(30), Ct);
        }));

        Assert.Single(claims, c => c is not null);
    }

    [Fact]
    public async Task A_flood_of_large_audience_jobs_does_not_delay_small_audience_jobs()
    {
        var timelines = new Timelines(Db.Valkey);
        var host = Host(small: 2, large: 1); // the large pool is the bottleneck on purpose
        var people = new People(host);
        var large = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var (author, _) = await Audience(people, 5_000, timelines);
            large.Add(await people.Post(author, $"to many {i}"));
        }

        var small = await people.SignUp();
        var fan = await people.SignUp();
        await people.Follow(fan, small);
        await timelines.Create([fan]);
        var started = DateTimeOffset.UtcNow;
        var post = await people.Post(small, "to a few");

        // Objective (design): a small audience's post is fanned out within 5 seconds, whatever large jobs wait.
        await Eventually(() => timelines.Holds(fan, post), seconds: 5);
        var waiting = await Db.Scalar<long>("SELECT count(*) FROM timeline.fanout_jobs WHERE pool = 'Large' AND status <> 'DONE'");
        Assert.True(waiting > 0, "The flood had already drained; the test proves nothing.");
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
