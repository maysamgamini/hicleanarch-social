using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Timeline.Application;

/// <summary>A stored post goes to the existing timelines of its author and of every follower.</summary>
public sealed record FanOut(Guid PostId, Guid AuthorId, DateTimeOffset StoredAt) : ICommand<FanOutResult>;

public sealed record FanOutResult(int FollowersPaged, int TimelinesWritten);

/// <summary>
/// Fanout on write: the author's followers are read 1,000 at a time and each page is written to
/// Valkey before the next is read, so memory stays flat for any audience. Running it twice for the
/// same post changes nothing: the second add finds the member already there.
/// </summary>
public sealed class FanOutHandler(ITimelines timelines, IFollowerPages followers, IFanoutProgress progress, TimeProvider clock)
    : ICommandHandler<FanOut, FanOutResult>
{
    public const int PageSize = 1_000;

    public async Task<Result<FanOutResult>> Handle(FanOut command, CancellationToken cancellationToken)
    {
        var record = await progress.Start(command.PostId, command.AuthorId, command.StoredAt, cancellationToken);
        if (record.FannedOutAt is not null)
        {
            return Result.Success(new FanOutResult((int)record.FollowersReached, 0)); // delivered again after it completed
        }

        // The author reads their own post in their own feed.
        var written = await timelines.AddToExisting([command.AuthorId], command.PostId, cancellationToken);
        var paged = 0;
        Guid? after = null;
        do
        {
            var (page, next) = await followers.Page(command.AuthorId, after, PageSize, cancellationToken);
            paged += page.Count;
            written += await timelines.AddToExisting(page, command.PostId, cancellationToken);
            await progress.Reached(command.PostId, paged, cancellationToken);
            after = next;
        }
        while (after is not null);

        // Fanned out: the last existing follower timeline has the post.
        await progress.Complete(command.PostId, clock.GetUtcNow(), cancellationToken);

        return Result.Success(new FanOutResult(paged, written));
    }
}

public static class TimelineApplication
{
    public static IServiceCollection AddTimelineApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<FanOut, FanOutResult>, FanOutHandler>()
            .AddScoped<IQueryHandler<ReadFeed, FeedPage>, ReadFeedHandler>()
            .AddScoped<ICommandHandler<RemoveAuthor, int>, RemoveAuthorHandler>()
            .AddScoped<ICommandHandler<AddRecentPosts, int>, AddRecentPostsHandler>()
            .AddScoped<IQueryHandler<GetDelivery, Delivery>, GetDeliveryHandler>();
}
