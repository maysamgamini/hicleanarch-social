using Social.BuildingBlocks.Application;

namespace Posts.Contracts;

/// <summary>A post as a feed shows it. Only active posts are ever summaries.</summary>
public sealed record PostSummary(Guid PostId, Guid AuthorId, string Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt);

/// <summary>The active posts among these ids, in one read; deleted and unknown ids are left out.</summary>
public sealed record GetPosts(IReadOnlyList<Guid> PostIds) : IQuery<IReadOnlyList<PostSummary>>;

/// <summary>Each author's newest active posts, at most <paramref name="PerAuthor"/> each, newest first overall.</summary>
public sealed record GetRecentByAuthors(IReadOnlyList<Guid> AuthorIds, int PerAuthor) : IQuery<IReadOnlyList<PostSummary>>;

/// <summary>One author's newest active posts, newest first.</summary>
public sealed record GetRecentByAuthor(Guid AuthorId, int Limit) : IQuery<IReadOnlyList<PostSummary>>;
