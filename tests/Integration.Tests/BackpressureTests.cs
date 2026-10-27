using System.Diagnostics;
using System.Net.Http.Json;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Timeline.Domain;
using Timeline.Infrastructure;

namespace Integration.Tests;

/// <summary>
/// Backpressure, with Valkey behind Toxiproxy (MIT, ghcr.io/shopify/toxiproxy) so the test can slow it down and speed it up. The
/// fanout's rate falls while Valkey is slow and climbs back after; feed reads stay within budget.
/// </summary>
public sealed class BackpressureTests(Postgres postgres) : IntegrationTest(postgres)
{
    private const int ProxyPort = 8666;

    private IContainer _valkey = null!;
    private IContainer _proxy = null!;
    private HttpClient _control = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        // No network of its own: the proxy reaches Valkey by its address on the default bridge.
        _valkey = new ContainerBuilder("valkey/valkey:8.1")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections")).Build();
        // Two published ports only (its API and the one proxy). The Testcontainers module publishes a
        // range of 32, which the Docker host's port forwarder (colima's ssh forwarder) cannot take at
        // once without dropping every other forwarded connection of the suite.
        _proxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithPortBinding(8474, true).WithPortBinding(ProxyPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8474).ForPath("/version")))
            .Build();
        await Task.WhenAll(_valkey.StartAsync(Ct), _proxy.StartAsync(Ct));
        _control = new HttpClient { BaseAddress = new Uri($"http://{_proxy.Hostname}:{_proxy.GetMappedPublicPort(8474)}") };
        (await _control.PostAsJsonAsync("/proxies", new { name = "valkey", listen = $"0.0.0.0:{ProxyPort}", upstream = $"{_valkey.IpAddress}:6379" }, Ct)).EnsureSuccessStatusCode();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _control.Dispose();
        await _proxy.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    private async Task Slow(int milliseconds) => (await _control.PostAsJsonAsync("/proxies/valkey/toxics",
        new { name = "slow", type = "latency", stream = "downstream", attributes = new { latency = milliseconds, jitter = 0 } }, Ct)).EnsureSuccessStatusCode();

    private async Task Fast() => (await _control.DeleteAsync("/proxies/valkey/toxics/slow", Ct)).EnsureSuccessStatusCode();

    [Fact]
    public async Task While_Valkey_is_slow_the_pools_rate_falls_and_feed_reads_stay_within_budget_then_it_climbs_back_and_every_job_finishes()
    {
        var valkey = $"{_proxy.Hostname}:{_proxy.GetMappedPublicPort(ProxyPort)},abortConnect=false";
        var host = StartHost(settings: web => web
            .UseSetting("ConnectionStrings:Valkey", valkey)
            .UseSetting("Timeline:Fanout:SlowAbove", "00:00:00.005")
            .UseSetting("Timeline:Fanout:Workers:Large", "1")
            .UseSetting("Timeline:Fanout:Ceiling:Large", "20"));
        var pace = host.Services.GetRequiredService<FanoutPace>();
        var people = new People(host);
        var (reader, author) = (await people.SignUp(), await people.SignUp());
        await people.Follow(reader, author);
        await people.As(reader).GetAsync("/v1/feed", Ct);
        await TimelineBuilt(reader); // the reader's timeline exists
        var fans = Enumerable.Range(0, 60_000).Select(_ => Guid.NewGuid()).ToArray();
        await Db.Execute("""
            INSERT INTO relationships.follows (follower_id, followee_id) SELECT f, @author FROM unnest(@fans) AS f;
            INSERT INTO relationships.relationship_counts (account_id, followers, following) VALUES (@author, 60000, 0)
            ON CONFLICT (account_id) DO UPDATE SET followers = relationships.relationship_counts.followers + 60000;
            """, ("author", author), ("fans", fans));

        await Slow(10);
        var post = await people.Post(author, "a large audience, while Valkey is slow");
        await Eventually(() => Task.FromResult(pace.RateOf(FanoutPool.Large) <= 5), seconds: 15);

        // Reads keep their budget meanwhile (design objective: 200 ms at p99; the toxic adds 10 ms per round trip).
        var reads = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            var clock = Stopwatch.StartNew();
            (await people.As(reader).GetAsync("/v1/feed", Ct)).EnsureSuccessStatusCode();
            reads.Add(clock.Elapsed.TotalMilliseconds);
        }

        // The 95th percentile of 40 reads: the objective is a p99, and one read of a shared test machine
        // can stall for reasons of its own.
        reads.Sort();
        Assert.True(reads[37] < 200, $"The 95th percentile feed read took {reads[37]:F0} ms (slowest {reads[^1]:F0} ms).");

        var slowest = pace.RateOf(FanoutPool.Large);
        await Fast();
        await Eventually(() => Task.FromResult(pace.RateOf(FanoutPool.Large) > slowest * 1.3), seconds: 15);

        await Eventually(async () => await Db.Scalar<string>("SELECT status FROM timeline.fanout_jobs WHERE post_id = @p", ("p", post)) == "DONE", seconds: 90);
    }
}
