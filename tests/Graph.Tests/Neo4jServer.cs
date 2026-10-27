using Neo4j.Driver;

using Recommendations.Infrastructure.Graph;

using Testcontainers.Neo4j;

[assembly: AssemblyFixture(typeof(Graph.Tests.Neo4jServer))]

namespace Graph.Tests;

/// <summary>
/// One Neo4j 5.26 Community for the run, authentication off, its setup step applied once. Tests share
/// it without cleaning up: every account a test makes has a fresh id, so no two tests meet.
/// </summary>
public sealed class Neo4jServer : IAsyncLifetime
{
    private readonly Neo4jContainer _container = new Neo4jBuilder("neo4j:5.26-community")
        .WithEnvironment("NEO4J_AUTH", "none")
        .Build();

    public IDriver Driver { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Driver = GraphDatabase.Driver(_container.GetConnectionString(), AuthTokens.None);
        await Neo4jSetup.Run(Driver, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Driver.DisposeAsync();
        await _container.DisposeAsync();
    }
}
