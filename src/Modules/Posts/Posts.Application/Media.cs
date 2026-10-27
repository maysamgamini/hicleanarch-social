using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;
using Posts.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Posts.Application;

/// <summary>Where to send one file, until when, and the headers the request must carry.</summary>
public sealed record UploadTicket(Uri Url, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// The media store: S3 behind CloudFront on AWS, a fake in memory everywhere else. The program never
/// carries the bytes: the client sends them to the upload URL itself.
/// </summary>
public interface IMediaStore
{
    /// <summary>An upload URL for one object of this type and size, valid until <paramref name="expiresAt"/>.</summary>
    Task<UploadTicket> IssueUpload(string objectKey, string contentType, long size, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Whether the store holds the object, of this type and size.</summary>
    Task<bool> Holds(string objectKey, string contentType, long size, CancellationToken cancellationToken);

    /// <summary>Where anyone reads it.</summary>
    string PublicUrl(string objectKey);

    /// <summary>Deletes the object, if it is there.</summary>
    Task Delete(string objectKey, CancellationToken cancellationToken);
}

public interface IMediaRepository
{
    Task Add(Media media, CancellationToken cancellationToken);

    /// <summary>Media in no post, requested before <paramref name="cutoff"/>: at most <paramref name="limit"/>, oldest first.</summary>
    Task<IReadOnlyList<Media>> Unattached(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken);

    /// <summary>Deletes the row, only if it is still in no post. True when deleted.</summary>
    Task<bool> DeleteUnattached(Guid id, CancellationToken cancellationToken);

    Task<Media?> Find(Guid id, CancellationToken cancellationToken);

    /// <summary>PENDING to UPLOADED, as one guarded statement: true only for the call that changed it.</summary>
    Task<bool> MarkUploaded(Guid id, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches these images, in this order, to the post, each only if it is the author's, uploaded and in
    /// no post: one guarded statement per image, so two posts can never take the same one. How many it attached.
    /// </summary>
    Task<int> Attach(IReadOnlyList<Guid> ids, Guid authorId, Guid postId, CancellationToken cancellationToken);

    /// <summary>The object keys of these posts' media, in each post's order.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> KeysOf(IReadOnlyCollection<Guid> postIds, CancellationToken cancellationToken);
}

/// <summary>How long an upload URL lives (Media:UploadFor, 5 minutes).</summary>
public sealed record MediaOptions(TimeSpan UploadFor);

public sealed record MediaView(Guid MediaId, string Status, DateTimeOffset UploadExpiresAt, UploadTicket? Upload, string? Url);

public sealed record RequestUpload(Guid OwnerId, string? ContentType, long Size) : ICommand<MediaView>;

public sealed record ConfirmUpload(Guid MediaId, Guid OwnerId) : ICommand<MediaView>;

/// <summary>A media row, PENDING, and an upload URL for its one object.</summary>
public sealed class RequestUploadHandler(IMediaRepository media, IMediaStore store, MediaOptions options, TimeProvider clock) : ICommandHandler<RequestUpload, MediaView>
{
    public async Task<Result<MediaView>> Handle(RequestUpload command, CancellationToken cancellationToken)
    {
        var requested = Media.Request(command.OwnerId, command.ContentType, command.Size, clock.GetUtcNow(), options.UploadFor);
        if (!requested.IsSuccess)
        {
            return Result.Failure<MediaView>(requested.Errors);
        }

        var item = requested.Value;
        await media.Add(item, cancellationToken);
        var ticket = await store.IssueUpload(item.ObjectKey, item.ContentType, item.Size, item.UploadExpiresAt, cancellationToken);
        return Result.Success(new MediaView(item.Id, "PENDING", item.UploadExpiresAt, ticket, null));
    }
}

/// <summary>The client says the upload is done; the store is asked. Confirming twice changes nothing.</summary>
public sealed class ConfirmUploadHandler(IMediaRepository media, IMediaStore store, TimeProvider clock) : ICommandHandler<ConfirmUpload, MediaView>
{
    public async Task<Result<MediaView>> Handle(ConfirmUpload command, CancellationToken cancellationToken)
    {
        var item = await media.Find(command.MediaId, cancellationToken);
        if (item is null || item.OwnerId != command.OwnerId)
        {
            return Result.Failure<MediaView>(MediaErrors.NotFound);
        }

        if (item.Status == MediaStatus.Pending)
        {
            if (!await store.Holds(item.ObjectKey, item.ContentType, item.Size, cancellationToken))
            {
                return Result.Failure<MediaView>(MediaErrors.NotUploaded);
            }

            // Two confirmations at once both find it pending; only one statement moves it.
            await media.MarkUploaded(item.Id, clock.GetUtcNow(), cancellationToken);
        }

        return Result.Success(new MediaView(item.Id, "UPLOADED", item.UploadExpiresAt, null, store.PublicUrl(item.ObjectKey)));
    }
}

/// <summary>Puts each post's public media URLs on its summary, with one read for all of them.</summary>
public sealed class MediaUrls(IMediaRepository media, IMediaStore store)
{
    public async Task<IReadOnlyList<PostSummary>> On(IReadOnlyList<PostSummary> posts, CancellationToken cancellationToken)
    {
        if (posts.Count == 0)
        {
            return posts;
        }

        var keys = await media.KeysOf([.. posts.Select(p => p.PostId)], cancellationToken);
        return keys.Count == 0
            ? posts
            : [.. posts.Select(p => keys.TryGetValue(p.PostId, out var k) ? p with { Media = [.. k.Select(store.PublicUrl)] } : p)];
    }
}

/// <summary>One cleanup pass: unattached uploads older than a day leave the store and the table. How many.</summary>
public sealed record CleanUpMedia(int Limit = 500) : ICommand<int>;

/// <summary>
/// An image requested or uploaded and never put in a post is kept a day, then deleted: the row, and the
/// object if it was uploaded. The row goes only if it is still in no post, so a post published during
/// the pass keeps its image; the object goes after its row, so no row ever names an object that is gone.
/// </summary>
public sealed class CleanUpMediaHandler(IMediaRepository media, IMediaStore store, TimeProvider clock) : ICommandHandler<CleanUpMedia, int>
{
    public static readonly TimeSpan KeepUnattached = TimeSpan.FromDays(1);

    public async Task<Result<int>> Handle(CleanUpMedia command, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var item in await media.Unattached(clock.GetUtcNow() - KeepUnattached, command.Limit, cancellationToken))
        {
            if (await media.DeleteUnattached(item.Id, cancellationToken))
            {
                await store.Delete(item.ObjectKey, cancellationToken);
                deleted++;
            }
        }

        return Result.Success(deleted);
    }
}

public static class MediaApplication
{
    public static IServiceCollection AddMediaApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<RequestUpload, MediaView>, RequestUploadHandler>()
            .AddScoped<ICommandHandler<ConfirmUpload, MediaView>, ConfirmUploadHandler>()
            .AddScoped<ICommandHandler<CleanUpMedia, int>, CleanUpMediaHandler>()
            .AddScoped<MediaUrls>();
}
