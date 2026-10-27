using Accounts.Contracts;

using Relationships.Application;

using Social.BuildingBlocks.Application;

namespace Relationships.Infrastructure;

/// <summary>
/// Asks Accounts, through its contract, whether an account is active. Never reads Accounts' tables.
/// It asks the writer (IsAccountActive, s3-l17): GetProfiles reads a replica, which may not yet have an
/// account that signed up a moment ago.
/// </summary>
internal sealed class AccountDirectory(IDispatcher dispatcher) : IAccountDirectory
{
    public async Task<bool> IsActive(Guid account, CancellationToken cancellationToken)
    {
        var active = await dispatcher.Query<IsAccountActive, bool>(new IsAccountActive(account), cancellationToken);
        return active.IsSuccess && active.Value;
    }
}
