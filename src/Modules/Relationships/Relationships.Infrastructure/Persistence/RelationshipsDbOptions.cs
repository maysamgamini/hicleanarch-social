using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Relationships.Infrastructure.Persistence;

internal static class RelationshipsDbOptions
{
    public static DbContextOptionsBuilder<RelationshipsDbContext> Configure(DbContextOptionsBuilder<RelationshipsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DatabaseMigrator.MigrationsHistory, RelationshipsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<RelationshipsDbContext>
{
    public RelationshipsDbContext CreateDbContext(string[] args) =>
        new(RelationshipsDbOptions.Configure(new DbContextOptionsBuilder<RelationshipsDbContext>(), "Host=localhost").Options);
}

/// <summary>Brings the Relationships schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class RelationshipsMigrator : IModuleMigrator
{
    public string Module => "Relationships";

    public string Schema => RelationshipsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var db = new RelationshipsDbContext(RelationshipsDbOptions.Configure(new DbContextOptionsBuilder<RelationshipsDbContext>(), ownerConnectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
