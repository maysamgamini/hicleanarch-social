using Accounts.Application;
using Accounts.Domain;

namespace Accounts.Infrastructure;

/// <summary>
/// Accounts kept in the process, for this lesson only: one lock guards both the accounts and the
/// handle index, so two registrations of one handle cannot both succeed. Stored as snapshots, so a
/// refused change never leaks into what is kept.
/// </summary>
internal sealed class InMemoryAccountRepository : IAccountRepository
{
    private sealed record Row(Guid Id, string Handle, string DisplayName, AccountStatus Status, DateTimeOffset RegisteredAt, DateTimeOffset UpdatedAt);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Row> _byId = [];
    private readonly Dictionary<string, Guid> _byHandle = [];

    public Task<Account?> Find(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_byId.TryGetValue(id, out var row) ? Restore(row) : null);
        }
    }

    public Task<Account?> FindByHandle(Handle handle, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_byHandle.TryGetValue(handle.Normalized, out var id) ? Restore(_byId[id]) : null);
        }
    }

    public Task<IReadOnlyList<Account>> FindMany(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Account>>([.. ids.Where(_byId.ContainsKey).Select(id => Restore(_byId[id]))]);
        }
    }

    public Task<SaveOutcome> Add(Account account, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(account.Id))
            {
                return Task.FromResult(SaveOutcome.AlreadyRegistered);
            }

            if (_byHandle.ContainsKey(account.Handle.Normalized))
            {
                return Task.FromResult(SaveOutcome.HandleTaken);
            }

            _byId[account.Id] = ToRow(account);
            _byHandle[account.Handle.Normalized] = account.Id;
            return Task.FromResult(SaveOutcome.Saved);
        }
    }

    public Task<SaveOutcome> Update(Account account, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_byHandle.TryGetValue(account.Handle.Normalized, out var holder) && holder != account.Id)
            {
                return Task.FromResult(SaveOutcome.HandleTaken);
            }

            _byHandle.Remove(Handle.FromStored(_byId[account.Id].Handle).Normalized);
            _byId[account.Id] = ToRow(account);
            _byHandle[account.Handle.Normalized] = account.Id;
            return Task.FromResult(SaveOutcome.Saved);
        }
    }

    private static Row ToRow(Account a) => new(a.Id, a.Handle.Value, a.DisplayName, a.Status, a.RegisteredAt, a.UpdatedAt);

    private static Account Restore(Row r) =>
        Account.Restore(r.Id, Handle.FromStored(r.Handle), r.DisplayName, r.Status, r.RegisteredAt, r.UpdatedAt);
}
