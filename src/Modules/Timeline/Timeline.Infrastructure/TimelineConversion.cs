using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using Timeline.Domain;

namespace Timeline.Infrastructure;

/// <summary>
/// Converts the timelines written before s3-l08 to the compact encoding, once per worker start. Valkey
/// 8.1 turns a sorted set back into a listpack only in a *STORE command, so a key that grew past the
/// old limit stays a skiplist until it is stored again: ZUNIONSTORE onto itself, after trimming it to
/// what a timeline keeps, with its expiry put back (a store drops it). Keys already compact are skipped.
/// </summary>
internal sealed partial class TimelineConversion(IConnectionMultiplexer valkey, ILogger<TimelineConversion> logger) : BackgroundService
{
    private const string ConvertOne = """
        if redis.call('OBJECT', 'ENCODING', KEYS[1]) ~= 'skiplist' then
          return 0
        end
        redis.call('ZREMRANGEBYRANK', KEYS[1], 0, -(tonumber(ARGV[1]) + 1))
        local ttl = redis.call('PTTL', KEYS[1])
        redis.call('ZUNIONSTORE', KEYS[1], 1, KEYS[1])
        if ttl > 0 then
          redis.call('PEXPIRE', KEYS[1], ttl)
        end
        return 1
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var converted = await ConvertAll(valkey, stoppingToken);
            LogConverted(logger, converted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(logger, e); // the next start tries again; reads work in either encoding
        }
    }

    /// <summary>Converts every skiplist timeline; how many it converted.</summary>
    public static async Task<int> ConvertAll(IConnectionMultiplexer valkey, CancellationToken cancellationToken)
    {
        var converted = 0;
        foreach (var server in valkey.GetServers().Where(s => !s.IsReplica))
        {
            await foreach (var key in server.KeysAsync(pattern: "tl:*", pageSize: 1_000).WithCancellation(cancellationToken))
            {
                // Trimmed to MaxEntries, as every add trims.
                converted += (int)await valkey.GetDatabase().ScriptEvaluateAsync(ConvertOne, [key], [TimelineRules.MaxEntries]);
            }
        }

        return converted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Converted {Count} timelines to the compact encoding")]
    private static partial void LogConverted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Converting timelines to the compact encoding failed; the next start tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
