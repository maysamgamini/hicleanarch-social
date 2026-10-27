using Accounts.Contracts;
using Accounts.Domain;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Accounts.Application;

public sealed class GetProfileByHandleHandler(IProfileReads accounts) : IQueryHandler<GetProfileByHandle, Profile>
{
    public async Task<Result<Profile>> Handle(GetProfileByHandle query, CancellationToken cancellationToken)
    {
        var handle = Domain.Handle.Create(query.Handle);
        var account = handle.IsSuccess ? await accounts.FindByHandle(handle.Value, cancellationToken) : null;
        // A deactivated account's profile is no longer shown.
        return account is null || account.Status != AccountStatus.Active
            ? Result.Failure<Profile>(AccountErrors.NotFound)
            : Result.Success(account.ToProfile());
    }
}

/// <summary>One read for any number of ids: the feed hydrates a whole page with it.</summary>
public sealed class GetProfilesHandler(IProfileReads accounts) : IQueryHandler<GetProfiles, IReadOnlyList<Profile>>
{
    public async Task<Result<IReadOnlyList<Profile>>> Handle(GetProfiles query, CancellationToken cancellationToken)
    {
        var found = await accounts.FindMany([.. query.AccountIds.Distinct()], cancellationToken);
        return Result.Success<IReadOnlyList<Profile>>([.. found.Select(a => a.ToProfile())]);
    }
}

internal static class ProfileMapping
{
    public static Profile ToProfile(this Account account) =>
        new(account.Id, account.Handle.Value, account.DisplayName, account.Status == AccountStatus.Active);
}
