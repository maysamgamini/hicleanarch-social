using Microsoft.EntityFrameworkCore;

using Posts.Application;
using Posts.Contracts;
using Posts.Domain;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Posts.Infrastructure.Persistence;

/// <summary>
/// Posts from the reader endpoint (ConnectionStrings:PostsRead; without it, the writer's database in
/// read-only sessions), with each post's images' public URLs, read in one more statement per call.
/// </summary>
internal sealed class EfPostReads(ReadOnlyDb<PostsDbContext> reader, IMediaStore store) : IPostReads
{
    private PostsDbContext Db => reader.Context;

    public async Task<IReadOnlyList<PostSummary>> Active(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await WithMedia(await Db.Posts
            .Where(p => ids.Contains(p.Id) && p.Status == PostStatus.Active)
            .Select(p => new PostSummary(p.Id, p.AuthorId, p.Text, p.CreatedAt, p.EditedAt, null))
            .ToListAsync(cancellationToken), cancellationToken);

    // Each author's newest posts, one index range scan per author on posts_by_author, then merged
    // newest first. UUIDv7 ids order by creation time, so the id is the sort key.
    public async Task<IReadOnlyList<PostSummary>> RecentByAuthors(IReadOnlyCollection<Guid> authors, int perAuthor, CancellationToken cancellationToken) =>
        authors.Count == 0
            ? []
            : await WithMedia(await Db.Read("""
                SELECT p.id, p.author_id, p.text, p.created_at, p.edited_at
                FROM unnest(@authors::uuid[]) AS a(id)
                CROSS JOIN LATERAL (
                    SELECT * FROM posts.posts WHERE author_id = a.id AND status = 'ACTIVE' ORDER BY id DESC LIMIT @per
                ) AS p
                ORDER BY p.id DESC
                """,
                r => new PostSummary(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
                    r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4)),
                cancellationToken, ("authors", authors.ToArray()), ("per", perAuthor)), cancellationToken);

    // One index range on (author_id, id): the newest first, from just below the page's cursor.
    public async Task<IReadOnlyList<PostSummary>> RecentByAuthor(Guid author, Guid? before, int limit, CancellationToken cancellationToken) =>
        await WithMedia(await Db.Read($"""
            SELECT id, author_id, text, created_at, edited_at FROM posts.posts
            WHERE author_id = @author AND status = 'ACTIVE'{(before is null ? "" : " AND id < @before")}
            ORDER BY id DESC LIMIT @limit
            """,
            r => new PostSummary(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4)),
            cancellationToken, [("author", author), ("before", before ?? Guid.Empty), ("limit", limit)]), cancellationToken);

    public async Task<IReadOnlyList<AuthorActivity>> MostRecentlyActive(IReadOnlyCollection<Guid> authors, int limit, CancellationToken cancellationToken) =>
        authors.Count == 0
            ? []
            : await Db.Read("""
                SELECT author_id, last_posted_at FROM posts.author_activity
                WHERE author_id = ANY(@authors) ORDER BY last_posted_at DESC, author_id LIMIT @limit
                """, r => new AuthorActivity(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1)),
                cancellationToken, ("authors", authors.ToArray()), ("limit", limit));

    // The images of these posts, in each post's order: one index range on media_by_post.
    private async Task<IReadOnlyList<PostSummary>> WithMedia(List<PostSummary> posts, CancellationToken cancellationToken)
    {
        if (posts.Count == 0)
        {
            return posts;
        }

        var rows = await Db.Read("""
            SELECT post_id, owner_id, id FROM posts.media WHERE post_id = ANY(@posts) ORDER BY post_id, position
            """, r => (Post: r.GetGuid(0), Owner: r.GetGuid(1), Id: r.GetGuid(2)), cancellationToken, ("posts", posts.Select(p => p.PostId).ToArray()));
        if (rows.Count == 0)
        {
            return posts;
        }

        var urls = rows.GroupBy(r => r.Post).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => store.PublicUrl(Media.KeyOf(r.Owner, r.Id)))]);
        return [.. posts.Select(p => urls.TryGetValue(p.PostId, out var u) ? p with { Media = u } : p)];
    }
}
