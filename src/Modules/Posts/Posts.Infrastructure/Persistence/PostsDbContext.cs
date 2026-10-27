using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Posts.Domain;

using Social.BuildingBlocks.Infrastructure.Events;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Posts.Infrastructure.Persistence;

/// <summary>The Posts module's view of the database: its own schema, and nothing else.</summary>
internal sealed class PostsDbContext(DbContextOptions<PostsDbContext> options) : DbContext(options)
{
    public const string Schema = "posts";

    public DbSet<Post> Posts => Set<Post>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapIdempotencyKeys();
        modelBuilder.MapOutboxAndInbox();

        modelBuilder.Entity<Post>(post =>
        {
            post.ToTable("posts");
            post.HasKey(p => p.Id);
            post.Property(p => p.Id).ValueGeneratedNever();
            post.Property(p => p.Text).HasMaxLength(Post.MaxLength);
            post.Property(p => p.Status)
                .HasConversion(s => s.ToString().ToUpperInvariant(), s => Enum.Parse<PostStatus>(s, true))
                .HasMaxLength(20);
            // "An author's newest posts": a timeline's build reads it for every followed account.
            post.HasIndex(p => new { p.AuthorId, p.Id }).IsDescending(false, true).HasDatabaseName("posts_by_author");
        });

        // Each author's last post time, one row per author: which followed accounts posted most recently
        // is a read of a few thousand primary keys, not of their posts.
        modelBuilder.Entity<AuthorActivityRow>(activity =>
        {
            activity.ToTable("author_activity");
            activity.HasKey(a => a.AuthorId);
        });
    }
}

internal sealed class AuthorActivityRow
{
    public Guid AuthorId { get; set; }

    public DateTimeOffset LastPostedAt { get; set; }
}

internal static class PostsDbOptions
{
    public static DbContextOptionsBuilder<PostsDbContext> Configure(DbContextOptionsBuilder<PostsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(DatabaseMigrator.MigrationsHistory, PostsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PostsDbContext>
{
    public PostsDbContext CreateDbContext(string[] args) =>
        new(PostsDbOptions.Configure(new DbContextOptionsBuilder<PostsDbContext>(), "Host=localhost").Options);
}

/// <summary>Brings the Posts schema up to date. Run by the host's <c>migrate</c> command, never at start-up.</summary>
internal sealed class PostsMigrator : IModuleMigrator
{
    public string Module => "Posts";

    public string Schema => PostsDbContext.Schema;

    public async Task Migrate(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var db = new PostsDbContext(PostsDbOptions.Configure(new DbContextOptionsBuilder<PostsDbContext>(), ownerConnectionString).Options);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
