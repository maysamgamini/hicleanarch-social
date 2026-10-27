extern alias gateway;

using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Yarp.ReverseProxy.Forwarder;

namespace Integration.Tests;

/// <summary>A gateway in front of the monolith: one catch-all route, limits per account and per address.</summary>
public sealed class GatewayTests(Postgres postgres) : IntegrationTest(postgres)
{
    private readonly List<IAsyncDisposable> _gateways = [];

    public override async ValueTask DisposeAsync()
    {
        foreach (var g in _gateways)
        {
            await g.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>The gateway's forwarder sends to the monolith's in-memory server instead of a network address.</summary>
    private sealed class ToMonolith(TestServer monolith) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(monolith.CreateHandler(), disposeHandler: false);
    }

    private WebApplicationFactory<gateway::Program> Gateway(WebApplicationFactory<Program> monolith, Action<IWebHostBuilder>? settings = null)
    {
        var server = monolith.Server;
        var factory = new WebApplicationFactory<gateway::Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing")
                .UseSetting("ConnectionStrings:ValkeyShort", Db.ValkeyShort)
                .UseSetting("Auth:ClientId", TestApp.ClientId)
                .UseSetting("Auth:LocalSigningKey", TestApp.SigningKey)
                .UseSetting("ReverseProxy:Clusters:monolith:Destinations:monolith:Address", "http://localhost/")
                // Every test's own counters: the short-lived store is shared by the whole run.
                .UseSetting("Gateway:Limits:Prefix", "rl-" + Guid.NewGuid().ToString("N"))
                .ConfigureTestServices(s => s.AddSingleton<IForwarderHttpClientFactory>(new ToMonolith(server)));
            settings?.Invoke(web);
        });
        _gateways.Add(factory);
        return factory;
    }

    private static HttpClient As(WebApplicationFactory<gateway::Program> gateway, Guid? user = null)
    {
        var client = gateway.CreateClient();
        if (user is { } id)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApp.Token(id));
        }

        return client;
    }

    [Fact]
    public async Task Every_public_path_reaches_the_monolith_through_the_gateway()
    {
        var monolith = StartHost();
        var gateway = Gateway(monolith);
        var people = new People(monolith);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());
        var post = await people.Post(ada, "through the gateway");

        var requests = new (HttpMethod Method, string Path, Guid? As, object? Body)[]
        {
            (HttpMethod.Get, "/health/ready", null, null),
            (HttpMethod.Get, "/v1/me", ada, null),
            (HttpMethod.Get, $"/v1/posts/{post}", ada, null),
            (HttpMethod.Post, "/v1/posts", ada, new { text = "posted through the gateway" }),
            (HttpMethod.Put, $"/v1/accounts/{ada}/follow", bob, null),
            (HttpMethod.Get, $"/v1/accounts/{ada}/followers", bob, null),
            (HttpMethod.Get, "/v1/feed", bob, null),
            (HttpMethod.Get, "/v1/notifications", ada, null),
            (HttpMethod.Get, "/v1/suggestions/who-to-follow", bob, null),
            (HttpMethod.Get, "/v1/feed", null, null), // no token: the monolith's 401, unchanged
        };

        foreach (var (method, path, user, body) in requests)
        {
            var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            if (method == HttpMethod.Post)
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            }

            var through = await As(gateway, user).SendAsync(request, Ct);
            Assert.True(through.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.BadGateway or HttpStatusCode.TooManyRequests),
                $"{method} {path} did not reach the monolith: {(int)through.StatusCode}.");
            if (method == HttpMethod.Get)
            {
                // A read answered through the gateway is answered as the monolith answers it directly.
                Assert.Equal((await TestApp.Client(monolith, user).GetAsync(path, Ct)).StatusCode, through.StatusCode);
            }
        }
    }

    [Fact]
    public async Task The_21st_post_in_a_minute_from_one_account_is_429_while_another_account_at_the_same_address_still_posts()
    {
        var monolith = StartHost();
        var gateway = Gateway(monolith);
        var people = new People(monolith);
        var (ada, bob) = (await people.SignUp(), await people.SignUp());

        async Task<HttpResponseMessage> Post(Guid author, int i)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/posts") { Content = JsonContent.Create(new { text = $"post {i}" }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await As(gateway, author).SendAsync(request, Ct);
        }

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await Post(ada, i)).StatusCode);
        }

        var refused = await Post(ada, 21);
        var other = await Post(bob, 1); // the same address (the test server's), another account

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(20L, await Db.Scalar<long>("SELECT count(*) FROM posts.posts WHERE author_id = @a", ("a", ada)));
    }

    [Fact]
    public async Task A_forged_X_Forwarded_For_from_outside_is_ignored()
    {
        var monolith = StartHost();
        // The load balancer's network, which the test's caller is not in.
        var gateway = Gateway(monolith, web => web
            .UseSetting("ForwardedHeaders:KnownNetwork", "10.0.0.0/16")
            .UseSetting("Gateway:Limits:AnonymousReadsPerMinute", "3"));

        var replies = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++)
        {
            // Each request claims another address; all of them come from the same caller.
            var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            replies.Add(await As(gateway).SendAsync(request, Ct));
        }

        Assert.All(replies.Take(3), r => Assert.NotEqual(HttpStatusCode.TooManyRequests, r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, replies[3].StatusCode);
    }
}
