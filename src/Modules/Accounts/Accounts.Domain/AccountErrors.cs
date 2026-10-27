using Social.BuildingBlocks.Domain;

namespace Accounts.Domain;

/// <summary>Every rule an account can break, with the code clients see.</summary>
public static class AccountErrors
{
    public static readonly Error HandleLength = Error.Validation("handle_length", "A handle has 3 to 30 characters.");
    public static readonly Error HandleCharacters = Error.Validation("handle_characters", "A handle has only letters, digits and underscores.");
    public static readonly Error DisplayNameLength = Error.Validation("display_name_length", "A display name has 1 to 50 characters.");
    public static readonly Error HandleTaken = Error.Conflict("handle_taken", "Another account has this handle, in some mix of upper and lower case.");
    public static readonly Error Deactivated = Error.Conflict("account_deactivated", "This account is deactivated; its profile cannot change.");
    public static readonly Error NotFound = Error.NotFound("account_not_found", "No account has this handle or id.");
}
