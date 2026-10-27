using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Accounts.Contracts;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Relationships.Application;
using Relationships.Contracts;
using Relationships.Infrastructure;

using Social.BuildingBlocks.Application;

using Timeline.Application;

namespace Integration.Tests;

/// <summary>s3-l17: the review's fixes, each with the test that failed before it, and the pieces Stages 4 and 5 need.</summary>
public sealed class HardeningTests(Postgres postgres) : IntegrationTest(postgres)
{
    private static async Task<TResult> Ask<TQuery, TResult>(WebApplicationFactory<Program> host, TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query<TQuery, TResult>(query, Ct)).Value;
    }

    [Fact]
    public async Task A_stopped_aggregator_shows_in_the_ledger_age_while_the_nightly_check_still_reads_zero()
    {
        // A web task: it takes follows and runs no background work, so nothing folds the ledger.
        var host = StartHost(settings: web => web.UseSetting("Host:Role", "Web"));
        var people = new People(host);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        (await people.Follow(ada, bob)).EnsureSuccessStatusCode();
        await Db.Execute("UPDATE relationships.follow_changes SET created_at = now() - interval '5 minutes'");

        await using var scope = host.Services.CreateAsyncScope();
        var age = await RelationshipsMonitor.MeasureLedger(scope.ServiceProvider, Ct);

        Assert.InRange(age, 299, 600);
        Assert.Equal(0L, await Ask<CheckCounts, long>(host, new CheckCounts()));
    }

    [Fact]
    public async Task A_follow_of_an_account_that_signed_up_a_moment_ago_finds_it_whatever_the_replica_holds()
    {
        // The Accounts reader endpoint has not caught up at all: another database, with no accounts.
        var behind = await Server.NewDatabase();
        var people = new People(StartHost(settings: web => web.UseSetting("ConnectionStrings:AccountsRead", behind.For("Accounts"))));
        var (ada, bob) = (await people.SignUp(), await people.SignUp());

        var reply = await people.Follow(ada, bob);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode); // 404 account_not_found while the check read the replica
    }

    [Fact]
    public async Task The_accounts_export_pages_every_account_exactly_once()
    {
        var host = StartHost();
        var people = new People(host);
        var accounts = await people.SignUp(10);

        var seen = new List<Guid>();
        Guid? after = null;
        do
        {
            var page = await Ask<ExportAccounts, AccountsPage>(host, new ExportAccounts(after, 3));
            seen.AddRange(page.Accounts.Select(a => a.AccountId));
            after = page.Next;
        }
        while (after is not null);

        Assert.Equal(accounts.Order().ToList(), seen.Order().ToList());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task The_pairs_export_pages_every_pair_exactly_once_with_its_version_and_since()
    {
        var host = StartHost();
        var people = new People(host);
        var crowd = await people.SignUp(5);
        foreach (var (a, b) in from a in crowd from b in crowd where a != b select (a, b))
        {
            await people.Follow(a, b);
        }

        await people.Unfollow(crowd[0], crowd[1]);
        await people.As(crowd[2]).PutAsync($"/v1/accounts/{crowd[3]}/block", null, Ct);

        var pairs = new List<ExportedPair>();
        (Guid? Low, Guid? High) after = (null, null);
        do
        {
            var page = await Ask<ExportPairs, PairsPage>(host, new ExportPairs(after.Low, after.High, 4));
            pairs.AddRange(page.Pairs);
            after = (page.NextLow, page.NextHigh);
        }
        while (after.Low is not null);

        Assert.Equal(10, pairs.Count); // 5 people: 10 pairs, each once
        Assert.Equal(10, pairs.Select(p => (p.LowId, p.HighId)).Distinct().Count());
        foreach (var pair in pairs)
        {
            Assert.Equal(await Db.Scalar<long>("SELECT version FROM relationships.pair_versions WHERE low_id = @l AND high_id = @h", ("l", pair.LowId), ("h", pair.HighId)), pair.PairVersion);
        }

        var unfollowed = pairs.Single(p => new[] { p.LowId, p.HighId }.Order().SequenceEqual(new[] { crowd[0], crowd[1] }.Order()));
        Assert.Equal([(crowd[1], crowd[0])], unfollowed.Edges.Where(e => e.Kind == "Follow").Select(e => (e.From, e.To)));
        var blocked = pairs.Single(p => new[] { p.LowId, p.HighId }.Order().SequenceEqual(new[] { crowd[2], crowd[3] }.Order()));
        Assert.Equal([("Block", crowd[2], crowd[3])], blocked.Edges.Select(e => (e.Kind, e.From, e.To)));
        Assert.All(pairs.SelectMany(p => p.Edges), e => Assert.True(e.Since > DateTimeOffset.UtcNow.AddMinutes(-10)));
    }

    /// <summary>Posts' answer for one author arrives after the budget.</summary>
    private sealed class Slow(IRecentPosts inner, ConcurrentDictionary<Guid, bool> slow) : IRecentPosts
    {
        public async Task<IReadOnlyList<PostCard>> Of(Guid author, Guid? before, int limit, CancellationToken cancellationToken)
        {
            if (slow.ContainsKey(author))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }

            return await inner.Of(author, before, limit, cancellationToken);
        }
    }

    [Fact]
    public async Task A_page_whose_pulled_author_timed_out_is_marked_partial_and_one_whose_did_not_is_not()
    {
        var slow = new ConcurrentDictionary<Guid, bool>();
        var people = new People(StartHost(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IRecentPosts) && d.IsKeyedService);
            services.Remove(real);
            services.AddKeyedSingleton<IRecentPosts>(real.ServiceKey, (sp, _) => new Slow((IRecentPosts)ActivatorUtilities.CreateInstance(sp, real.KeyedImplementationType!), slow));
        }, web => web.UseSetting("Timeline:Pull:Budget", "00:00:00.200")));
        var (star, fan) = (await people.SignUp(), await people.SignUp());
        await people.Follow(fan, star);
        await Delivered();
        await using (var shortLived = new Timelines(Db.ValkeyShort))
        {
            await shortLived.Db.HashSetAsync("fanout:registry", star.ToString("N"), $"{DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()}:");
        }

        await people.Post(star, "pulled");
        await Delivered();

        Assert.False((await people.As(fan).GetFromJsonAsync<JsonElement>("/v1/feed", Ct)).GetProperty("partial").GetBoolean());
        slow[star] = true;
        await Task.Delay(TimeSpan.FromSeconds(3.5), Ct); // past the micro-cache's three seconds
        Assert.True((await people.As(fan).GetFromJsonAsync<JsonElement>("/v1/feed", Ct)).GetProperty("partial").GetBoolean());
    }
}
