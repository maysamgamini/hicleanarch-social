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

        modelBuilder.Entity<FanoutJobRow>(job =>
        {
            job.ToTable("fanout_jobs");
            // A post has at most two jobs: its fanout, and after PostDeleted its removal (s3-l09).
            job.HasKey(j => new { j.PostId, j.Kind });
            job.Property(j => j.Kind).HasMaxLength(10).HasDefaultValue("ADD");
            job.Property(j => j.Pool).HasMaxLength(10);
            job.Property(j => j.Status).HasMaxLength(10);
            // What a worker of a pool looks for: the oldest job not done.
            job.HasIndex(j => new { j.Pool, j.CreatedAt }).HasFilter("status <> 'DONE'").HasDatabaseName("fanout_jobs_waiting");
        });

        modelBuilder.Entity<RebuildJobRow>(job =>
        {
            job.ToTable("rebuild_jobs");
            job.HasKey(j => j.ReaderId);
            job.HasIndex(j => j.CreatedAt).HasDatabaseName("rebuild_jobs_oldest");
        });
    }
}

/// <summary>A returning reader's timeline to build: one row per reader, so two returns ask once.</summary>
internal sealed class RebuildJobRow
{
    public Guid ReaderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }
}

internal sealed class FanoutProgressRow
{
    public Guid PostId { get; set; }

    public Guid AuthorId { get; set; }

    public DateTimeOffset StoredAt { get; set; }

    public long FollowersReached { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// One post's fanout as a durable job: the next follower to write after (the cursor), the lease of the
/// worker that holds it, and its status (WAITING, RUNNING, DONE).
/// </summary>
internal sealed class FanoutJobRow
{
    public Guid PostId { get; set; }

    /// <summary>ADD (the post's fanout) or REMOVE (its removal after PostDeleted).</summary>
    public string Kind { get; set; } = "ADD";

    public Guid AuthorId { get; set; }

    public long Audience { get; set; }

    public required string Pool { get; set; }

    public required string Status { get; set; }

    public Guid? Cursor { get; set; }

    public bool AuthorDone { get; set; }

    public long FollowersReached { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseUntil { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
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
