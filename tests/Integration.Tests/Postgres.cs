using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Social.BuildingBlocks.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Integration.Tests.Postgres))]

namespace Integration.Tests;

/// <summary>
/// One real PostgreSQL 17 and one Valkey 8 for the whole run. PostgreSQL It is migrated once, into a template database, by the
/// same code path as the deploy's migration step; every test then gets a database of its own,
/// copied from the template, so tests run in parallel without seeing each other's rows or events.
/// </summary>
public sealed class Postgres : IAsyncLifetime
{
    private const string Template = "social_template";

    // Each test's host keeps a connection pool per module role.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=1000")
        .Build();

    // Valkey as ElastiCache runs it: a plain container, ready when it says so. Tests share it and
    // never collide, because every key names accounts or posts with fresh ids.
    private readonly IContainer _valkey = new ContainerBuilder("valkey/valkey:8.1")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    public string Valkey => $"{_valkey.Hostname}:{_valkey.GetMappedPublicPort(6379)},abortConnect=false";

    // CREATE DATABASE ... TEMPLATE needs the template to have no other session; one copy at a time.
    private readonly SemaphoreSlim _copying = new(1, 1);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), _valkey.StartAsync());
        await Execute(_container.GetConnectionString(), $"CREATE DATABASE {Template}");
        var template = new TestDatabase(At(Template), Valkey);
        await using (var host = TestApp.Start(template))
        {
            await host.Services.GetRequiredService<DatabaseMigrator>().Run(template.Owner, CancellationToken.None);
        }

        NpgsqlConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    /// <summary>A fresh, migrated database: the module roles, schemas and grants of a deploy, and no rows.</summary>
    public async Task<TestDatabase> NewDatabase()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await _copying.WaitAsync();
        try
        {
            await Execute(_container.GetConnectionString(), $"CREATE DATABASE {name} TEMPLATE {Template}");
        }
        finally
        {
            _copying.Release();
        }

        // TEMPORARY is a right on the database itself, which a copy does not inherit from its template.
        await Execute(_container.GetConnectionString(), $"REVOKE TEMPORARY ON DATABASE {name} FROM PUBLIC");
        return new TestDatabase(At(name), Valkey);
    }

    private string At(string database) => new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
    {
        Database = database,
        // Hosts come and go with every test: let their idle connections go quickly.
        ConnectionIdleLifetime = 5,
        ConnectionPruningInterval = 1,
    }.ConnectionString;

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>One test's database: the owner's connection, each module role's, and the shared Valkey.</summary>
public sealed class TestDatabase(string owner, string valkey)
{
    public string Valkey { get; } = valkey;

    /// <summary>Every module with a schema, as its connection string name and its role's name prefix.</summary>
    public static readonly string[] Modules = ["Accounts", "Relationships", "Posts", "Timeline", "Recommendations"];

    public string Owner { get; } = owner;

    /// <summary>The module's own role, which reaches only the module's schema.</summary>
    public string For(string module) => new NpgsqlConnectionStringBuilder(Owner)
    {
        Username = module.ToLowerInvariant() + "_app",
        Password = module.ToLowerInvariant() + "_test",
    }.ConnectionString;

    /// <summary>Runs SQL as the owner, for what a test sets up or checks behind the API's back.</summary>
    public async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>One value, read as the owner.</summary>
    public async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }
}
