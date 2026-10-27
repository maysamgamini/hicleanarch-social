using Microsoft.Extensions.DependencyInjection;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;

namespace Relationships.Application;

public static class RelationshipsApplication
{
    public static IServiceCollection AddRelationshipsApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<Follow, FollowState>, FollowHandler>()
            .AddScoped<ICommandHandler<Unfollow, FollowState>, UnfollowHandler>()
            .AddScoped<IQueryHandler<ListFollowers, RelationshipPage>, ListFollowersHandler>()
            .AddScoped<IQueryHandler<ListFollowing, RelationshipPage>, ListFollowingHandler>()
            .AddScoped<IQueryHandler<GetFollowersPage, FollowersPage>, GetFollowersPageHandler>()
            .AddScoped<IQueryHandler<GetFollowing, IReadOnlyList<Guid>>, GetFollowingHandler>()
            .AddScoped<IQueryHandler<GetCounts, RelationshipCounts>, GetCountsHandler>();
}
