using StackExchange.Redis;

namespace Integration.Tests;

/// <summary>Direct access to the test's Valkey, for what a test sets up or checks behind the API's back.</summary>
public sealed class Timelines(string configuration) : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _valkey = ConnectionMultiplexer.Connect(configuration);

    public IDatabase Db => _valkey.GetDatabase();

    public static RedisKey Key(Guid owner) => "tl:" + owner.ToString("N");

    /// <summary>A post's member as the program writes it since s3-l08: its 16 bytes, big-endian.</summary>
    public static RedisValue Member(Guid post) => post.ToByteArray(bigEndian: true);

    /// <summary>The member form before s3-l08: 32 hex digits.</summary>
    public static RedisValue HexMember(Guid post) => post.ToString("N");

    /// <summary>A member read back, in either form; null for the sentinel.</summary>
    public static Guid? PostOf(RedisValue member) => ((byte[])member!).Length switch
    {
        16 => new Guid((byte[])member!, bigEndian: true),
        32 => Guid.ParseExact(member.ToString(), "N"),
        _ => null,
    };

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
