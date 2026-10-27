using Social.BuildingBlocks.Domain;

namespace Posts.Domain;

public enum MediaStatus
{
    Pending,
    Uploaded,
}

public static class MediaErrors
{
    public static readonly Error Type = Error.Validation("media_type", "Media is a JPEG, PNG, WebP or GIF image.");
    public static readonly Error Size = Error.Validation("media_size", "An image is 1 byte to 10 MB.");
    public static readonly Error NotFound = Error.NotFound("media_not_found", "No media of yours has this id.");
    public static readonly Error NotUploaded = Error.Conflict("media_not_uploaded", "The upload has not arrived: put the file at its upload URL, then confirm.");
    public static readonly Error Unusable = Error.Validation("media_unusable", "A post takes up to 4 of your own uploaded images, each in one post.");
}

/// <summary>
/// One image a person uploads straight to the media store with an upload URL valid for 5 minutes,
/// then confirms. Only an uploaded image of its owner's can go into a post, and into one post only.
/// </summary>
public sealed class Media
{
    public const long MaxSize = 10 * 1024 * 1024;

    public const int MaxPerPost = 4;

    public static readonly TimeSpan UploadFor = TimeSpan.FromMinutes(5);

    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { "image/jpeg", "image/png", "image/webp", "image/gif" };

    private Media(Guid id, Guid ownerId, string contentType, long size, MediaStatus status, DateTimeOffset createdAt, DateTimeOffset uploadExpiresAt)
    {
        Id = id;
        OwnerId = ownerId;
        ContentType = contentType;
        Size = size;
        Status = status;
        CreatedAt = createdAt;
        UploadExpiresAt = uploadExpiresAt;
    }

    public Guid Id { get; private set; }

    public Guid OwnerId { get; private set; }

    public string ContentType { get; private set; }

    public long Size { get; private set; }

    public MediaStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UploadExpiresAt { get; private set; }

    public DateTimeOffset? UploadedAt { get; private set; }

    public Guid? PostId { get; private set; }

    public int? Position { get; private set; }

    /// <summary>Where the store keeps it: one prefix per owner.</summary>
    public string ObjectKey => KeyOf(OwnerId, Id);

    public static string KeyOf(Guid ownerId, Guid mediaId) => $"media/{ownerId:N}/{mediaId:N}";

    public static Result<Media> Request(Guid ownerId, string? contentType, long size, DateTimeOffset now, TimeSpan? uploadFor = null)
    {
        if (contentType is null || !Types.Contains(contentType))
        {
            return Result.Failure<Media>(MediaErrors.Type);
        }

        if (size is < 1 or > MaxSize)
        {
            return Result.Failure<Media>(MediaErrors.Size);
        }

        return Result.Success(new Media(Guid.CreateVersion7(now), ownerId, contentType, size, MediaStatus.Pending, now, now + (uploadFor ?? UploadFor)));
    }

    /// <summary>
    /// What a post may take: its author's own image, uploaded, in no other post. The repository applies
    /// it as one guarded statement per image, so two posts at once can never both take one.
    /// </summary>
    public bool UsableBy(Guid author) => OwnerId == author && Status == MediaStatus.Uploaded && PostId is null;
}
