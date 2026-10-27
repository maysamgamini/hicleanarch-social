using Microsoft.EntityFrameworkCore;

using Posts.Application;
using Posts.Domain;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Posts.Infrastructure.Persistence;

internal sealed class EfMediaRepository(PostsDbContext db) : IMediaRepository
{
    public async Task Add(Media media, CancellationToken cancellationToken)
    {
        db.Set<Media>().Add(media);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Media>> Unattached(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken) =>
        await db.Set<Media>().AsNoTracking().Where(m => m.PostId == null && m.CreatedAt < cutoff)
            .OrderBy(m => m.CreatedAt).Take(limit).ToListAsync(cancellationToken);

    public async Task<bool> DeleteUnattached(Guid id, CancellationToken cancellationToken) =>
        await db.Execute("DELETE FROM posts.media WHERE id = @id AND post_id IS NULL", cancellationToken, ("id", id)) == 1;

    public Task<Media?> Find(Guid id, CancellationToken cancellationToken) =>
        db.Set<Media>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public async Task<bool> MarkUploaded(Guid id, DateTimeOffset at, CancellationToken cancellationToken) =>
        await db.Execute("UPDATE posts.media SET status = 'UPLOADED', uploaded_at = @at WHERE id = @id AND status = 'PENDING'",
            cancellationToken, ("id", id), ("at", at)) == 1;

    public async Task<int> Attach(IReadOnlyList<Guid> ids, Guid authorId, Guid postId, CancellationToken cancellationToken)
    {
        var attached = 0;
        for (var i = 0; i < ids.Count; i++)
        {
            attached += await db.Execute("""
                UPDATE posts.media SET post_id = @post, position = @position
                WHERE id = @id AND owner_id = @author AND status = 'UPLOADED' AND post_id IS NULL
                """, cancellationToken, ("post", postId), ("position", i), ("id", ids[i]), ("author", authorId));
        }

        return attached;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> KeysOf(IReadOnlyCollection<Guid> postIds, CancellationToken cancellationToken)
    {
        var rows = await db.Read("""
            SELECT post_id, owner_id, id FROM posts.media WHERE post_id = ANY(@posts) ORDER BY post_id, position
            """, r => (Post: r.GetGuid(0), Owner: r.GetGuid(1), Id: r.GetGuid(2)), cancellationToken, ("posts", postIds.ToArray()));
        return rows.GroupBy(r => r.Post).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => Media.KeyOf(r.Owner, r.Id))]);
    }
}
