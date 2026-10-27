using Accounts.Application;
using Accounts.Domain;

using Microsoft.EntityFrameworkCore;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Accounts.Infrastructure.Persistence;

/// <summary>Profiles from the reader endpoint (ConnectionStrings:AccountsRead), read-only sessions, no tracking.</summary>
internal sealed class EfProfileReads(ReadOnlyDb<AccountsDbContext> reader) : IProfileReads
{
    public Task<Account?> FindByHandle(Handle handle, CancellationToken cancellationToken) =>
        reader.Context.Accounts.FromSql($"SELECT * FROM accounts.accounts WHERE lower(handle) = {handle.Normalized}")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Account>> FindMany(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await reader.Context.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);

    // Keyset: one index range on the primary key, from just after the last id of the previous page.
    public async Task<IReadOnlyList<Account>> Page(Guid? after, int limit, CancellationToken cancellationToken) =>
        await (after is { } last
            ? reader.Context.Accounts.FromSql($"SELECT * FROM accounts.accounts WHERE id > {last} ORDER BY id LIMIT {limit}")
            : reader.Context.Accounts.FromSql($"SELECT * FROM accounts.accounts ORDER BY id LIMIT {limit}"))
            .ToListAsync(cancellationToken);
}
