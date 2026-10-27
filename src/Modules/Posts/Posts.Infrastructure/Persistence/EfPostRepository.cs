using Microsoft.EntityFrameworkCore;

using Posts.Application;
using Posts.Contracts;
using Posts.Domain;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Posts.Infrastructure.Persistence;

internal sealed class EfPostRepository(PostsDbContext db) : IPostRepository
{
    public async Task Add(Post post, CancellationToken cancellationToken)
    {
        db.Posts.Add(post);
        await db.SaveChangesAsync(cancellationToken);
        // In the post's transaction: the author's own row, so never a hot one for anyone else.
        await db.Execute("""
            INSERT INTO posts.author_activity (author_id, last_posted_at) VALUES (@author, @at)
            ON CONFLICT (author_id) DO UPDATE SET last_posted_at = GREATEST(posts.author_activity.last_posted_at, excluded.last_posted_at)
            """, cancellationToken, ("author", post.AuthorId), ("at", post.CreatedAt));
    }

    public Task<Post?> Find(Guid id, CancellationToken cancellationToken) =>
        db.Posts.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task Save(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
