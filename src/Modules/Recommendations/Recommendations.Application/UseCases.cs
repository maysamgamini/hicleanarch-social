using Microsoft.Extensions.DependencyInjection;

using Recommendations.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Recommendations.Application;

public sealed record WhoToFollow(Guid ReaderId) : IQuery<IReadOnlyList<Suggestion>>;

public sealed class WhoToFollowHandler(IGraphReads graph, TraversalLimits limits) : IQueryHandler<WhoToFollow, IReadOnlyList<Suggestion>>
{
    public async Task<Result<IReadOnlyList<Suggestion>>> Handle(WhoToFollow query, CancellationToken cancellationToken) =>
        Result.Success(SuggestionRanking.Rank(await graph.WhoToFollow(query.ReaderId, limits, cancellationToken), limits.Results));
}

/// <summary>An account entered or changed in the graph.</summary>
public sealed record ProjectAccount(Guid AccountId, string Handle) : ICommand<bool>;

public sealed record ProjectDeactivation(Guid AccountId) : ICommand<bool>;

/// <summary>A relationship event, as an edge change.</summary>
public sealed record ProjectEdge(EdgeChange Change) : ICommand<bool>;

public sealed class ProjectionHandlers(IGraphProjection graph) :
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

    public async Task<Result<bool>> Handle(ProjectEdge command, CancellationToken cancellationToken) =>
        Result.Success(await graph.Apply(command.Change, cancellationToken));
}

public static class RecommendationsApplication
{
    public static IServiceCollection AddRecommendationsApplication(this IServiceCollection services) =>
        services
            .AddSingleton(TraversalLimits.Default)
            .AddScoped<IQueryHandler<WhoToFollow, IReadOnlyList<Suggestion>>, WhoToFollowHandler>()
            .AddScoped<ProjectionHandlers>()
            .AddScoped<ICommandHandler<ProjectAccount, bool>>(sp => sp.GetRequiredService<ProjectionHandlers>())
            .AddScoped<ICommandHandler<ProjectDeactivation, bool>>(sp => sp.GetRequiredService<ProjectionHandlers>())
            .AddScoped<ICommandHandler<ProjectEdge, bool>>(sp => sp.GetRequiredService<ProjectionHandlers>())
            .AddScoped<MaintenanceHandlers>()
            .AddScoped<ICommandHandler<Reconcile, ReconciliationReport>>(sp => sp.GetRequiredService<MaintenanceHandlers>())
            .AddScoped<ICommandHandler<Compact, long>>(sp => sp.GetRequiredService<MaintenanceHandlers>());
}
