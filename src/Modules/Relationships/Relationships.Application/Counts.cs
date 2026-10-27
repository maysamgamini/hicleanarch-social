using Microsoft.Extensions.DependencyInjection;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Relationships.Application;

/// <summary>One pass of the counts aggregator: fold the ledger, announce each changed account once.</summary>
public sealed record FoldFollowerCounts : ICommand<int>;

/// <summary>
/// Folds the ledger into relationship_counts and writes one FollowerCountChanged per changed account,
/// in one transaction: a count and its announcement commit together.
/// </summary>
public sealed class FoldFollowerCountsHandler(
    IFollowerLedger ledger,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<FoldFollowerCounts, int>
{
    public const int Batch = 100_000;

    public Task<Result<int>> Handle(FoldFollowerCounts command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var changed = await ledger.Fold(Batch, ct);
            if (changed.Count > 0)
            {
                var now = clock.GetUtcNow();
                await outbox.AddRange([.. changed.Select(c => (IntegrationEvent)new FollowerCountChanged(Guid.CreateVersion7(), now, c.Account, c.Followers))], ct);
            }

            return Result.Success(changed.Count);
        }, cancellationToken);
}

/// <summary>The nightly check: accounts whose counts disagree with their follows. Must be zero.</summary>
public sealed record CheckCounts : IQuery<long>;

/// <summary>The check compares counts with the truth itself: the writer, never a replica a moment behind.</summary>
[ReadsTheWriter]
public sealed class CheckCountsHandler(IFollowerLedger ledger) : IQueryHandler<CheckCounts, long>
{
    public async Task<Result<long>> Handle(CheckCounts query, CancellationToken cancellationToken) =>
        Result.Success(await ledger.Mismatches(cancellationToken));
}
