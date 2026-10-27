using Social.BuildingBlocks.Application;

namespace Accounts.Contracts;

/// <summary>An account as other modules and clients see it.</summary>
public sealed record Profile(Guid AccountId, string Handle, string DisplayName, bool Active);

/// <summary>The profile with this handle, in any mix of case.</summary>
public sealed record GetProfileByHandle(string Handle) : IQuery<Profile>;

/// <summary>The profiles of these accounts, in one read; ids with no account are left out.</summary>
public sealed record GetProfiles(IReadOnlyList<Guid> AccountIds) : IQuery<IReadOnlyList<Profile>>;

/// <summary>
/// Whether this account exists and is active, from the writer (s3-l17): a follow of an account that
/// signed up a moment ago must find it, which a replica a moment behind may not.
/// </summary>
public sealed record IsAccountActive(Guid AccountId) : IQuery<bool>;

/// <summary>An account as the export gives it: what a copy kept by another service starts from.</summary>
public sealed record ExportedAccount(Guid AccountId, string Handle, string DisplayName, bool Active, DateTimeOffset RegisteredAt);

/// <summary>
/// Every account, a page at a time in id order, after <paramref name="After"/> (s3-l17): the export a
/// service extracted in Stages 4 and 5 fills its copy from. <see cref="AccountsPage.Next"/> is null on the last page.
/// </summary>
public sealed record ExportAccounts(Guid? After, int Limit) : IQuery<AccountsPage>;

public sealed record AccountsPage(IReadOnlyList<ExportedAccount> Accounts, Guid? Next);
