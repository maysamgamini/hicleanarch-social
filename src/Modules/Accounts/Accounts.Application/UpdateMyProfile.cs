using Accounts.Contracts;
using Accounts.Domain;

using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Accounts.Application;

/// <summary>The module's name, which keys its transaction scope and its outbox.</summary>
public static class ModuleName
{
    public const string Value = "Accounts";
}

/// <summary>The signed-in person registers their profile, or changes it: one request, <c>PUT /v1/me</c>.</summary>
public sealed record UpdateMyProfile(Guid AccountId, string? Handle, string? DisplayName) : ICommand<ProfileUpdate>;

/// <summary>The profile after the change, and whether this request created it.</summary>
public sealed record ProfileUpdate(Profile Profile, bool Registered);

/// <summary>
/// The account and its event commit together: AccountRegistered or ProfileUpdated is written to the
/// outbox in the change's own transaction.
/// </summary>
public sealed class UpdateMyProfileHandler(
    IAccountRepository accounts,
    TimeProvider clock,
    [FromKeyedServices(ModuleName.Value)] ITransactionScope transaction,
    [FromKeyedServices(ModuleName.Value)] IOutbox outbox) : ICommandHandler<UpdateMyProfile, ProfileUpdate>
{
    // Two registrations of the same person at once: the second finds the first's row and is an update.
    private static readonly Error RegisteredMeanwhile = Error.Conflict("registered_meanwhile", "Registered by a request running beside this one.");

    public async Task<Result<ProfileUpdate>> Handle(UpdateMyProfile command, CancellationToken cancellationToken)
    {
        var result = await transaction.Run(ct => Once(command, ct), cancellationToken);
        return result.Errors is [{ Code: var code }] && code == RegisteredMeanwhile.Code
            ? await transaction.Run(ct => Once(command, ct), cancellationToken)
            : result;
    }

    private async Task<Result<ProfileUpdate>> Once(UpdateMyProfile command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var existing = await accounts.Find(command.AccountId, cancellationToken);
        if (existing is null)
        {
            var registered = Account.Register(command.AccountId, command.Handle, command.DisplayName, now);
            if (!registered.IsSuccess)
            {
                return Result.Failure<ProfileUpdate>(registered.Errors);
            }

            var account = registered.Value;
            switch (await accounts.Add(account, cancellationToken))
            {
                case SaveOutcome.HandleTaken:
                    return Result.Failure<ProfileUpdate>(AccountErrors.HandleTaken);
                case SaveOutcome.AlreadyRegistered:
                    return Result.Failure<ProfileUpdate>(RegisteredMeanwhile);
                default:
                    await outbox.Add(new AccountRegistered(Guid.CreateVersion7(), now, account.Id, account.Handle.Value, account.DisplayName), cancellationToken);
                    return Result.Success(new ProfileUpdate(account.ToProfile(), Registered: true));
            }
        }

        var before = (existing.Handle, existing.DisplayName);
        var updated = existing.UpdateProfile(command.Handle, command.DisplayName, now);
        if (!updated.IsSuccess)
        {
            return Result.Failure<ProfileUpdate>(updated.Errors);
        }

        if (!existing.Differs(before.Handle, before.DisplayName))
        {
            return Result.Success(new ProfileUpdate(existing.ToProfile(), Registered: false));
        }

        if (await accounts.Update(existing, cancellationToken) != SaveOutcome.Saved)
        {
            return Result.Failure<ProfileUpdate>(AccountErrors.HandleTaken);
        }

        await outbox.Add(new ProfileUpdated(Guid.CreateVersion7(), now, existing.Id, existing.Handle.Value, existing.DisplayName), cancellationToken);
        return Result.Success(new ProfileUpdate(existing.ToProfile(), Registered: false));
    }
}
