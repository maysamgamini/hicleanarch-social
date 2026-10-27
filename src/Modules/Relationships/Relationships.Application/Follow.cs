using Microsoft.Extensions.DependencyInjection;

using Relationships.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Relationships.Application;

/// <summary>What a follow or an unfollow leaves behind: whether the caller now follows the account.</summary>
public sealed record FollowState(Guid AccountId, bool Following);

/// <summary><c>PUT /v1/accounts/{id}/follow</c>: idempotent, so a retry finds the follow already there.</summary>
public sealed record Follow(Guid FollowerId, Guid FolloweeId) : ICommand<FollowState>;

/// <summary><c>DELETE /v1/accounts/{id}/follow</c>: unfollowing an account not followed changes nothing.</summary>
public sealed record Unfollow(Guid FollowerId, Guid FolloweeId) : ICommand<FollowState>;

public sealed class FollowHandler(
    IRelationshipStore store,
    IAccountDirectory accounts,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction) : ICommandHandler<Follow, FollowState>
{
    public async Task<Result<FollowState>> Handle(Follow command, CancellationToken cancellationToken)
    {
        if (command.FollowerId == command.FolloweeId)
        {
            return Result.Failure<FollowState>(RelationshipErrors.CannotFollowSelf);
        }

        if (!await accounts.IsActive(command.FolloweeId, cancellationToken))
        {
            return Result.Failure<FollowState>(RelationshipErrors.AccountNotFound);
        }

        return await transaction.Run(async ct =>
        {
            var pair = await store.LockPair(command.FollowerId, command.FolloweeId, ct);
            if (pair.Follow(command.FollowerId, command.FolloweeId) == Change.Added
                && !await store.AddFollow(command.FollowerId, command.FolloweeId, ct))
            {
                return Result.Failure<FollowState>(RelationshipErrors.FollowLimit);
            }

            return Result.Success(new FollowState(command.FolloweeId, Following: true));
        }, cancellationToken);
    }
}

public sealed class UnfollowHandler(
    IRelationshipStore store,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction) : ICommandHandler<Unfollow, FollowState>
{
    public Task<Result<FollowState>> Handle(Unfollow command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var pair = await store.LockPair(command.FollowerId, command.FolloweeId, ct);
            if (pair.Unfollow(command.FollowerId, command.FolloweeId) == Change.Removed)
            {
                await store.RemoveFollow(command.FollowerId, command.FolloweeId, ct);
            }

            return Result.Success(new FollowState(command.FolloweeId, Following: false));
        }, cancellationToken);
}
