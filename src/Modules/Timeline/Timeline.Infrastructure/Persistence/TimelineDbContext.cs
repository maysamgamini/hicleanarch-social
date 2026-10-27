using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Timeline.Infrastructure.Persistence;

/// <summary>Timeline's own PostgreSQL schema: what it records about each post's fanout. Timelines stay in Valkey.</summary>
internal sealed class TimelineDbContext(DbContextOptions<TimelineDbContext> options) : DbContext(options)
{
    public const string Schema = "timeline";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<FanoutProgressRow>(progress =>
        {
            progress.ToTable("fanout_progress");
            progress.HasKey(p => p.PostId);
            // Stored but not yet fanned out: what the s2-l19 gauge counts.
            progress.HasIndex(p => p.StoredAt).HasFilter("completed_at IS NULL").HasDatabaseName("fanout_progress_open");
        });
    }
}

internal sealed class FanoutProgressRow
{
    public Guid PostId { get; set; }

    public Guid AuthorId { get; set; }

    public DateTimeOffset StoredAt { get; set; }

    public long FollowersReached { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

internal static class TimelineDbOptions
{
    public static DbContextOptionsBuilder<TimelineDbContext> Configure(DbContextOptionsBuilder<TimelineDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DatabaseMigrator.MigrationsHistory, TimelineDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TimelineDbContext>
{
    public TimelineDbContext CreateDbContext(string[] args) =>
        new(TimelineDbOptions.Configure(new DbContextOptionsBuilder<TimelineDbContext>(), "Host=localhost").Options);
}

internal sealed class TimelineMigrator : IModuleMigrator
{
    public string Module => "Timeline";

    public string Schema => TimelineDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var db = new TimelineDbContext(TimelineDbOptions.Configure(new DbContextOptionsBuilder<TimelineDbContext>(), ownerConnectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
