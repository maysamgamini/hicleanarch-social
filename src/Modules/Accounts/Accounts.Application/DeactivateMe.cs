using Accounts.Contracts;
using Accounts.Domain;

using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Accounts.Application;

/// <summary><c>POST /v1/me/deactivate</c>: the signed-in person's account stops being active. Sending it again changes nothing.</summary>
public sealed record DeactivateMe(Guid AccountId) : ICommand<Profile>;

public sealed class DeactivateMeHandler(
    IAccountRepository accounts,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<DeactivateMe, Profile>
{
    public Task<Result<Profile>> Handle(DeactivateMe command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            var account = await accounts.Find(command.AccountId, ct);
            if (account is null)
            {
                return Result.Failure<Profile>(AccountErrors.NotFound);
            }

            var now = clock.GetUtcNow();
            if (account.Deactivate(now))
            {
                await accounts.Update(account, ct);
                await outbox.Add(new AccountDeactivated(Guid.CreateVersion7(), now, account.Id), ct);
            }

            return Result.Success(account.ToProfile());
        }, cancellationToken);
}
