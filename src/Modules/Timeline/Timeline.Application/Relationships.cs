using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

using Timeline.Domain;

namespace Timeline.Application;

/// <summary>
/// Takes an author's posts out of an owner's timeline: best effort, after an unfollow or a block. The
/// read filter already hides them; this only keeps the timeline from carrying dead entries.
/// </summary>
public sealed record RemoveAuthor(Guid OwnerId, Guid AuthorId) : ICommand<int>;

/// <summary>After a new follow, the followee's newest posts join the follower's timeline, if it exists.</summary>
public sealed record AddRecentPosts(Guid OwnerId, Guid AuthorId) : ICommand<int>;

public sealed class RemoveAuthorHandler(ITimelines timelines, IPostSummaries posts) : ICommandHandler<RemoveAuthor, int>
{
    public async Task<Result<int>> Handle(RemoveAuthor command, CancellationToken cancellationToken)
    {
        if (!await timelines.Exists(command.OwnerId, cancellationToken))
        {
            return Result.Success(0);
        }

        var theirs = await posts.RecentByAuthors([command.AuthorId], TimelineRules.MaxEntries, cancellationToken);
        await timelines.Remove(command.OwnerId, [.. theirs.Select(p => p.PostId)], cancellationToken);
        return Result.Success(theirs.Count);
    }
}

public sealed class AddRecentPostsHandler(ITimelines timelines, IPostSummaries posts, IPullRegistry registry, TimeProvider clock) : ICommandHandler<AddRecentPosts, int>
{
    public const int Recent = 20;

    public async Task<Result<int>> Handle(AddRecentPosts command, CancellationToken cancellationToken)
    {
        if (!await timelines.Exists(command.OwnerId, cancellationToken))
        {
            return Result.Success(0); // the timeline's build will bring them in
        }

        // A pulled author's posts are read at read time: a new follower's timeline takes none of them.
        if (await registry.Find(command.AuthorId, cancellationToken) is { } pulled && !pulled.Pushed(clock.GetUtcNow()))
        {
            return Result.Success(0);
        }

        var recent = await posts.RecentByAuthors([command.AuthorId], Recent, cancellationToken);
        await timelines.Fill(command.OwnerId, [.. recent.Select(p => p.PostId)], cancellationToken);
        return Result.Success(recent.Count);
    }
}
