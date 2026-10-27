namespace Timeline.Domain;

/// <summary>
/// What a timeline holds and how it is bounded. A timeline is a sorted set of post ids, scored by
/// the time the post was written; it is derived data, rebuilt from Posts and Relationships at will.
/// </summary>
public static class TimelineRules
{
    /// <summary>A timeline keeps its newest 800 entries; older posts are read from Posts if ever needed.</summary>
    public const int MaxEntries = 800;

    /// <summary>A timeline nobody reads for this long expires; only a read refreshes it.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(7);

    /// <summary>The member that makes an empty timeline exist, at score 0, below every post.</summary>
    public const string Sentinel = "_";

    /// <summary>A post id as a member: its 32 lower-case hex digits.</summary>
    public static string Member(Guid postId) => postId.ToString("N");

    public static Guid PostOf(string member) => Guid.ParseExact(member, "N");

    /// <summary>
    /// A post's score: the Unix milliseconds in the first 48 bits of its UUIDv7 id, the moment it was
    /// written. A late delivery lands at its time, not its arrival. 48 bits fit a sorted set's
    /// double exactly (2^48 &lt; 2^53).
    /// </summary>
    public static long Score(Guid postId)
    {
        if (postId.Version != 7)
        {
            throw new ArgumentException("A post id is a UUIDv7.", nameof(postId));
        }

        return Convert.ToInt64(postId.ToString("N")[..12], 16);
    }
}
