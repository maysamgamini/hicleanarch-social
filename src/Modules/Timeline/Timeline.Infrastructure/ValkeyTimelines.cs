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
}
