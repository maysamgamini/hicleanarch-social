using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Infrastructure.Identity;

namespace Integration.Tests;

/// <summary>The host, started in memory on a test database, and clients signed in as people of the test's choosing.</summary>
public static class TestApp
{
    public const string ClientId = "test-client";

    public const string SigningKey = "test-only-signing-key-0123456789abcdef";

    /// <summary>A token for the given person, shaped like a Cognito access token.</summary>
    public static string Token(Guid userId) => LocalTokens.Issue(SigningKey, ClientId, userId);

    /// <summary>A fresh host process on this database: a new host is what a restart looks like.</summary>
    public static WebApplicationFactory<Program> Start(TestDatabase database, Action<IServiceCollection>? services = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing")
                .ConfigureTestServices(s => services?.Invoke(s))
                // UseSetting, not ConfigureAppConfiguration: the host reads connection strings while it is built.
                .UseSetting("ConnectionStrings:Owner", database.Owner)
                .UseSetting("ConnectionStrings:Valkey", database.Valkey)
                .UseSetting("Auth:ClientId", ClientId)
                .UseSetting("Auth:LocalSigningKey", SigningKey);
            foreach (var module in TestDatabase.Modules)
            {
                web.UseSetting($"ConnectionStrings:{module}", database.For(module));
            }
        });

    /// <summary>A client signed in as <paramref name="userId"/>, or anonymous when it is null.</summary>
    public static HttpClient Client(WebApplicationFactory<Program> host, Guid? userId = null)
    {
        var client = host.CreateClient();
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(id));
        }

        return client;
    }
}

/// <summary>
/// A test with a database of its own, made before the test runs, and a host on it. xUnit makes a new
/// instance for every test, so no two tests share rows.
/// </summary>
public abstract class IntegrationTest(Postgres postgres) : IAsyncLifetime
{
    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected TestDatabase Db { get; private set; } = null!;

    /// <summary>The server, for a test that needs a second database.</summary>
    protected Postgres Server { get; } = postgres;

    public virtual async ValueTask InitializeAsync() => Db = await Server.NewDatabase();

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A host on this test's database, stopped when the test ends.</summary>
    protected WebApplicationFactory<Program> StartHost(Action<IServiceCollection>? services = null)
    {
        var host = TestApp.Start(Db, services);
        _hosts.Add(host);
        return host;
    }

    protected static async Task Eventually(Func<Task<bool>> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !await condition())
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(await condition(), "The condition did not hold in time.");
    }
}
