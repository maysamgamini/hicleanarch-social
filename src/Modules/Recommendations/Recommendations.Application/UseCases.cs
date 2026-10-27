using Microsoft.Extensions.DependencyInjection;

using Recommendations.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Recommendations.Application;

public sealed record WhoToFollow(Guid ReaderId) : IQuery<IReadOnlyList<Suggestion>>;

public sealed record PeopleYouMayKnow(Guid ReaderId) : IQuery<IReadOnlyList<Suggestion>>;

/// <summary>
/// Both lists: computed by one traversal and cached for an hour, then filtered again every time they
/// are served, against the graph's current blocks (both ways), mutes and dismissals. A list cached
/// before a block can never show the blocked person, whatever the cache still holds.
/// </summary>
public sealed class SuggestionHandlers(IGraphReads graph, ISuggestionCache cache, TraversalLimits limits) :
    IQueryHandler<WhoToFollow, IReadOnlyList<Suggestion>>, IQueryHandler<PeopleYouMayKnow, IReadOnlyList<Suggestion>>
{
    /// <summary>People you may know walks at most the reader's 200 newest followers who are not followed back.</summary>
    public const int Followers = 200;

    public Task<Result<IReadOnlyList<Suggestion>>> Handle(WhoToFollow query, CancellationToken cancellationToken) =>
        Serve(query.ReaderId, SuggestionKind.WhoToFollow, async ct =>
            SuggestionRanking.Rank(await graph.WhoToFollow(query.ReaderId, limits, ct), limits.Results), cancellationToken);

    public Task<Result<IReadOnlyList<Suggestion>>> Handle(PeopleYouMayKnow query, CancellationToken cancellationToken) =>
        Serve(query.ReaderId, SuggestionKind.PeopleYouMayKnow, async ct =>
            SuggestionRanking.PeopleYouMayKnow(
                await graph.FollowersNotFollowedBack(query.ReaderId, Followers, limits, ct),
                await graph.FriendsOfFriends(query.ReaderId, limits, ct),
                limits.Results), cancellationToken);

    private async Task<Result<IReadOnlyList<Suggestion>>> Serve(
        Guid reader, string kind, Func<CancellationToken, Task<IReadOnlyList<Suggestion>>> compute, CancellationToken cancellationToken)
    {
        var list = await cache.Get(reader, kind, cancellationToken);
        if (list is null)
        {
            list = await compute(cancellationToken);
            await cache.Set(reader, kind, list, cancellationToken);
        }

        if (list.Count == 0)
        {
            return Result.Success(list);
        }

        var hidden = await graph.HiddenNow(reader, [.. list.Select(s => s.AccountId)], cancellationToken);
        return Result.Success<IReadOnlyList<Suggestion>>([.. list.Where(s => !hidden.Contains(s.AccountId))]);
    }
}

/// <summary><c>POST /v1/suggestions/{accountId}/dismiss</c>: not suggested to this reader for 90 days.</summary>
public sealed record Dismiss(Guid ReaderId, Guid AccountId) : ICommand<DateTimeOffset>;

public sealed class DismissHandler(IDismissals dismissals, IGraphProjection graph, ISuggestionCache cache, TimeProvider clock) : ICommandHandler<Dismiss, DateTimeOffset>
{
    public static readonly Error CannotDismissSelf = Error.Validation("cannot_dismiss_self", "An account cannot dismiss itself.");
    public static readonly Error AccountNotFound = Error.NotFound("account_not_found", "No account has this id.");

    public async Task<Result<DateTimeOffset>> Handle(Dismiss command, CancellationToken cancellationToken)
    {
        if (command.ReaderId == command.AccountId)
        {
            return Result.Failure<DateTimeOffset>(CannotDismissSelf);
        }

        var until = clock.GetUtcNow() + Domain.Dismissal.Lasts;
        // Durable first, in PostgreSQL; then projected into the graph, where the walks see it.
        await dismissals.Save(command.ReaderId, command.AccountId, until, cancellationToken);
        if (!await graph.Dismiss(command.ReaderId, command.AccountId, until, cancellationToken))
        {
            return Result.Failure<DateTimeOffset>(AccountNotFound);
        }

        await cache.Drop(command.ReaderId, cancellationToken);
        return Result.Success(until);
    }
}

/// <summary>An account entered or changed in the graph.</summary>
public sealed record ProjectAccount(Guid AccountId, string Handle) : ICommand<bool>;

public sealed record ProjectDeactivation(Guid AccountId) : ICommand<bool>;

/// <summary>A relationship event, as an edge change.</summary>
public sealed record ProjectEdge(EdgeChange Change) : ICommand<bool>;

public sealed class ProjectionHandlers(IGraphProjection graph, ISuggestionCache cache) :
    ICommandHandler<ProjectAccount, bool>, ICommandHandler<ProjectDeactivation, bool>, ICommandHandler<ProjectEdge, bool>
{
    public async Task<Result<bool>> Handle(ProjectAccount command, CancellationToken cancellationToken)
    {
        await graph.UpsertAccount(command.AccountId, command.Handle, cancellationToken);
        return Result.Success(true);
    }

    public async Task<Result<bool>> Handle(ProjectDeactivation command, CancellationToken cancellationToken)
    {
        await graph.Deactivate(command.AccountId, cancellationToken);
        return Result.Success(true);
    }

    /// <summary>
    /// A follow, a block or a mute changes what the actor should be suggested, so their cached lists
    /// go; a block changes the blocked person's too.
    /// </summary>
    public async Task<Result<bool>> Handle(ProjectEdge command, CancellationToken cancellationToken)
    {
        var applied = await graph.Apply(command.Change, cancellationToken);
        if (command.Change.On)
        {
            await cache.Drop(command.Change.From, cancellationToken);
            if (command.Change.Kind == EdgeKind.Blocks)
            {
                await cache.Drop(command.Change.To, cancellationToken);
            }
        }

        return Result.Success(applied);
    }
}

public static class RecommendationsApplication
{
    public static IServiceCollection AddRecommendationsApplication(this IServiceCollection services) =>
        services
            .AddSingleton(TraversalLimits.Default)
            .AddScoped<SuggestionHandlers>()
            .AddScoped<IQueryHandler<WhoToFollow, IReadOnlyList<Suggestion>>>(sp => sp.GetRequiredService<SuggestionHandlers>())
            .AddScoped<IQueryHandler<PeopleYouMayKnow, IReadOnlyList<Suggestion>>>(sp => sp.GetRequiredService<SuggestionHandlers>())
            .AddScoped<ICommandHandler<Dismiss, DateTimeOffset>, DismissHandler>()
            .AddScoped<ProjectionHandlers>()
            .AddScoped<ICommandHandler<ProjectAccount, bool>>(sp => sp.GetRequiredService<ProjectionHandlers>())
            .AddScoped<ICommandHandler<ProjectDeactivation, bool>>(sp => sp.GetRequiredService<ProjectionHandlers>())
            .AddScoped<ICommandHandler<ProjectEdge, bool>>(sp => sp.GetRequiredService<ProjectionHandlers>())
            .AddScoped<MaintenanceHandlers>()
            .AddScoped<ICommandHandler<Reconcile, ReconciliationReport>>(sp => sp.GetRequiredService<MaintenanceHandlers>())
            .AddScoped<ICommandHandler<Compact, long>>(sp => sp.GetRequiredService<MaintenanceHandlers>());
}
