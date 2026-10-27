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
public sealed class SuggestionHandlers(IGraphReads graph, ISuggestionCache cache, TraversalLimits limits, TimeProvider clock) :
    IQueryHandler<WhoToFollow, IReadOnlyList<Suggestion>>, IQueryHandler<PeopleYouMayKnow, IReadOnlyList<Suggestion>>
{
    public Task<Result<IReadOnlyList<Suggestion>>> Handle(WhoToFollow query, CancellationToken cancellationToken) =>
        Serve(query.ReaderId, SuggestionKind.WhoToFollow, ct => SuggestionLists.WhoToFollow(graph, query.ReaderId, limits, ct), cancellationToken);

    public Task<Result<IReadOnlyList<Suggestion>>> Handle(PeopleYouMayKnow query, CancellationToken cancellationToken) =>
        Serve(query.ReaderId, SuggestionKind.PeopleYouMayKnow, ct => SuggestionLists.PeopleYouMayKnow(graph, query.ReaderId, limits, ct), cancellationToken);

    /// <summary>
    /// The precomputed list, or the on-request one, or a traversal now (only for a reader with neither);
    /// whichever it is, filtered against the current blocks, mutes and dismissals.
    /// </summary>
    private async Task<Result<IReadOnlyList<Suggestion>>> Serve(
        Guid reader, string kind, Func<CancellationToken, Task<IReadOnlyList<Suggestion>>> compute, CancellationToken cancellationToken)
    {
        await cache.Opened(reader, clock.GetUtcNow(), cancellationToken);
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

/// <summary>The two lists, computed by traversal: on request, and by the precompute pass.</summary>
public static class SuggestionLists
{
    /// <summary>People you may know walks at most the reader's 200 newest followers who are not followed back.</summary>
    public const int Followers = 200;

    public static async Task<IReadOnlyList<Suggestion>> WhoToFollow(IGraphReads graph, Guid reader, TraversalLimits limits, CancellationToken cancellationToken) =>
        SuggestionRanking.Rank(await graph.WhoToFollow(reader, limits, cancellationToken), limits.Results);

    public static async Task<IReadOnlyList<Suggestion>> PeopleYouMayKnow(IGraphReads graph, Guid reader, TraversalLimits limits, CancellationToken cancellationToken) =>
        SuggestionRanking.PeopleYouMayKnow(
            await graph.FollowersNotFollowedBack(reader, Followers, limits, cancellationToken),
            await graph.FriendsOfFriends(reader, limits, cancellationToken),
            limits.Results);
}

/// <summary>One precompute pass: the readers whose lists are due, at most <paramref name="Limit"/>. How many it computed.</summary>
public sealed record PrecomputeSuggestions(int Limit = 1_000) : ICommand<int>;

/// <summary>
/// Precomputed suggestions (s3-l12): for every reader who opened suggestions in the last 7 days, both
/// lists computed in the background, again after 24 hours or after ten follow changes, so a reader's
/// request finds a list and walks nothing.
/// </summary>
public sealed class PrecomputeSuggestionsHandler(IGraphReads graph, ISuggestionCache cache, TraversalLimits limits, TimeProvider clock)
    : ICommandHandler<PrecomputeSuggestions, int>
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public static readonly TimeSpan Refresh = TimeSpan.FromHours(24);

    public const long ChangesBeforeRefresh = 10;

    public async Task<Result<int>> Handle(PrecomputeSuggestions command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var computed = 0;
        foreach (var reader in await cache.OpenedSince(now - Window, int.MaxValue, cancellationToken))
        {
            if (computed >= command.Limit)
            {
                break;
            }

            if (await cache.Precomputed(reader, cancellationToken) is { } state && now - state.At < Refresh && state.Changes < ChangesBeforeRefresh)
            {
                continue;
            }

            var lists = new Dictionary<string, IReadOnlyList<Suggestion>>
            {
                [SuggestionKind.WhoToFollow] = await SuggestionLists.WhoToFollow(graph, reader, limits, cancellationToken),
                [SuggestionKind.PeopleYouMayKnow] = await SuggestionLists.PeopleYouMayKnow(graph, reader, limits, cancellationToken),
            };
            await cache.SetPrecomputed(reader, lists, now, cancellationToken);
            computed++;
        }

        return Result.Success(computed);
    }
}

/// <summary>An account's follower count, as Relationships announced it: the graph's followers degree.</summary>
public sealed record ProjectFollowerCount(Guid AccountId, long Followers, DateTimeOffset At) : ICommand<bool>;

public sealed class ProjectFollowerCountHandler(IGraphProjection graph) : ICommandHandler<ProjectFollowerCount, bool>
{
    public async Task<Result<bool>> Handle(ProjectFollowerCount command, CancellationToken cancellationToken) =>
        Result.Success(await graph.SetFollowers(command.AccountId, command.Followers, command.At, cancellationToken));
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
            // A reader with precomputed lists keeps them (blocks, mutes and dismissals are filtered when
            // served) and counts the change: the tenth sends them to be computed again.
            if (!await cache.CountChange(command.Change.From, cancellationToken))
            {
                await cache.Drop(command.Change.From, cancellationToken);
            }

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
            .AddScoped<ICommandHandler<ProjectFollowerCount, bool>, ProjectFollowerCountHandler>()
            .AddScoped<ICommandHandler<PrecomputeSuggestions, int>, PrecomputeSuggestionsHandler>()
            .AddScoped<MaintenanceHandlers>()
            .AddScoped<ICommandHandler<Reconcile, ReconciliationReport>>(sp => sp.GetRequiredService<MaintenanceHandlers>())
            .AddScoped<ICommandHandler<Compact, long>>(sp => sp.GetRequiredService<MaintenanceHandlers>());
}
