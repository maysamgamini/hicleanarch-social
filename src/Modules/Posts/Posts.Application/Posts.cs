using Microsoft.Extensions.DependencyInjection;

using Posts.Contracts;
using Posts.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Posts.Application;

/// <summary>The module's name, which keys its transaction scope and its outbox.</summary>
public static class ModuleName
{
    public const string Value = "Posts";
}

/// <summary>Where posts are kept.</summary>
public interface IPostRepository
{
    Task Add(Post post, CancellationToken cancellationToken);

    Task<Post?> Find(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes the changes made to posts found in this scope.</summary>
    Task Save(CancellationToken cancellationToken);

    Task<IReadOnlyList<PostSummary>> Active(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<IReadOnlyList<PostSummary>> RecentByAuthors(IReadOnlyCollection<Guid> authors, int perAuthor, CancellationToken cancellationToken);
}

/// <summary>
/// A post as its reader sees it. The author of a deleted post sees its tombstone (status DELETED,
/// no text); anyone else gets 404.
/// </summary>
public sealed record PostView(Guid PostId, Guid AuthorId, string? Text, string Status, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt)
{
    public static PostView Of(Post post) => new(
        post.Id, post.AuthorId, post.Status == PostStatus.Active ? post.Text : null,
        post.Status.ToString().ToUpperInvariant(), post.CreatedAt, post.EditedAt, post.DeletedAt);
}

public sealed record PublishPost(Guid AuthorId, string? Text) : ICommand<PostView>;

public sealed record EditPost(Guid PostId, Guid CallerId, string? Text) : ICommand<PostView>;

public sealed record DeletePost(Guid PostId, Guid CallerId) : ICommand<PostView>;

public sealed record GetPost(Guid PostId, Guid CallerId) : IQuery<PostView>;

/// <summary>
/// The post and its PostPublished commit together, in the transaction the idempotent request opened
/// with the key row: once that commits, the post survives any crash and its fanout will happen.
/// </summary>
public sealed class PublishPostHandler(
    IPostRepository posts,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<PublishPost, PostView>
{
    public Task<Result<PostView>> Handle(PublishPost command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var written = Post.Write(command.AuthorId, command.Text, clock.GetUtcNow());
            if (!written.IsSuccess)
            {
                return Result.Failure<PostView>(written.Errors);
            }

            var post = written.Value;
            await posts.Add(post, ct);
            await outbox.Add(new PostPublished(Guid.CreateVersion7(), post.CreatedAt, post.Id, post.AuthorId, post.CreatedAt, post.Text), ct);
            return Result.Success(PostView.Of(post));
        }, cancellationToken);
}

public sealed class EditPostHandler(
    IPostRepository posts,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<EditPost, PostView>
{
    public Task<Result<PostView>> Handle(EditPost command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var post = await posts.Find(command.PostId, ct);
            if (post is null || post.AuthorId != command.CallerId)
            {
                return Result.Failure<PostView>(PostErrors.NotFound);
            }

            var now = clock.GetUtcNow();
            var edited = post.Edit(command.Text, now);
            if (!edited.IsSuccess)
            {
                return Result.Failure<PostView>(edited.Errors);
            }

            await posts.Save(ct);
            await outbox.Add(new PostEdited(Guid.CreateVersion7(), now, post.Id, post.AuthorId, now, post.Text), ct);
            return Result.Success(PostView.Of(post));
        }, cancellationToken);
}

/// <summary>A delete is stored at once and hidden at the next load; deleting again changes nothing.</summary>
public sealed class DeletePostHandler(
    IPostRepository posts,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<DeletePost, PostView>
{
    public Task<Result<PostView>> Handle(DeletePost command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var post = await posts.Find(command.PostId, ct);
            if (post is null || post.AuthorId != command.CallerId)
            {
                return Result.Failure<PostView>(PostErrors.NotFound);
            }

            var now = clock.GetUtcNow();
            if (post.Delete(now))
            {
                await posts.Save(ct);
                await outbox.Add(new PostDeleted(Guid.CreateVersion7(), now, post.Id, post.AuthorId, now), ct);
            }

            return Result.Success(PostView.Of(post));
        }, cancellationToken);
}

/// <summary>The author's own read: it must see the latest write, so it reads the writer.</summary>
[ReadsTheWriter]
public sealed class GetPostHandler(IPostRepository posts) : IQueryHandler<GetPost, PostView>
{
    public async Task<Result<PostView>> Handle(GetPost query, CancellationToken cancellationToken)
    {
        var post = await posts.Find(query.PostId, cancellationToken);
        return post is null || (post.Status == PostStatus.Deleted && post.AuthorId != query.CallerId)
            ? Result.Failure<PostView>(PostErrors.NotFound)
            : Result.Success(PostView.Of(post));
    }
}

/// <summary>Answers other modules: the feed hydrates a page with one call.</summary>
public sealed class GetPostsHandler(IPostRepository posts) : IQueryHandler<GetPosts, IReadOnlyList<PostSummary>>
{
    public async Task<Result<IReadOnlyList<PostSummary>>> Handle(GetPosts query, CancellationToken cancellationToken) =>
        Result.Success(await posts.Active([.. query.PostIds.Distinct()], cancellationToken));
}

public sealed class GetRecentByAuthorsHandler(IPostRepository posts) : IQueryHandler<GetRecentByAuthors, IReadOnlyList<PostSummary>>
{
    public async Task<Result<IReadOnlyList<PostSummary>>> Handle(GetRecentByAuthors query, CancellationToken cancellationToken) =>
        Result.Success(await posts.RecentByAuthors([.. query.AuthorIds.Distinct()], Math.Clamp(query.PerAuthor, 1, 800), cancellationToken));
}

public sealed class GetRecentByAuthorHandler(IPostRepository posts) : IQueryHandler<GetRecentByAuthor, IReadOnlyList<PostSummary>>
{
    public async Task<Result<IReadOnlyList<PostSummary>>> Handle(GetRecentByAuthor query, CancellationToken cancellationToken) =>
        Result.Success(await posts.RecentByAuthors([query.AuthorId], Math.Clamp(query.Limit, 1, 800), cancellationToken));
}

public static class PostsApplication
{
    public static IServiceCollection AddPostsApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<PublishPost, PostView>, PublishPostHandler>()
            .AddScoped<ICommandHandler<EditPost, PostView>, EditPostHandler>()
            .AddScoped<ICommandHandler<DeletePost, PostView>, DeletePostHandler>()
            .AddScoped<IQueryHandler<GetPost, PostView>, GetPostHandler>()
            .AddScoped<IQueryHandler<GetPosts, IReadOnlyList<PostSummary>>, GetPostsHandler>()
            .AddScoped<IQueryHandler<GetRecentByAuthors, IReadOnlyList<PostSummary>>, GetRecentByAuthorsHandler>()
            .AddScoped<IQueryHandler<GetRecentByAuthor, IReadOnlyList<PostSummary>>, GetRecentByAuthorHandler>();
}
