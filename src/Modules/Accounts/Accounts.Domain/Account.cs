using Social.BuildingBlocks.Domain;

namespace Accounts.Domain;

public enum AccountStatus
{
    Active,
    Deactivated,
}

/// <summary>
/// A person's profile. Its id is the identity provider's subject for that person, so an account
/// exists only for someone who has signed in. The handle's uniqueness spans every account, so the
/// store enforces it; everything else is decided here.
/// </summary>
public sealed class Account
{
    public const int DisplayNameMaxLength = 50;

    private Account(Guid id, Handle handle, string displayName, AccountStatus status, DateTimeOffset registeredAt, DateTimeOffset updatedAt)
    {
        Id = id;
        Handle = handle;
        DisplayName = displayName;
        Status = status;
        RegisteredAt = registeredAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }

    public Handle Handle { get; private set; }

    public string DisplayName { get; private set; }

    public AccountStatus Status { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<Account> Register(Guid id, string? handle, string? displayName, DateTimeOffset now)
    {
        var checkedHandle = Handle.Create(handle);
        var checkedName = CheckDisplayName(displayName);
        var errors = checkedHandle.Errors.Concat(checkedName.Errors).ToList();
        return errors.Count > 0
            ? Result.Failure<Account>(errors)
            : Result.Success(new Account(id, checkedHandle.Value, checkedName.Value, AccountStatus.Active, now, now));
    }

    /// <summary>Rebuilds an account from storage; the rules were checked when it was written.</summary>
    public static Account Restore(Guid id, Handle handle, string displayName, AccountStatus status, DateTimeOffset registeredAt, DateTimeOffset updatedAt) =>
        new(id, handle, displayName, status, registeredAt, updatedAt);

    public Result<Account> UpdateProfile(string? handle, string? displayName, DateTimeOffset now)
    {
        var checkedHandle = Handle.Create(handle);
        var checkedName = CheckDisplayName(displayName);
        var errors = checkedHandle.Errors.Concat(checkedName.Errors).ToList();
        if (errors.Count > 0)
        {
            return Result.Failure<Account>(errors);
        }

        Handle = checkedHandle.Value;
        DisplayName = checkedName.Value;
        UpdatedAt = now;
        return Result.Success(this);
    }

    private static Result<string> CheckDisplayName(string? displayName)
    {
        var trimmed = displayName?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > DisplayNameMaxLength
            ? Result.Failure<string>(AccountErrors.DisplayNameLength)
            : Result.Success(trimmed);
    }
}
