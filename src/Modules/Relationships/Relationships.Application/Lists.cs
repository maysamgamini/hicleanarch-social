using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Relationships.Application;

public sealed record ListFollowers(Guid AccountId, Guid? After, int Limit) : IQuery<RelationshipPage>;

public sealed record ListFollowing(Guid AccountId, Guid? After, int Limit) : IQuery<RelationshipPage>;

/// <summary>Page sizes the API accepts: 1 to 100, 50 when none is asked for.</summary>
public static class PageSize
{
    public const int Default = 50;
    public const int Max = 100;

    public static int Clamp(int? asked) => Math.Clamp(asked ?? Default, 1, Max);
}

public sealed class ListFollowersHandler(IRelationshipReads reads) : IQueryHandler<ListFollowers, RelationshipPage>
{
    public async Task<Result<RelationshipPage>> Handle(ListFollowers query, CancellationToken cancellationToken) =>
        Result.Success(await reads.Followers(query.AccountId, query.After, PageSize.Clamp(query.Limit), cancellationToken));
}

public sealed class ListFollowingHandler(IRelationshipReads reads) : IQueryHandler<ListFollowing, RelationshipPage>
{
    public async Task<Result<RelationshipPage>> Handle(ListFollowing query, CancellationToken cancellationToken) =>
        Result.Success(await reads.Following(query.AccountId, query.After, PageSize.Clamp(query.Limit), cancellationToken));
}

/// <summary>Answers other modules: the fanout pages an author's audience 1,000 at a time.</summary>
public sealed class GetFollowersPageHandler(IRelationshipReads reads) : IQueryHandler<GetFollowersPage, FollowersPage>
{
    public const int MaxLimit = 1_000;

    public async Task<Result<FollowersPage>> Handle(GetFollowersPage query, CancellationToken cancellationToken)
    {
        var page = await reads.Followers(query.AccountId, query.After, Math.Clamp(query.Limit, 1, MaxLimit), cancellationToken);
        return Result.Success(new FollowersPage([.. page.Items.Select(i => i.AccountId)], page.Next));
    }
}

public sealed class GetFollowingHandler(IRelationshipReads reads) : IQueryHandler<GetFollowing, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(GetFollowing query, CancellationToken cancellationToken) =>
        Result.Success(await reads.AllFollowing(query.AccountId, cancellationToken));
}

public sealed class GetCountsHandler(IRelationshipReads reads) : IQueryHandler<GetCounts, RelationshipCounts>
{
    public async Task<Result<RelationshipCounts>> Handle(GetCounts query, CancellationToken cancellationToken)
    {
        var (followers, following) = await reads.Counts(query.AccountId, cancellationToken);
        return Result.Success(new RelationshipCounts(followers, following));
    }
}
