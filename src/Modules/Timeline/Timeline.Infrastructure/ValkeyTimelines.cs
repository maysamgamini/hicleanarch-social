using StackExchange.Redis;

using Timeline.Application;
using Timeline.Domain;

namespace Timeline.Infrastructure;

/// <summary>
/// Timelines as Valkey sorted sets, <c>tl:{account}</c>: member = the post id's 16 bytes (32 hex digits
/// before s3-l08; reads take both until the old ones expire), score = its UUIDv7 milliseconds. One script per timeline, so the existence check, the add and the
/// trim are one step no other command can come between.
/// </summary>
internal sealed class ValkeyTimelines(IConnectionMultiplexer valkey, FanoutPace pace) : ITimelines
{
    // Add only to a timeline that exists, then keep the newest MaxEntries. A fanout never creates a
    // timeline: one is created by its reader's first read, which fills it from Posts. The sentinel
    // sits at score 0, below every post, so it is the first entry a full timeline lets go.
    private static readonly LuaScript AddIfExists = LuaScript.Prepare("""
        if redis.call('EXISTS', @key) == 1 then
          redis.call('ZADD', @key, @score, @member)
          redis.call('ZREMRANGEBYRANK', @key, 0, -(tonumber(@keep) + 1))
          return 1
        end
        return 0
        """);

    // A bulk add (a rebuild, a follow's backfill), only to a timeline that exists, trimmed every 200 adds.
    private const string BulkAddIfExists = """
        if redis.call('EXISTS', KEYS[1]) == 0 then
          return 0
        end
        local keep = tonumber(ARGV[1])
        local added = 0
        for i = 2, #ARGV, 2 do
          redis.call('ZADD', KEYS[1], ARGV[i], ARGV[i + 1])
          added = added + 1
          if added % 200 == 0 then
            redis.call('ZREMRANGEBYRANK', KEYS[1], 0, -(keep + 1))
          end
        end
        redis.call('ZREMRANGEBYRANK', KEYS[1], 0, -(keep + 1))
        return 1
        """;

    // Creates the timeline with only its sentinel, and its expiry, unless it exists: 1 when created.
    private static readonly LuaScript CreateIfMissing = LuaScript.Prepare("""
        if redis.call('EXISTS', @key) == 1 then
          return 0
        end
        redis.call('ZADD', @key, 0, @sentinel)
        redis.call('PEXPIRE', @key, @expiry)
        return 1
        """);

    public static RedisKey Key(Guid owner) => "tl:" + owner.ToString("N");

    public async Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken)
    {
        if (owners.Count == 0)
        {
            return 0;
        }

        var score = TimelineRules.Score(postId);
        var member = TimelineRules.MemberBytes(postId);
        // One round trip for the whole page: the scripts are pipelined, each still atomic on its own.
        var batch = valkey.GetDatabase().CreateBatch();
        var writes = owners
            .Select(owner => batch.ScriptEvaluateAsync(AddIfExists, new { key = Key(owner), score, member = (RedisValue)member, keep = TimelineRules.MaxEntries }))
            .ToList();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        batch.Execute();
        // The first reply is one round trip: how slow Valkey is right now, whatever the page's size.
        await Task.WhenAny(writes).WaitAsync(cancellationToken);
        pace.Written(System.Diagnostics.Stopwatch.GetElapsedTime(started));
        var results = await Task.WhenAll(writes).WaitAsync(cancellationToken);
        return results.Count(r => (int)r == 1);
    }

    public Task<bool> Exists(Guid owner, CancellationToken cancellationToken) =>
        valkey.GetDatabase().KeyExistsAsync(Key(owner)).WaitAsync(cancellationToken);

    public async Task<bool> CreateEmpty(Guid owner, CancellationToken cancellationToken) =>
        (int)await valkey.GetDatabase().ScriptEvaluateAsync(CreateIfMissing, new
        {
            key = Key(owner),
            sentinel = (RedisValue)TimelineRules.Sentinel,
            expiry = (long)TimelineRules.Expiry.TotalMilliseconds,
        }).WaitAsync(cancellationToken) == 1;

    public async Task Fill(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken)
    {
        // Only what can stay: the newest MaxEntries of the input. One script, which trims every 200 adds,
        // so the key never passes 1,024 entries (800 + 1 + 200) and keeps its listpack encoding.
        var input = postIds.Distinct().OrderByDescending(TimelineRules.Score).Take(TimelineRules.MaxEntries).ToList();
        if (input.Count == 0)
        {
            return;
        }

        var values = new RedisValue[1 + (2 * input.Count)];
        values[0] = TimelineRules.MaxEntries;
        for (var i = 0; i < input.Count; i++)
        {
            values[1 + (2 * i)] = TimelineRules.Score(input[i]);
            values[2 + (2 * i)] = TimelineRules.MemberBytes(input[i]);
        }

        await valkey.GetDatabase().ScriptEvaluateAsync(BulkAddIfExists, [Key(owner)], values).WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken)
    {
        // Scores above 0 only: the sentinel sits at 0 and is never a post.
        var entries = await valkey.GetDatabase().SortedSetRangeByScoreWithScoresAsync(
            Key(owner), start: 0, stop: maxScore ?? double.PositiveInfinity, exclude: Exclude.Start,
            order: Order.Descending, skip: skip, take: count).WaitAsync(cancellationToken);
        return [.. entries.Select(e => new Entry(TimelineRules.PostOf((byte[])e.Element!), (long)e.Score))];
    }

    public Task Touch(Guid owner, CancellationToken cancellationToken) =>
        valkey.GetDatabase().KeyExpireAsync(Key(owner), TimelineRules.Expiry).WaitAsync(cancellationToken);

    public async Task Remove(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken)
    {
        if (postIds.Count > 0)
        {
            // Both forms: an entry written before s3-l08 is still hex until its timeline expires.
            await valkey.GetDatabase().SortedSetRemoveAsync(Key(owner),
                [.. postIds.SelectMany(p => new RedisValue[] { TimelineRules.MemberBytes(p), TimelineRules.Member(p) })]).WaitAsync(cancellationToken);
        }
    }
}
