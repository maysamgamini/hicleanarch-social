using Accounts.Contracts;
using Accounts.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Accounts.Application;

/// <summary>The signed-in person registers their profile, or changes it: one request, <c>PUT /v1/me</c>.</summary>
public sealed record UpdateMyProfile(Guid AccountId, string? Handle, string? DisplayName) : ICommand<ProfileUpdate>;

/// <summary>The profile after the change, and whether this request created it.</summary>
public sealed record ProfileUpdate(Profile Profile, bool Registered);

public sealed class UpdateMyProfileHandler(IAccountRepository accounts, TimeProvider clock) : ICommandHandler<UpdateMyProfile, ProfileUpdate>
{
    public async Task<Result<ProfileUpdate>> Handle(UpdateMyProfile command, CancellationToken cancellationToken)
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

            switch (await accounts.Add(registered.Value, cancellationToken))
            {
                case SaveOutcome.Saved:
                    return Result.Success(new ProfileUpdate(registered.Value.ToProfile(), Registered: true));
                case SaveOutcome.HandleTaken:
                    return Result.Failure<ProfileUpdate>(AccountErrors.HandleTaken);
                default:
                    // The same person registered in a request running beside this one: this one is an update.
                    existing = await accounts.Find(command.AccountId, cancellationToken)
                        ?? throw new InvalidOperationException("An account reported as registered was not found.");
                    break;
            }
        }

        var updated = existing.UpdateProfile(command.Handle, command.DisplayName, now);
        if (!updated.IsSuccess)
        {
            return Result.Failure<ProfileUpdate>(updated.Errors);
        }

        return await accounts.Update(existing, cancellationToken) == SaveOutcome.Saved
            ? Result.Success(new ProfileUpdate(existing.ToProfile(), Registered: false))
            : Result.Failure<ProfileUpdate>(AccountErrors.HandleTaken);
    }
}
