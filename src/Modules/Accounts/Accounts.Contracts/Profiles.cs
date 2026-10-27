using Social.BuildingBlocks.Application;

namespace Accounts.Contracts;

/// <summary>An account as other modules and clients see it.</summary>
public sealed record Profile(Guid AccountId, string Handle, string DisplayName, bool Active);

/// <summary>The profile with this handle, in any mix of case.</summary>
public sealed record GetProfileByHandle(string Handle) : IQuery<Profile>;

/// <summary>The profiles of these accounts, in one read; ids with no account are left out.</summary>
public sealed record GetProfiles(IReadOnlyList<Guid> AccountIds) : IQuery<IReadOnlyList<Profile>>;
