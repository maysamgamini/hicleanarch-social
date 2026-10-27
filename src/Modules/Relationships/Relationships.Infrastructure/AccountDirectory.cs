using Accounts.Contracts;

using Relationships.Application;

using Social.BuildingBlocks.Application;

namespace Relationships.Infrastructure;

/// <summary>Asks Accounts, through its contract, whether an account is active. Never reads Accounts' tables.</summary>
internal sealed class AccountDirectory(IDispatcher dispatcher) : IAccountDirectory
{
    public async Task<bool> IsActive(Guid account, CancellationToken cancellationToken)
    {
        var profiles = await dispatcher.Query<GetProfiles, IReadOnlyList<Profile>>(new GetProfiles([account]), cancellationToken);
        return profiles.IsSuccess && profiles.Value.Any(p => p.Active);
    }
}
