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

/// <summary>The follow's check that the followee is active: the writer, so a new account is found at once.</summary>
[ReadsTheWriter]
public sealed class IsAccountActiveHandler(IAccountRepository accounts) : IQueryHandler<IsAccountActive, bool>
{
    public async Task<Result<bool>> Handle(IsAccountActive query, CancellationToken cancellationToken) =>
        Result.Success(await accounts.Find(query.AccountId, cancellationToken) is { Status: AccountStatus.Active });
}

/// <summary>The export, a page at a time, from the read side.</summary>
public sealed class ExportAccountsHandler(IProfileReads accounts) : IQueryHandler<ExportAccounts, AccountsPage>
{
    public const int MaxLimit = 1_000;

    public async Task<Result<AccountsPage>> Handle(ExportAccounts query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var page = await accounts.Page(query.After, limit, cancellationToken);
        return Result.Success(new AccountsPage(
            [.. page.Select(a => new ExportedAccount(a.Id, a.Handle.Value, a.DisplayName, a.Status == AccountStatus.Active, a.RegisteredAt))],
            page.Count == limit ? page[^1].Id : null));
    }
}

internal static class ProfileMapping
{
    public static Profile ToProfile(this Account account) =>
        new(account.Id, account.Handle.Value, account.DisplayName, account.Status == AccountStatus.Active);
}
