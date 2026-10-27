using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Recommendations.Infrastructure.Persistence;

/// <summary>
/// Recommendations' own PostgreSQL schema. It held the first adapter of the graph (s2-l13); the graph
/// now lives in the graph database, and the schema waits for what must be durable here (s2-l16).
/// </summary>
internal sealed class RecommendationsDbContext(DbContextOptions<RecommendationsDbContext> options) : DbContext(options)
{
    public const string Schema = "recommendations";

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema(Schema);
}

internal static class RecommendationsDbOptions
{
    public static DbContextOptionsBuilder<RecommendationsDbContext> Configure(DbContextOptionsBuilder<RecommendationsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DatabaseMigrator.MigrationsHistory, RecommendationsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<RecommendationsDbContext>
{
    public RecommendationsDbContext CreateDbContext(string[] args) =>
        new(RecommendationsDbOptions.Configure(new DbContextOptionsBuilder<RecommendationsDbContext>(), "Host=localhost").Options);
}

/// <summary>
/// Brings Recommendations' stores up to date in the deploy's migration step: its PostgreSQL schema,
/// and, when a graph database is configured, the graph's setup step (Neo4j's uniqueness constraint).
/// </summary>
internal sealed class RecommendationsMigrator(Neo4j.Driver.IDriver? graph = null) : IModuleMigrator
{
    public string Module => "Recommendations";

    public string Schema => RecommendationsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var db = new RecommendationsDbContext(RecommendationsDbOptions.Configure(new DbContextOptionsBuilder<RecommendationsDbContext>(), ownerConnectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
        if (graph is not null)
        {
            await Graph.Neo4jSetup.Run(graph, cancellationToken);
        }
    }
}
