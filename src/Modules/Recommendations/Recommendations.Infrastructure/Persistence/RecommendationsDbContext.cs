using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Recommendations.Infrastructure.Persistence;

/// <summary>
/// The first home of the derived graph: tables in Recommendations' own schema, fed only by events.
/// Nothing here is a source of truth; all of it can be rebuilt from Relationships and Accounts.
/// </summary>
internal sealed class RecommendationsDbContext(DbContextOptions<RecommendationsDbContext> options) : DbContext(options)
{
    public const string Schema = "recommendations";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<AccountRow>(account =>
        {
            account.ToTable("accounts");
            account.HasKey(a => a.Id);
            account.Property(a => a.Handle).HasMaxLength(30);
        });

        modelBuilder.Entity<FollowEdgeRow>(edge =>
        {
            edge.ToTable("follow_edges");
            edge.HasKey(e => new { e.FollowerId, e.FolloweeId });
            edge.Property(e => e.V).HasColumnName("v");
            edge.Property(e => e.On).HasColumnName("on");
            // An edge names two accounts of the graph: one delivered before its account fails and is retried.
            edge.HasOne<AccountRow>().WithMany().HasForeignKey(e => e.FollowerId).OnDelete(DeleteBehavior.Restrict);
            edge.HasOne<AccountRow>().WithMany().HasForeignKey(e => e.FolloweeId).OnDelete(DeleteBehavior.Restrict);
            // The first hop: a reader's newest follows.
            edge.HasIndex(e => new { e.FollowerId, e.Since }).IsDescending(false, true).HasFilter("\"on\"").HasDatabaseName("follow_edges_newest");
        });

        modelBuilder.Entity<HiddenEdgeRow>(edge =>
        {
            edge.ToTable("hidden_edges");
            edge.HasKey(e => new { e.OwnerId, e.TargetId, e.Kind });
            edge.Property(e => e.Kind).HasMaxLength(10);
            edge.Property(e => e.V).HasColumnName("v");
            edge.Property(e => e.On).HasColumnName("on");
            edge.HasIndex(e => new { e.TargetId, e.OwnerId }).HasDatabaseName("hidden_edges_by_target");
        });
    }
}

internal sealed class AccountRow
{
    public Guid Id { get; set; }

    public required string Handle { get; set; }

    public bool Active { get; set; }

    public long Followers { get; set; }

    public long Following { get; set; }
}

internal sealed class FollowEdgeRow
{
    public Guid FollowerId { get; set; }

    public Guid FolloweeId { get; set; }

    public long V { get; set; }

    public bool On { get; set; }

    public DateTimeOffset Since { get; set; }
}

/// <summary>A block or a mute, one direction each.</summary>
internal sealed class HiddenEdgeRow
{
    public Guid OwnerId { get; set; }

    public Guid TargetId { get; set; }

    public required string Kind { get; set; }

    public long V { get; set; }

    public bool On { get; set; }

    public DateTimeOffset Since { get; set; }
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
