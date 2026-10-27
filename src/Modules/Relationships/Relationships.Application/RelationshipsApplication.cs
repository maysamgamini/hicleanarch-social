using Microsoft.Extensions.DependencyInjection;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;

namespace Relationships.Application;

public static class RelationshipsApplication
{
    public static IServiceCollection AddRelationshipsApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<Follow, FollowState>, FollowHandler>()
            .AddScoped<ICommandHandler<FoldFollowerCounts, int>, FoldFollowerCountsHandler>()
            .AddScoped<IQueryHandler<CheckCounts, long>, CheckCountsHandler>()
            .AddScoped<ICommandHandler<Unfollow, FollowState>, UnfollowHandler>()
            .AddScoped<ICommandHandler<Block, BlockState>, BlockHandler>()
            .AddScoped<ICommandHandler<Unblock, BlockState>, UnblockHandler>()
            .AddScoped<ICommandHandler<Mute, MuteState>, MuteHandler>()
            .AddScoped<ICommandHandler<Unmute, MuteState>, UnmuteHandler>()
            .AddScoped<IQueryHandler<GetHiddenAuthors, HiddenAuthors>, GetHiddenAuthorsHandler>()
            .AddScoped<IQueryHandler<ListFollowers, RelationshipPage>, ListFollowersHandler>()
            .AddScoped<IQueryHandler<ListFollowing, RelationshipPage>, ListFollowingHandler>()
            .AddScoped<IQueryHandler<GetFollowersPage, FollowersPage>, GetFollowersPageHandler>()
            .AddScoped<IQueryHandler<GetFollowing, IReadOnlyList<Guid>>, GetFollowingHandler>()
            .AddScoped<IQueryHandler<GetCounts, RelationshipCounts>, GetCountsHandler>()
            .AddScoped<IQueryHandler<GetCountsOf, IReadOnlyList<AccountCounts>>, GetCountsOfHandler>()
            .AddScoped<IQueryHandler<GetPairsOf, IReadOnlyList<PairState>>, GetPairsOfHandler>()
            .AddScoped<IQueryHandler<ExportPairs, PairsPage>, ExportPairsHandler>();
}
