using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Notifications;

/// <summary>Notifications' own schema: what it tells people, its own copy of blocks, and its inbox.</summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<BlockedActor> BlockedActors => Set<BlockedActor>();

    public DbSet<FollowerCount> FollowerCounts => Set<FollowerCount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapOutboxAndInbox();

        modelBuilder.Entity<Notification>(notification =>
        {
            notification.ToTable("notifications");
            notification.HasKey(n => n.Id);
            notification.Property(n => n.Kind).HasMaxLength(30);
            // A recipient's newest first, a page at a time; ids are UUIDv7, so id order is time order.
            notification.HasIndex(n => new { n.RecipientId, n.Id }).IsDescending(false, true).HasDatabaseName("notifications_by_recipient");
            // One row per recipient, kind and hour (s3-l13): the follows of an hour are counted into it.
            notification.HasIndex(n => new { n.RecipientId, n.Kind, n.WindowStart }).IsUnique().HasDatabaseName("notifications_one_per_hour");
            notification.Property(n => n.Count).HasDefaultValue(1);
            // The hourly summaries still to send.
            notification.HasIndex(n => n.WindowStart).HasFilter("summary_due AND summarized_at IS NULL").HasDatabaseName("notifications_summaries_due");
        });

        modelBuilder.Entity<FollowerCount>(count =>
        {
            count.ToTable("follower_counts");
            count.HasKey(c => c.AccountId);
            count.Property(c => c.AccountId).ValueGeneratedNever();
        });

        modelBuilder.Entity<BlockedActor>(blocked =>
        {
            blocked.ToTable("blocked_actors");
            blocked.HasKey(b => new { b.BlockerId, b.BlockedId });
            blocked.Property(b => b.V).HasColumnName("v");
            blocked.Property(b => b.On).HasColumnName("on");
        });
    }
}

/// <summary>Something an account is told: for now, that someone followed it.</summary>
internal sealed class Notification
{
    public Guid Id { get; set; }

    public Guid RecipientId { get; set; }

    public required string Kind { get; set; }

    public Guid ActorId { get; set; }

    public Guid? SubjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>The hour this row gathers (s3-l13): every new follower of that hour is counted here.</summary>
    public DateTimeOffset WindowStart { get; set; }

    public int Count { get; set; } = 1;

    /// <summary>The hour's first three actors; ActorId is the first.</summary>
    public Guid[] Actors { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The recipient is a celebrity: the hour is sent as one summary once it is over, not follow by follow.</summary>
    public bool SummaryDue { get; set; }

    public DateTimeOffset? SummarizedAt { get; set; }
}

/// <summary>An account's latest follower count, from Relationships' FollowerCountChanged: who is a celebrity.</summary>
internal sealed class FollowerCount
{
    public Guid AccountId { get; set; }

    public long Followers { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// Notifications' own copy of one block, from Relationships' block events, with the pair's version. An
/// unblock turns it off; the row is kept 30 days, longer than any redelivery, so a late FollowStarted
/// older than the block is still recognised.
/// </summary>
internal sealed class BlockedActor
{
    public Guid BlockerId { get; set; }

    public Guid BlockedId { get; set; }

    public long V { get; set; }

    public bool On { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}

internal static class NotificationsDbOptions
{
    public static DbContextOptionsBuilder<NotificationsDbContext> Configure(DbContextOptionsBuilder<NotificationsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DatabaseMigrator.MigrationsHistory, NotificationsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(NotificationsDbOptions.Configure(new DbContextOptionsBuilder<NotificationsDbContext>(), "Host=localhost").Options);
}

internal sealed class NotificationsMigrator : IModuleMigrator
{
    public string Module => "Notifications";

    public string Schema => NotificationsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var db = new NotificationsDbContext(NotificationsDbOptions.Configure(new DbContextOptionsBuilder<NotificationsDbContext>(), ownerConnectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
