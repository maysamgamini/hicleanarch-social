using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

using Timeline.Domain;

namespace Timeline.Application;

/// <summary>Removes, in the background, the posts a page found deleted from that reader's timeline.</summary>
public interface IReadRepair
{
    void Remove(Guid reader, IReadOnlyList<Guid> postIds);
}

/// <summary>A post was deleted: take it out of the timelines it was written into, best-effort.</summary>
public sealed record RemoveDeletedPost(Guid PostId, Guid AuthorId) : ICommand<bool>;

/// <summary>
/// PostDeleted becomes a removal job, run like a fanout (page by page, through the job table), so the
/// timelines let the post go without waiting for their readers. Best-effort: the read filter already
/// drops a deleted post at hydration, and read repair removes what a removal missed. A pulled
/// author's post was only in the author's own timeline. True when a job was made.
/// </summary>
public sealed class RemoveDeletedPostHandler(IFanoutJobs jobs, IAudience audience, IPullRegistry registry, ITimelines timelines, TimeProvider clock)
    : ICommandHandler<RemoveDeletedPost, bool>
{
    public async Task<Result<bool>> Handle(RemoveDeletedPost command, CancellationToken cancellationToken)
    {
        if (await registry.Find(command.AuthorId, cancellationToken) is { } pulled && !pulled.Pushed(clock.GetUtcNow()))
        {
            await timelines.Remove(command.AuthorId, [command.PostId], cancellationToken);
            return Result.Success(false);
        }

        var size = await audience.Followers(command.AuthorId, cancellationToken);
        await jobs.Create(command.PostId, command.AuthorId, size, FanoutPools.For(size), cancellationToken, FanoutKind.Remove);
        return Result.Success(true);
    }
}
