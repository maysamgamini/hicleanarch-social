using Accounts.Domain;

namespace Accounts.Application;

/// <summary>How a save ended: stored, or refused by a rule only the store can see.</summary>
public enum SaveOutcome
{
    Saved,

    /// <summary>Another account holds the handle, in some mix of case.</summary>
    HandleTaken,

    /// <summary>An account with this id was registered in the meantime.</summary>
    AlreadyRegistered,
}

/// <summary>Where accounts are kept. The application says what it needs; Infrastructure decides how.</summary>
public interface IAccountRepository
{
    Task<Account?> Find(Guid id, CancellationToken cancellationToken);

    Task<Account?> FindByHandle(Handle handle, CancellationToken cancellationToken);

    Task<IReadOnlyList<Account>> FindMany(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Stores a new account, unless its handle or its id is already taken.</summary>
    Task<SaveOutcome> Add(Account account, CancellationToken cancellationToken);

    /// <summary>Stores a changed profile, unless another account holds the new handle.</summary>
    Task<SaveOutcome> Update(Account account, CancellationToken cancellationToken);
}

/// <summary>
/// Profiles on the read side (s3-l11): the Aurora reader endpoint, a moment behind the writer. The
/// feed's hydration and a profile page read here; registering and editing read the writer.
/// </summary>
public interface IProfileReads
{
    Task<Account?> FindByHandle(Handle handle, CancellationToken cancellationToken);

    Task<IReadOnlyList<Account>> FindMany(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
