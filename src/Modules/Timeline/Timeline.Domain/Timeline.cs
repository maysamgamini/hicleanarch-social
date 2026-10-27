namespace Timeline.Domain;

/// <summary>
/// What a timeline holds and how it is bounded. A timeline is a sorted set of post ids, scored by
/// the time the post was written; it is derived data, rebuilt from Posts and Relationships at will.
/// </summary>
public static class TimelineRules
{
    /// <summary>A timeline keeps its newest 800 entries; older posts are read from Posts if ever needed.</summary>
    public const int MaxEntries = 800;

    /// <summary>
    /// A timeline nobody reads for this long expires; only a read refreshes it. Round 2 (s3-l08): 3 days,
    /// down from 7, so the timeline store holds the readers of the last three days.
    /// </summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(3);

    /// <summary>The member that makes an empty timeline exist, at score 0, below every post.</summary>
    public const string Sentinel = "_";

    /// <summary>
    /// A post id as 32 lower-case hex digits: the timeline's order among posts of the same millisecond,
    /// and the member form before s3-l08. Hex and big-endian bytes sort the same way.
    /// </summary>
    public static string Member(Guid postId) => postId.ToString("N");

    /// <summary>A post id as a member since s3-l08: its 16 bytes, big-endian, half the hex form's 32.</summary>
    public static byte[] MemberBytes(Guid postId) => postId.ToByteArray(bigEndian: true);

    /// <summary>A member in either form: 16 bytes (s3-l08 on) or 32 hex digits (written before, until it expires).</summary>
    public static Guid PostOf(ReadOnlySpan<byte> member) => member.Length switch
    {
        16 => new Guid(member, bigEndian: true),
        32 => Guid.ParseExact(System.Text.Encoding.ASCII.GetString(member), "N"),
        _ => throw new ArgumentException($"A timeline member is 16 bytes or 32 hex digits, not {member.Length} bytes.", nameof(member)),
    };

    /// <summary>
    /// The sorted-set size up to which Valkey keeps the compact listpack encoding (zset-max-listpack-entries).
    /// A timeline reaches 802 entries between an add and its trim (800, the new post, the sentinel), so
    /// 1,024 holds every timeline compact; a bulk add trims as it goes so it never passes it.
    /// </summary>
    public const int ListpackEntries = 1_024;

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
