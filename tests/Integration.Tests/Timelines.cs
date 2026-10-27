using StackExchange.Redis;

namespace Integration.Tests;

/// <summary>Direct access to the test's Valkey, for what a test sets up or checks behind the API's back.</summary>
public sealed class Timelines(string configuration) : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _valkey = ConnectionMultiplexer.Connect(configuration);

    public IDatabase Db => _valkey.GetDatabase();

    public static RedisKey Key(Guid owner) => "tl:" + owner.ToString("N");

    public static string Member(Guid post) => post.ToString("N");

    /// <summary>Makes timelines exist, empty but for the sentinel, as a reader's first read would.</summary>
    public async Task Create(IEnumerable<Guid> owners)
    {
        var batch = Db.CreateBatch();
        var writes = owners.Select(o => batch.SortedSetAddAsync(Key(o), "_", 0)).ToList();
        batch.Execute();
        await Task.WhenAll(writes);
    }

    public async Task<bool> Holds(Guid owner, Guid post) => await Db.SortedSetScoreAsync(Key(owner), Member(post)) is not null;

    /// <summary>How many of these timelines hold the post, in one round trip.</summary>
    public async Task<int> HoldingCount(IReadOnlyCollection<Guid> owners, Guid post)
    {
        var batch = Db.CreateBatch();
        var reads = owners.Select(o => batch.SortedSetScoreAsync(Key(o), Member(post))).ToList();
        batch.Execute();
        return (await Task.WhenAll(reads)).Count(s => s is not null);
    }

    public ValueTask DisposeAsync() => _valkey.DisposeAsync();
}
