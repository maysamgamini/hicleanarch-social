using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Infrastructure.Valkey;

using StackExchange.Redis;

using Timeline.Application;
using Timeline.Domain;

namespace Timeline.Infrastructure;

/// <summary>
/// The registry of pulled authors as one Valkey hash, <c>fanout:registry</c>: field = the author's id,
/// value = promoted-at and demoted-at in Unix milliseconds ("1730000000000:" while pulled). A hash and
/// not a set, because the push and pull overlaps need the two times. It holds a few thousand authors
/// at most, so a read takes it whole.
/// Each reader's pull list as a string, <c>pulls:{reader}</c>, the ids joined by commas, with a
/// 10-minute expiry; an empty string is a cached "none". Both live in the short-lived store (s3-l08).
/// </summary>
internal sealed class ValkeyPulls([FromKeyedServices(ValkeyRegistration.ShortLived)] IConnectionMultiplexer valkey) : IPullRegistry, IPullLists
{
    public const string RegistryKey = "fanout:registry";

    public static RedisKey PullsKey(Guid reader) => "pulls:" + reader.ToString("N");

    private IDatabase Db => valkey.GetDatabase();

    public async Task<PulledAuthor?> Find(Guid author, CancellationToken cancellationToken)
    {
        var value = await Db.HashGetAsync(RegistryKey, author.ToString("N")).WaitAsync(cancellationToken);
        return value.IsNull ? null : Parse(author, value!);
    }

    public async Task<IReadOnlyList<PulledAuthor>> All(CancellationToken cancellationToken) =>
        [.. (await Db.HashGetAllAsync(RegistryKey).WaitAsync(cancellationToken)).Select(e => Parse(Guid.ParseExact(e.Name!, "N"), e.Value!))];

    public Task Save(PulledAuthor entry, CancellationToken cancellationToken) =>
        Db.HashSetAsync(RegistryKey, entry.AuthorId.ToString("N"),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{entry.PromotedAt.ToUnixTimeMilliseconds()}:{entry.DemotedAt?.ToUnixTimeMilliseconds()}")).WaitAsync(cancellationToken);

    public Task Remove(Guid author, CancellationToken cancellationToken) =>
        Db.HashDeleteAsync(RegistryKey, author.ToString("N")).WaitAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>?> Get(Guid reader, CancellationToken cancellationToken)
    {
        var value = await Db.StringGetAsync(PullsKey(reader)).WaitAsync(cancellationToken);
        return value.IsNull ? null : [.. ((string)value!).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => Guid.ParseExact(id, "N"))];
    }

    public Task Set(Guid reader, IReadOnlyList<Guid> authors, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Db.StringSetAsync(PullsKey(reader), string.Join(',', authors.Select(a => a.ToString("N"))), lifetime).WaitAsync(cancellationToken);

    private static PulledAuthor Parse(Guid author, string value)
    {
        var parts = value.Split(':');
        return new PulledAuthor(author, DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)),
            parts[1].Length == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
    }
}
