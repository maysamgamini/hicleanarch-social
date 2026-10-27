using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

using Timeline.Application;

namespace Integration.Tests;

/// <summary>Timelines that fit in memory: 16-byte members, the compact encoding, and its conversion.</summary>
public sealed class TimelineStoreTests(Postgres postgres) : IntegrationTest(postgres)
{
    private readonly Timelines _timelines = new(postgres.Valkey);

    public override async ValueTask DisposeAsync()
    {
        await _timelines.DisposeAsync();
        await base.DisposeAsync();
    }

    private static long Score(Guid post) => Convert.ToInt64(post.ToString("N")[..12], 16);

    private async Task<string> Encoding(Guid owner) =>
        (await _timelines.Db.ExecuteAsync("OBJECT", "ENCODING", Timelines.Key(owner).ToString())).ToString();

    [Fact]
    public async Task A_timeline_written_in_the_old_form_and_one_in_the_new_form_both_read_correctly()
    {
        var host = StartHost();
        var port = host.Services.GetRequiredService<ITimelines>();
        var (old, current) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var posts = Enumerable.Range(0, 5).Select(i => Guid.CreateVersion7(now.AddSeconds(i))).ToList();
        await _timelines.Create([old, current]);
        // Before s3-l08: 32 hex digits.
        foreach (var post in posts)
        {
            await _timelines.Db.SortedSetAddAsync(Timelines.Key(old), Timelines.HexMember(post), Score(post));
        }

        foreach (var post in posts)
        {
            await port.AddToExisting([current], post, Ct);
        }

        var newestFirst = posts.AsEnumerable().Reverse().ToList();
        Assert.Equal(newestFirst, (await port.Read(old, null, 0, 10, Ct)).Select(e => e.PostId));
        Assert.Equal(newestFirst, (await port.Read(current, null, 0, 10, Ct)).Select(e => e.PostId));
        Assert.All(await _timelines.Db.SortedSetRangeByScoreAsync(Timelines.Key(current), start: 1), m => Assert.Equal(16, ((byte[])m!).Length));

        // A removal finds the post in either form.
        await port.Remove(old, [posts[0]], Ct);
        await port.Remove(current, [posts[0]], Ct);
        Assert.Equal(4, (await port.Read(old, null, 0, 10, Ct)).Count);
        Assert.Equal(4, (await port.Read(current, null, 0, 10, Ct)).Count);
    }

    [Fact]
    public async Task After_the_conversion_every_timeline_is_compact_including_one_that_was_a_skiplist()
    {
        var (big, small) = (Guid.NewGuid(), Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        // Written as the old program could leave it: 1,100 hex entries, past the listpack limit.
        await _timelines.Create([big, small]);
        await _timelines.Db.SortedSetAddAsync(Timelines.Key(big),
            [.. Enumerable.Range(0, 1_100).Select(i => Guid.CreateVersion7(now.AddMilliseconds(-i))).Select(p => new SortedSetEntry(Timelines.HexMember(p), Score(p)))]);
        await _timelines.Db.KeyExpireAsync(Timelines.Key(big), TimeSpan.FromDays(2));
        await _timelines.Db.SortedSetAddAsync(Timelines.Key(small), Timelines.HexMember(Guid.CreateVersion7(now)), Score(Guid.CreateVersion7(now)));
        Assert.Equal("skiplist", await Encoding(big));

        _ = StartHost().Services; // a worker start runs the conversion

        await Eventually(async () => await Encoding(big) == "listpack");
        Assert.Equal("listpack", await Encoding(small));
        Assert.Equal(800, await _timelines.Db.SortedSetLengthAsync(Timelines.Key(big))); // trimmed to what a timeline keeps
        Assert.InRange((await _timelines.Db.KeyTimeToLiveAsync(Timelines.Key(big)))!.Value, TimeSpan.FromDays(1.9), TimeSpan.FromDays(2)); // the expiry kept
    }

    [Fact]
    public async Task A_rebuild_of_1500_posts_never_leaves_a_key_above_1024_entries()
    {
        var host = StartHost();
        var port = host.Services.GetRequiredService<ITimelines>();
        var reader = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var posts = Enumerable.Range(0, 1_500).Select(i => Guid.CreateVersion7(now.AddMilliseconds(-i))).ToList();
        await port.CreateEmpty(reader, Ct);

        await port.Fill(reader, posts, Ct);

        // A key that had passed 1,024 entries would still be a skiplist: Valkey does not convert back on a trim.
        Assert.Equal("listpack", await Encoding(reader));
        Assert.Equal(800, await _timelines.Db.SortedSetLengthAsync(Timelines.Key(reader)));
        Assert.Equal(posts.Take(800), (await port.Read(reader, null, 0, 800, Ct)).Select(e => e.PostId));
    }
}
