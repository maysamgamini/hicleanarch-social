using Accounts.Application;
using Accounts.Domain;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Accounts.Infrastructure.Persistence;

/// <summary>
/// Accounts in PostgreSQL. The handle's uniqueness is the database's: a unique index on
/// <c>lower(handle)</c> refuses the second of two registrations however close together they come.
/// </summary>
internal sealed class EfAccountRepository(AccountsDbContext db) : IAccountRepository
{
    public Task<Account?> Find(Guid id, CancellationToken cancellationToken) =>
        db.Accounts.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    // The statement is the rule, so it is written as it runs: the same lower() the index is built on.
    public Task<Account?> FindByHandle(Handle handle, CancellationToken cancellationToken) =>
        db.Accounts.FromSql($"SELECT * FROM accounts.accounts WHERE lower(handle) = {handle.Normalized}")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Account>> FindMany(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);

    public Task<SaveOutcome> Add(Account account, CancellationToken cancellationToken)
    {
        db.Accounts.Add(account);
        return Save(cancellationToken);
    }

    public Task<SaveOutcome> Update(Account account, CancellationToken cancellationToken) => Save(cancellationToken);

    private async Task<SaveOutcome> Save(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return SaveOutcome.Saved;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
        {
            db.ChangeTracker.Clear();
            return unique.ConstraintName == AccountsDbContext.HandleIndex ? SaveOutcome.HandleTaken : SaveOutcome.AlreadyRegistered;
        }
    }
}
