using System.Text.Json;

using Recommendations.Application;
using Recommendations.Domain;

using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Infrastructure.Valkey;

using StackExchange.Redis;

namespace Recommendations.Infrastructure;

/// <summary>
/// Computed lists in Valkey, under Recommendations' own prefix: rec:sugg:{reader}:{kind}, one hour,
/// in the short-lived store (s3-l08).
/// A cache that cannot be reached is a miss: suggestions are computed again, never refused.
/// </summary>
internal sealed class ValkeySuggestionCache([FromKeyedServices(ValkeyRegistration.ShortLived)] IConnectionMultiplexer valkey) : ISuggestionCache
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RedisKey Key(Guid reader, string kind) => $"rec:sugg:{reader:N}:{kind}";

    /// <summary>A reader's precomputed lists, one hash: a field per kind, "at" (Unix ms) and "changes".</summary>
    public static RedisKey PrecomputedKey(Guid reader) => $"rec:pre:{reader:N}";

    /// <summary>Who opened suggestions when: a sorted set scored by Unix milliseconds.</summary>
    public static readonly RedisKey OpenedKey = "rec:opened";

    /// <summary>A precomputed list outlives its 24-hour refresh by an hour, so a late pass never leaves a reader without one.</summary>
    public static readonly TimeSpan PrecomputedKeep = TimeSpan.FromHours(25);

    public async Task<IReadOnlyList<Suggestion>?> Get(Guid reader, string kind, CancellationToken cancellationToken)
    {
        try
        {
            var precomputed = await valkey.GetDatabase().HashGetAsync(PrecomputedKey(reader), kind).WaitAsync(cancellationToken);
            var stored = precomputed.IsNull ? await valkey.GetDatabase().StringGetAsync(Key(reader, kind)).WaitAsync(cancellationToken) : precomputed;
            return stored.IsNull ? null : JsonSerializer.Deserialize<List<Suggestion>>(stored.ToString(), Json);
        }
        catch (RedisException)
        {
            return null;
        }
    }

    public async Task Set(Guid reader, string kind, IReadOnlyList<Suggestion> suggestions, CancellationToken cancellationToken)
    {
        try
        {
            await valkey.GetDatabase().StringSetAsync(Key(reader, kind), JsonSerializer.Serialize(suggestions, Json), Keep).WaitAsync(cancellationToken);
        }
        catch (RedisException)
        {
            // the next request computes the list again
        }
    }

    public Task Drop(Guid reader, CancellationToken cancellationToken) =>
        valkey.GetDatabase().KeyDeleteAsync([Key(reader, SuggestionKind.WhoToFollow), Key(reader, SuggestionKind.PeopleYouMayKnow)]).WaitAsync(cancellationToken);

    public Task Opened(Guid reader, DateTimeOffset at, CancellationToken cancellationToken) =>
        valkey.GetDatabase().SortedSetAddAsync(OpenedKey, reader.ToString("N"), at.ToUnixTimeMilliseconds()).WaitAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> OpenedSince(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        var db = valkey.GetDatabase();
        await db.SortedSetRemoveRangeByScoreAsync(OpenedKey, double.NegativeInfinity, since.ToUnixTimeMilliseconds(), Exclude.Stop).WaitAsync(cancellationToken);
        var readers = await db.SortedSetRangeByScoreAsync(OpenedKey, since.ToUnixTimeMilliseconds(), double.PositiveInfinity, take: limit).WaitAsync(cancellationToken);
        return [.. readers.Select(r => Guid.ParseExact(r.ToString(), "N"))];
    }

    public async Task<(DateTimeOffset At, long Changes)?> Precomputed(Guid reader, CancellationToken cancellationToken)
    {
        var fields = await valkey.GetDatabase().HashGetAsync(PrecomputedKey(reader), ["at", "changes"]).WaitAsync(cancellationToken);
        return fields[0].IsNull ? null : (DateTimeOffset.FromUnixTimeMilliseconds((long)fields[0]), fields[1].IsNull ? 0 : (long)fields[1]);
    }

    public async Task SetPrecomputed(Guid reader, IReadOnlyDictionary<string, IReadOnlyList<Suggestion>> lists, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var db = valkey.GetDatabase();
        var tx = db.CreateTransaction();
        _ = tx.KeyDeleteAsync(PrecomputedKey(reader));
        _ = tx.HashSetAsync(PrecomputedKey(reader),
            [.. lists.Select(l => new HashEntry(l.Key, JsonSerializer.Serialize(l.Value, Json))), new HashEntry("at", at.ToUnixTimeMilliseconds()), new HashEntry("changes", 0)]);
        _ = tx.KeyExpireAsync(PrecomputedKey(reader), PrecomputedKeep);
        await tx.ExecuteAsync().WaitAsync(cancellationToken);
    }

    public async Task<bool> CountChange(Guid reader, CancellationToken cancellationToken)
    {
        var db = valkey.GetDatabase();
        // Only an existing hash counts: HINCRBY would create one with no lists.
        return (int)await db.ScriptEvaluateAsync("""
            if redis.call('EXISTS', KEYS[1]) == 1 then
              redis.call('HINCRBY', KEYS[1], 'changes', 1)
              return 1
            end
            return 0
            """, [PrecomputedKey(reader)]).WaitAsync(cancellationToken) == 1;
    }
}
