using StackExchange.Redis;

using Timeline.Application;
using Timeline.Domain;

namespace Timeline.Infrastructure;

/// <summary>
/// Timelines as Valkey sorted sets, <c>tl:{account}</c>: member = the post id's 32 hex digits,
/// score = its UUIDv7 milliseconds. One script per timeline, so the existence check, the add and the
/// trim are one step no other command can come between.
/// </summary>
internal sealed class ValkeyTimelines(IConnectionMultiplexer valkey) : ITimelines
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
        var member = TimelineRules.Member(postId);
        // One round trip for the whole page: the scripts are pipelined, each still atomic on its own.
        var batch = valkey.GetDatabase().CreateBatch();
        var writes = owners
            .Select(owner => batch.ScriptEvaluateAsync(AddIfExists, new { key = Key(owner), score, member = (RedisValue)member, keep = TimelineRules.MaxEntries }))
            .ToList();
        batch.Execute();
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
        var batch = valkey.GetDatabase().CreateBatch();
        var writes = postIds
            .Select(post => batch.ScriptEvaluateAsync(AddIfExists, new
            {
                key = Key(owner),
                score = TimelineRules.Score(post),
                member = (RedisValue)TimelineRules.Member(post),
                keep = TimelineRules.MaxEntries,
            }))
            .ToList();
        batch.Execute();
        await Task.WhenAll(writes).WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken)
    {
        // Scores above 0 only: the sentinel sits at 0 and is never a post.
        var entries = await valkey.GetDatabase().SortedSetRangeByScoreWithScoresAsync(
            Key(owner), start: 0, stop: maxScore ?? double.PositiveInfinity, exclude: Exclude.Start,
            order: Order.Descending, skip: skip, take: count).WaitAsync(cancellationToken);
        return [.. entries.Select(e => new Entry(TimelineRules.PostOf(e.Element!), (long)e.Score))];
    }

    public Task Touch(Guid owner, CancellationToken cancellationToken) =>
        valkey.GetDatabase().KeyExpireAsync(Key(owner), TimelineRules.Expiry).WaitAsync(cancellationToken);
}
