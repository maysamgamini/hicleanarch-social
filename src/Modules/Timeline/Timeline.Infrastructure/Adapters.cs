using Posts.Contracts;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;

using Timeline.Application;

namespace Timeline.Infrastructure;

/// <summary>Timeline's IFollowerPages answered in process by Relationships' GetFollowersPage query.</summary>
internal sealed class InProcessFollowerPages(IDispatcher dispatcher) : IFollowerPages
{
    public async Task<(IReadOnlyList<Guid> Followers, Guid? Next)> Page(Guid author, Guid? after, int limit, CancellationToken cancellationToken)
    {
        var page = await dispatcher.Query<GetFollowersPage, FollowersPage>(new GetFollowersPage(author, after, limit), cancellationToken);
        return page.IsSuccess
            ? (page.Value.Followers, page.Value.Next)
            : throw new InvalidOperationException($"Relationships refused a followers page: {page.Errors[0].Code}");
    }
}

/// <summary>Turns Posts' PostPublished into Timeline's own command; the use case never sees Posts' types.</summary>
internal sealed class FanOutOnPostPublished(IDispatcher dispatcher) : IIntegrationEventHandler<PostPublished>
{
    public async Task Handle(PostPublished integrationEvent, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Send<FanOut, FanOutResult>(new FanOut(integrationEvent.PostId, integrationEvent.AuthorId), cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Fanout of {integrationEvent.PostId} failed: {result.Errors[0].Code}");
        }
    }
}
