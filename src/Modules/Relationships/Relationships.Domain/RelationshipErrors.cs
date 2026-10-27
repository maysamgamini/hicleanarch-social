using Social.BuildingBlocks.Domain;

namespace Relationships.Domain;

/// <summary>Every rule a relationship change can break, with the code clients see.</summary>
public static class RelationshipErrors
{
    public static readonly Error CannotFollowSelf = Error.Validation("cannot_follow_self", "An account cannot follow itself.");
    public static readonly Error FollowLimit = Error.Conflict("follow_limit", "An account follows at most 5,000 accounts.");
    public static readonly Error CannotBlockSelf = Error.Validation("cannot_block_self", "An account cannot block itself.");
    public static readonly Error CannotMuteSelf = Error.Validation("cannot_mute_self", "An account cannot mute itself.");
    public static readonly Error Blocked = Error.Conflict("blocked", "One of the two accounts has blocked the other.");
    public static readonly Error AccountNotFound = Error.NotFound("account_not_found", "No active account has this id.");
}
