using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Relationships.Application;
using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Events;

namespace Integration.Tests;

/// <summary>The follower counter is a hot row: follows append to a ledger, a worker folds it every second.</summary>
public sealed class HotCounterTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<FollowerCountChanged>> Announced = new();

    private sealed class Recorder : IIntegrationEventHandler<FollowerCountChanged>
    {
        public Task Handle(FollowerCountChanged e, CancellationToken ct)
        {
            Announced.GetOrAdd(e.AccountId, _ => new()).Enqueue(e);
            return Task.CompletedTask;
        }
    }

    private WebApplicationFactory<Program> Start() =>
        StartHost(services => services.AddEventHandler<FollowerCountChanged, Recorder>("Notifications"));

    private Task<long> Followers(Guid account) =>
        Db.Scalar<long>("SELECT coalesce(max(followers), 0) FROM relationships.relationship_counts WHERE account_id = @a", ("a", account));

    [Fact]
    public async Task Ten_thousand_concurrent_follows_of_one_account_never_wait_on_its_row_and_its_count_converges_within_two_seconds()
    {
        // A web task takes the burst: it runs no background work, so the 10,000 FollowStarted events wait
        // in the outbox instead of loading the graph and the timelines, which are not under test here.
        var people = new People(StartHost(settings: web => web.UseSetting("Host:Role", "Web")));
        var star = await people.SignUp();
        // Another transaction holds the star's counts row for the whole burst: a follow that touched it would wait.
        await Db.Execute("INSERT INTO relationships.relationship_counts (account_id, followers, following) VALUES (@a, 0, 0)", ("a", star));
        await using var holder = new NpgsqlConnection(Db.For("Relationships"));
        await holder.OpenAsync(Ct);
        var hold = await holder.BeginTransactionAsync(Ct);
        await using (var take = new NpgsqlCommand("SELECT 1 FROM relationships.relationship_counts WHERE account_id = @a FOR UPDATE", holder, hold))
        {
            take.Parameters.AddWithValue("a", star);
            await take.ExecuteScalarAsync(Ct);
        }

        // The followers need no account row: a follow checks only that the followee is active.
        var fans = Enumerable.Range(0, 10_000).Select(_ => Guid.CreateVersion7()).ToArray();
        using var gate = new SemaphoreSlim(16);
        var burst = Stopwatch.StartNew();
        var replies = await Task.WhenAll(fans.Select(async fan =>
        {
            await gate.WaitAsync(Ct);
            try
            {
                return (await people.Follow(fan, star)).StatusCode;
            }
            finally
            {
                gate.Release();
            }
        }));
        burst.Stop();

        Assert.All(replies, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(10_000L, await Db.Scalar<long>("SELECT count(*) FROM relationships.follows WHERE followee_id = @a", ("a", star)));
        await hold.RollbackAsync(Ct);
        await Db.Execute("UPDATE relationships.outbox SET delivered_at = now() WHERE type = 'FollowStarted'");

        // A worker task starts and folds the ledger: the count is right within two seconds.
        _ = StartHost(settings: web => web.UseSetting("Host:Role", "Worker")).Services;
        var started = Stopwatch.StartNew();
        await Eventually(async () => await Followers(star) == 10_000, seconds: 2);
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task One_FollowerCountChanged_per_changed_account_per_second_carrying_the_new_count()
    {
        var people = new People(Start());
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        // Signed-up fans: a follow by an account the graph does not have is retried by its projection,
        // and 600 of those retries would hold the delivery of the announcements up behind them.
        var fans = await people.SignUp(300);

        // Three seconds of follows: 100 a second, to ada and bob.
        for (var second = 0; second < 3; second++)
        {
            await Task.WhenAll(fans.Skip(second * 100).Take(100).SelectMany(f => new[] { people.Follow(f, ada), people.Follow(f, bob) }));
            await Task.Delay(1000, Ct);
        }

        // The announcements may be handed over in any order: the highest count is the last fold's.
        await Eventually(() => Task.FromResult(Announced.TryGetValue(ada, out var a) && a.Max(e => e.Followers) == 300
            && Announced.TryGetValue(bob, out var b) && b.Max(e => e.Followers) == 300));
        foreach (var account in new[] { ada, bob })
        {
            var events = Announced[account].OrderBy(e => e.OccurredAt).ToList();
            // A fold a second: far fewer announcements than follows, and no more than one per second of the
            // run (the timer ticks every second; a slow fold and a quick one can stamp closer than that).
            var span = events[^1].OccurredAt - events[0].OccurredAt;
            Assert.InRange(events.Count, 1, (int)Math.Floor(span.TotalSeconds) + 2);
            Assert.Equal(events.Select(e => e.Followers).Order(), events.Select(e => e.Followers));
        }
    }

    [Fact]
    public async Task The_5000_follow_limit_holds_under_concurrency()
    {
        var people = new People(Start());
        var ada = await people.SignUp();
        var targets = await people.SignUp(40);
        await Db.Execute("""
            INSERT INTO relationships.follows (follower_id, followee_id) SELECT @ada, gen_random_uuid() FROM generate_series(1, 4990);
            INSERT INTO relationships.relationship_counts (account_id, followers, following) VALUES (@ada, 0, 4990);
            """, ("ada", ada));

        var replies = await Task.WhenAll(targets.Select(t => people.Follow(ada, t)));

        Assert.Equal(10, replies.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(30, replies.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(5000L, await Db.Scalar<long>("SELECT count(*) FROM relationships.follows WHERE follower_id = @a", ("a", ada)));
        Assert.Equal(5000L, await Db.Scalar<long>("SELECT following FROM relationships.relationship_counts WHERE account_id = @a", ("a", ada)));
    }

    [Fact]
    public async Task The_nightly_check_reports_zero_difference_and_finds_a_count_that_drifted()
    {
        var host = Start();
        var people = new People(host);
        var crowd = await people.SignUp(5);
        await Task.WhenAll(from a in crowd from b in crowd where a != b select people.Follow(a, b));
        await Task.WhenAll(people.Unfollow(crowd[0], crowd[1]), people.Unfollow(crowd[2], crowd[3]));

        async Task<long> Check()
        {
            await using var scope = host.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query<CheckCounts, long>(new CheckCounts(), Ct)).Value;
        }

        // Folded or not yet, the counts with the ledger equal the facts.
        Assert.Equal(0L, await Check());
        await LedgerFolded();
        Assert.Equal(0L, await Check());

        await Db.Execute("UPDATE relationships.relationship_counts SET followers = followers + 1 WHERE account_id = @a", ("a", crowd[4]));
        Assert.Equal(1L, await Check());
    }
}
