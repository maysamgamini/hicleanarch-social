using Microsoft.EntityFrameworkCore;

using Npgsql;

using Recommendations.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Graph.Tests.PostgresGraph))]

namespace Graph.Tests;

/// <summary>One PostgreSQL 17 with Recommendations' schema, migrated once; each test gets a database of its own.</summary>
public sealed class PostgresGraph : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    private readonly SemaphoreSlim _copying = new(1, 1);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await Execute(_container.GetConnectionString(), "CREATE DATABASE graph_template");
        await new RecommendationsMigrator().Migrate(At("graph_template"), CancellationToken.None);
        NpgsqlConnection.ClearAllPools();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>A context on a fresh copy of the migrated schema.</summary>
    internal async Task<RecommendationsDbContext> NewContext()
    {
        var name = "g_" + Guid.NewGuid().ToString("N");
        await _copying.WaitAsync();
        try
        {
            await Execute(_container.GetConnectionString(), $"CREATE DATABASE {name} TEMPLATE graph_template");
        }
        finally
        {
            _copying.Release();
        }

        return new RecommendationsDbContext(RecommendationsDbOptions.Configure(new DbContextOptionsBuilder<RecommendationsDbContext>(), At(name)).Options);
    }

    private string At(string database) => new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
