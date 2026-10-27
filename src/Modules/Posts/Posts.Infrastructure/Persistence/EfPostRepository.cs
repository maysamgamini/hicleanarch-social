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
    }

    public Task<Post?> Find(Guid id, CancellationToken cancellationToken) =>
        db.Posts.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task Save(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<PostSummary>> Active(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await db.Posts.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.Status == PostStatus.Active)
            .Select(p => new PostSummary(p.Id, p.AuthorId, p.Text, p.CreatedAt, p.EditedAt))
            .ToListAsync(cancellationToken);

    // Each author's newest posts, one index range scan per author on posts_by_author, then merged
    // newest first. UUIDv7 ids order by creation time, so the id is the sort key.
    public async Task<IReadOnlyList<PostSummary>> RecentByAuthors(IReadOnlyCollection<Guid> authors, int perAuthor, CancellationToken cancellationToken) =>
        authors.Count == 0
            ? []
            : await db.Read("""
                SELECT p.id, p.author_id, p.text, p.created_at, p.edited_at
                FROM unnest(@authors::uuid[]) AS a(id)
                CROSS JOIN LATERAL (
                    SELECT * FROM posts.posts WHERE author_id = a.id AND status = 'ACTIVE' ORDER BY id DESC LIMIT @per
                ) AS p
                ORDER BY p.id DESC
                """,
                r => new PostSummary(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
                    r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4)),
                cancellationToken, ("authors", authors.ToArray()), ("per", perAuthor));

    // One index range on (author_id, id): the newest first, from just below the page's cursor.
    public async Task<IReadOnlyList<PostSummary>> RecentByAuthor(Guid author, Guid? before, int limit, CancellationToken cancellationToken) =>
        await db.Read($"""
            SELECT id, author_id, text, created_at, edited_at FROM posts.posts
            WHERE author_id = @author AND status = 'ACTIVE'{(before is null ? "" : " AND id < @before")}
            ORDER BY id DESC LIMIT @limit
            """,
            r => new PostSummary(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4)),
            cancellationToken, [("author", author), ("before", before ?? Guid.Empty), ("limit", limit)]);
}
