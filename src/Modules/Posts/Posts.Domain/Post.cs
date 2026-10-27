using Social.BuildingBlocks.Domain;

namespace Posts.Domain;

public enum PostStatus
{
    Active,
    Deleted,
}

public static class PostErrors
{
    public static readonly Error TextLength = Error.Validation("text_length", "A post has 1 to 500 characters.");
    public static readonly Error NotFound = Error.NotFound("post_not_found", "No post has this id.");
    public static readonly Error Deleted = Error.Conflict("post_deleted", "This post is deleted; it cannot be edited.");
}

/// <summary>
/// A post. Its id is a UUIDv7 made from the moment it was written, so ids sort by creation time to
/// the millisecond and a timeline can take its score from the id. A delete leaves a tombstone: the
/// row stays, marked DELETED, so the author can still see what happened to it.
/// </summary>
public sealed class Post
{
    public const int MaxLength = 500;

    private Post(Guid id, Guid authorId, string text, PostStatus status, DateTimeOffset createdAt, DateTimeOffset? editedAt, DateTimeOffset? deletedAt)
    {
        Id = id;
        AuthorId = authorId;
        Text = text;
        Status = status;
        CreatedAt = createdAt;
        EditedAt = editedAt;
        DeletedAt = deletedAt;
    }

    public Guid Id { get; private set; }

    public Guid AuthorId { get; private set; }

    public string Text { get; private set; }

    public PostStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? EditedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public static Result<Post> Write(Guid authorId, string? text, DateTimeOffset now)
    {
        var checkedText = CheckText(text);
        if (!checkedText.IsSuccess)
        {
            return Result.Failure<Post>(checkedText.Errors);
        }

        // The id carries the creation time; created_at is the same millisecond.
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        return Result.Success(new Post(Guid.CreateVersion7(createdAt), authorId, checkedText.Value, PostStatus.Active, createdAt, null, null));
    }

    public Result<Post> Edit(string? text, DateTimeOffset now)
    {
        if (Status == PostStatus.Deleted)
        {
            return Result.Failure<Post>(PostErrors.Deleted);
        }

        var checkedText = CheckText(text);
        if (!checkedText.IsSuccess)
        {
            return Result.Failure<Post>(checkedText.Errors);
        }

        Text = checkedText.Value;
        EditedAt = now;
        return Result.Success(this);
    }

    /// <summary>True when this call deleted the post; false when it already was.</summary>
    public bool Delete(DateTimeOffset now)
    {
        if (Status == PostStatus.Deleted)
        {
            return false;
        }

        Status = PostStatus.Deleted;
        DeletedAt = now;
        return true;
    }

    private static Result<string> CheckText(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Length > MaxLength
            ? Result.Failure<string>(PostErrors.TextLength)
            : Result.Success(text);
}
