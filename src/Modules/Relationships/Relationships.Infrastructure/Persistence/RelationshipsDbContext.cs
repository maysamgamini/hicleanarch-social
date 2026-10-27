using Microsoft.EntityFrameworkCore;

using Social.BuildingBlocks.Infrastructure.Events;

namespace Relationships.Infrastructure.Persistence;

/// <summary>
/// The Relationships module's tables. EF Core owns their shape and their migrations; the statements
/// that are the rules (the pair lock, the guarded counter) are SQL, in <see cref="SqlRelationshipStore"/>.
/// </summary>
internal sealed class RelationshipsDbContext(DbContextOptions<RelationshipsDbContext> options) : DbContext(options)
{
    public const string Schema = "relationships";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapOutboxAndInbox();

        modelBuilder.Entity<FollowRow>(follow =>
        {
            follow.ToTable("follows", t => t.HasCheckConstraint("follows_not_self", "follower_id <> followee_id"));
            follow.HasKey(f => new { f.FollowerId, f.FolloweeId });
            follow.Property(f => f.CreatedAt).HasDefaultValueSql("now()");
            // The fanout's question: "the followers of X, a page at a time", an index range scan.
            follow.HasIndex(f => new { f.FolloweeId, f.FollowerId }).HasDatabaseName("follows_by_followee");
        });

        modelBuilder.Entity<BlockRow>(block =>
        {
            block.ToTable("blocks", t => t.HasCheckConstraint("blocks_not_self", "blocker_id <> blocked_id"));
            block.HasKey(b => new { b.BlockerId, b.BlockedId });
            block.Property(b => b.CreatedAt).HasDefaultValueSql("now()");
            // "Who blocked the viewer", for the read filter.
            block.HasIndex(b => b.BlockedId).HasDatabaseName("blocks_by_blocked");
        });

        modelBuilder.Entity<MuteRow>(mute =>
        {
            mute.ToTable("mutes", t => t.HasCheckConstraint("mutes_not_self", "muter_id <> muted_id"));
            mute.HasKey(m => new { m.MuterId, m.MutedId });
            mute.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<PairVersionRow>(pair =>
        {
            pair.ToTable("pair_versions", t => t.HasCheckConstraint("pair_versions_ordered", "low_id < high_id"));
            pair.HasKey(p => new { p.LowId, p.HighId });
        });

        modelBuilder.Entity<CountsRow>(counts =>
        {
            counts.ToTable("relationship_counts");
            counts.HasKey(c => c.AccountId);
        });
    }
}

internal sealed class FollowRow
{
    public Guid FollowerId { get; set; }

    public Guid FolloweeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class BlockRow
{
    public Guid BlockerId { get; set; }

    public Guid BlockedId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class MuteRow
{
    public Guid MuterId { get; set; }

    public Guid MutedId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One row per pair that has ever changed: low_id &lt; high_id in PostgreSQL's uuid order.</summary>
internal sealed class PairVersionRow
{
    public Guid LowId { get; set; }

    public Guid HighId { get; set; }

    public long Version { get; set; }
}

internal sealed class CountsRow
{
    public Guid AccountId { get; set; }

    public long Followers { get; set; }

    public long Following { get; set; }
}
