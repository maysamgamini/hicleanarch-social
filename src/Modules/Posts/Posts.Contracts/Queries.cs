using Social.BuildingBlocks.Application;

namespace Posts.Contracts;

/// <summary>A post as a feed shows it, with the public URLs of its images (s3-l10). Only active posts are ever summaries.</summary>
public sealed record PostSummary(Guid PostId, Guid AuthorId, string Text, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, IReadOnlyList<string>? Media = null);

/// <summary>The active posts among these ids, in one read; deleted and unknown ids are left out.</summary>
public sealed record GetPosts(IReadOnlyList<Guid> PostIds) : IQuery<IReadOnlyList<PostSummary>>;

/// <summary>Each author's newest active posts, at most <paramref name="PerAuthor"/> each, newest first overall.</summary>
public sealed record GetRecentByAuthors(IReadOnlyList<Guid> AuthorIds, int PerAuthor) : IQuery<IReadOnlyList<PostSummary>>;

/// <summary>One author's newest active posts, newest first; with <paramref name="Before"/>, only posts older than that one (by id, which is time order).</summary>
public sealed record GetRecentByAuthor(Guid AuthorId, int Limit, Guid? Before = null) : IQuery<IReadOnlyList<PostSummary>>;

/// <summary>An author's last post time, as of the last post they wrote (a deletion does not move it back).</summary>
public sealed record AuthorActivity(Guid AuthorId, DateTimeOffset LastPostedAt);

/// <summary>Among these authors, at most <paramref name="Limit"/> that posted most recently, newest first.</summary>
public sealed record GetRecentlyActiveAuthors(IReadOnlyList<Guid> AuthorIds, int Limit) : IQuery<IReadOnlyList<AuthorActivity>>;
