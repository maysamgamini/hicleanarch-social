using Microsoft.Extensions.DependencyInjection;

using Relationships.Contracts;
using Relationships.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Relationships.Application;

/// <summary>What a block, mute or their undoing leaves behind.</summary>
public sealed record BlockState(Guid AccountId, bool Blocking);

public sealed record MuteState(Guid AccountId, bool Muting);

public sealed record Block(Guid BlockerId, Guid BlockedId) : ICommand<BlockState>;

public sealed record Unblock(Guid BlockerId, Guid BlockedId) : ICommand<BlockState>;

public sealed record Mute(Guid MuterId, Guid MutedId) : ICommand<MuteState>;

public sealed record Unmute(Guid MuterId, Guid MutedId) : ICommand<MuteState>;

/// <summary>
/// The block and the removal of both follows commit together, under the pair's lock: a follow that
/// raced the block either committed first (and is removed here) or runs after it (and is refused).
/// </summary>
public sealed class BlockHandler(
    IRelationshipStore store,
    IAccountDirectory accounts,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<Block, BlockState>
{
    public async Task<Result<BlockState>> Handle(Block command, CancellationToken cancellationToken)
    {
        if (command.BlockerId == command.BlockedId)
        {
            return Result.Failure<BlockState>(RelationshipErrors.CannotBlockSelf);
        }

        if (!await accounts.IsActive(command.BlockedId, cancellationToken))
        {
            return Result.Failure<BlockState>(RelationshipErrors.AccountNotFound);
        }

        return await transaction.Run(async ct =>
        {
            var pair = await store.LockPair(command.BlockerId, command.BlockedId, ct);
            var outcome = pair.Block(command.BlockerId, command.BlockedId);
            var now = clock.GetUtcNow();
            var events = new List<IntegrationEvent>();
            foreach (var follow in outcome.RemovedFollows)
            {
                await store.RemoveFollow(follow.From, follow.To, ct);
                // Every listener hears that the follow ended, with the block's version.
                events.Add(new FollowEnded(Guid.CreateVersion7(), now, follow.From, follow.To, pair.Version));
            }

            if (outcome.Block == Change.Added)
            {
                await store.Add(EdgeKind.Block, command.BlockerId, command.BlockedId, ct);
                events.Add(new AccountBlocked(Guid.CreateVersion7(), now, command.BlockerId, command.BlockedId, pair.Version));
            }

            if (events.Count > 0)
            {
                await outbox.AddRange(events, ct);
            }

            return Result.Success(new BlockState(command.BlockedId, Blocking: true));
        }, cancellationToken);
    }
}

public sealed class UnblockHandler(
    IRelationshipStore store,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<Unblock, BlockState>
{
    public Task<Result<BlockState>> Handle(Unblock command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var pair = await store.LockPair(command.BlockerId, command.BlockedId, ct);
            if (pair.Unblock(command.BlockerId, command.BlockedId) == Change.Removed)
            {
                await store.Remove(EdgeKind.Block, command.BlockerId, command.BlockedId, ct);
                await outbox.Add(new AccountUnblocked(Guid.CreateVersion7(), clock.GetUtcNow(), command.BlockerId, command.BlockedId, pair.Version), ct);
            }

            return Result.Success(new BlockState(command.BlockedId, Blocking: false));
        }, cancellationToken);
}

public sealed class MuteHandler(
    IRelationshipStore store,
    IAccountDirectory accounts,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<Mute, MuteState>
{
    public async Task<Result<MuteState>> Handle(Mute command, CancellationToken cancellationToken)
    {
        if (command.MuterId == command.MutedId)
        {
            return Result.Failure<MuteState>(RelationshipErrors.CannotMuteSelf);
        }

        if (!await accounts.IsActive(command.MutedId, cancellationToken))
        {
            return Result.Failure<MuteState>(RelationshipErrors.AccountNotFound);
        }

        return await transaction.Run(async ct =>
        {
            var pair = await store.LockPair(command.MuterId, command.MutedId, ct);
            if (pair.Mute(command.MuterId, command.MutedId) == Change.Added)
            {
                await store.Add(EdgeKind.Mute, command.MuterId, command.MutedId, ct);
                await outbox.Add(new AccountMuted(Guid.CreateVersion7(), clock.GetUtcNow(), command.MuterId, command.MutedId, pair.Version), ct);
            }

            return Result.Success(new MuteState(command.MutedId, Muting: true));
        }, cancellationToken);
    }
}

public sealed class UnmuteHandler(
    IRelationshipStore store,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<Unmute, MuteState>
{
    public Task<Result<MuteState>> Handle(Unmute command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var pair = await store.LockPair(command.MuterId, command.MutedId, ct);
            if (pair.Unmute(command.MuterId, command.MutedId) == Change.Removed)
            {
                await store.Remove(EdgeKind.Mute, command.MuterId, command.MutedId, ct);
                await outbox.Add(new AccountUnmuted(Guid.CreateVersion7(), clock.GetUtcNow(), command.MuterId, command.MutedId, pair.Version), ct);
            }

            return Result.Success(new MuteState(command.MutedId, Muting: false));
        }, cancellationToken);
}

/// <summary>Answers other modules: the feed's read filter asks it for every page.</summary>
public sealed class GetHiddenAuthorsHandler(IRelationshipReads reads) : IQueryHandler<GetHiddenAuthors, HiddenAuthors>
{
    public async Task<Result<HiddenAuthors>> Handle(GetHiddenAuthors query, CancellationToken cancellationToken) =>
        Result.Success(new HiddenAuthors(await reads.HiddenFrom(query.ViewerId, cancellationToken)));
}
