using Accounts.Contracts;

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
        var result = await dispatcher.Send<FanOut, FanOutResult>(new FanOut(integrationEvent.PostId, integrationEvent.AuthorId, integrationEvent.CreatedAt), cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Fanout of {integrationEvent.PostId} failed: {result.Errors[0].Code}");
        }
    }
}

/// <summary>Timeline's IPostSummaries answered in process by Posts' queries, one batch per call.</summary>
internal sealed class InProcessPostSummaries(IDispatcher dispatcher) : IPostSummaries
{
    public async Task<IReadOnlyList<PostCard>> Get(IReadOnlyList<Guid> postIds, CancellationToken cancellationToken) =>
        Cards(await dispatcher.Query<GetPosts, IReadOnlyList<PostSummary>>(new GetPosts(postIds), cancellationToken));

    public async Task<IReadOnlyList<PostCard>> RecentByAuthors(IReadOnlyList<Guid> authors, int perAuthor, CancellationToken cancellationToken) =>
        Cards(await dispatcher.Query<GetRecentByAuthors, IReadOnlyList<PostSummary>>(new GetRecentByAuthors(authors, perAuthor), cancellationToken));

    private static IReadOnlyList<PostCard> Cards(Social.BuildingBlocks.Domain.Result<IReadOnlyList<PostSummary>> result) =>
        result.IsSuccess
            ? [.. result.Value.Select(p => new PostCard(p.PostId, p.AuthorId, p.Text, p.CreatedAt, p.EditedAt))]
            : throw new InvalidOperationException($"Posts refused a read: {result.Errors[0].Code}");
}

/// <summary>Timeline's IProfiles answered in process by Accounts' GetProfiles, one batch per call.</summary>
internal sealed class InProcessProfiles(IDispatcher dispatcher) : IProfiles
{
    public async Task<IReadOnlyList<AuthorCard>> Get(IReadOnlyList<Guid> accountIds, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Query<GetProfiles, IReadOnlyList<Profile>>(new GetProfiles(accountIds), cancellationToken);
        return result.IsSuccess
            ? [.. result.Value.Select(p => new AuthorCard(p.AccountId, p.Handle, p.DisplayName, p.Active))]
            : throw new InvalidOperationException($"Accounts refused a read: {result.Errors[0].Code}");
    }
}

/// <summary>Timeline's IFollowing answered in process by Relationships' GetFollowing.</summary>
internal sealed class InProcessFollowing(IDispatcher dispatcher) : IFollowing
{
    public async Task<IReadOnlyList<Guid>> Of(Guid reader, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Query<GetFollowing, IReadOnlyList<Guid>>(new GetFollowing(reader), cancellationToken);
        return result.IsSuccess ? result.Value : throw new InvalidOperationException($"Relationships refused a read: {result.Errors[0].Code}");
    }
}

/// <summary>Timeline's IHiddenAuthors answered in process by Relationships' GetHiddenAuthors, once per page.</summary>
internal sealed class InProcessHiddenAuthors(IDispatcher dispatcher) : IHiddenAuthors
{
    public async Task<IReadOnlyCollection<Guid>> Of(Guid reader, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Query<GetHiddenAuthors, HiddenAuthors>(new GetHiddenAuthors(reader), cancellationToken);
        return result.IsSuccess ? result.Value.AccountIds : throw new InvalidOperationException($"Relationships refused a read: {result.Errors[0].Code}");
    }
}

/// <summary>Relationship events, turned into Timeline's own commands: best-effort upkeep of the timelines.</summary>
internal sealed class TimelineUpkeep(IDispatcher dispatcher) :
    IIntegrationEventHandler<AccountBlocked>, IIntegrationEventHandler<FollowEnded>, IIntegrationEventHandler<FollowStarted>
{
    public async Task Handle(AccountBlocked integrationEvent, CancellationToken cancellationToken)
    {
        await Send(new RemoveAuthor(integrationEvent.BlockerId, integrationEvent.BlockedId), cancellationToken);
        await Send(new RemoveAuthor(integrationEvent.BlockedId, integrationEvent.BlockerId), cancellationToken);
    }

    public Task Handle(FollowEnded integrationEvent, CancellationToken cancellationToken) =>
        Send(new RemoveAuthor(integrationEvent.FollowerId, integrationEvent.FolloweeId), cancellationToken);

    public async Task Handle(FollowStarted integrationEvent, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Send<AddRecentPosts, int>(new AddRecentPosts(integrationEvent.FollowerId, integrationEvent.FolloweeId), cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Errors[0].Code);
        }
    }

    private async Task Send(RemoveAuthor command, CancellationToken cancellationToken)
    {
        var result = await dispatcher.Send<RemoveAuthor, int>(command, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Errors[0].Code);
        }
    }
}
