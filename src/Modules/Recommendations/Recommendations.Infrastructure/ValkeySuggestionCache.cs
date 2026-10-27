using System.Text.Json;

using Recommendations.Application;
using Recommendations.Domain;

using StackExchange.Redis;

namespace Recommendations.Infrastructure;

/// <summary>
/// Computed lists in Valkey, under Recommendations' own prefix: rec:sugg:{reader}:{kind}, one hour.
/// A cache that cannot be reached is a miss: suggestions are computed again, never refused.
/// </summary>
internal sealed class ValkeySuggestionCache(IConnectionMultiplexer valkey) : ISuggestionCache
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RedisKey Key(Guid reader, string kind) => $"rec:sugg:{reader:N}:{kind}";

    public async Task<IReadOnlyList<Suggestion>?> Get(Guid reader, string kind, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await valkey.GetDatabase().StringGetAsync(Key(reader, kind)).WaitAsync(cancellationToken);
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
}
