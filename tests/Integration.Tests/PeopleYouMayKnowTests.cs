using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Recommendations.Application;
using Recommendations.Domain;
using Recommendations.Infrastructure.Graph;

using StackExchange.Redis;

namespace Integration.Tests;

public sealed class PeopleYouMayKnowTests(Postgres postgres) : IntegrationTest(postgres)
{
    /// <summary>Counts the walks: a cached list costs none.</summary>
    private sealed class CountingReads(IGraphReads inner) : IGraphReads
    {
        public int Walks;

        public Task<IReadOnlyList<Suggestion>> WhoToFollow(Guid reader, TraversalLimits limits, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Walks);
            return inner.WhoToFollow(reader, limits, cancellationToken);
        }

        public Task<IReadOnlyList<Suggestion>> FriendsOfFriends(Guid reader, TraversalLimits limits, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Walks);
            return inner.FriendsOfFriends(reader, limits, cancellationToken);
        }

        public Task<IReadOnlyList<Suggestion>> FollowersNotFollowedBack(Guid reader, int limit, TraversalLimits limits, CancellationToken cancellationToken) =>
            inner.FollowersNotFollowedBack(reader, limit, limits, cancellationToken);

        public Task<IReadOnlySet<Guid>> HiddenNow(Guid reader, IReadOnlyList<Guid> accounts, CancellationToken cancellationToken) =>
            inner.HiddenNow(reader, accounts, cancellationToken);
    }

    private CountingReads _reads = null!;
    private WebApplicationFactory<Program> _host = null!;
    private People _people = null!;
    private IDatabase _valkey = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _host = StartHost(services => services.AddSingleton<IGraphReads>(sp => _reads = new CountingReads(sp.GetRequiredService<Neo4jGraph>())));
        _people = new People(_host);
        _valkey = (await ConnectionMultiplexer.ConnectAsync(Db.ValkeyShort)).GetDatabase();
    }

    private async Task<List<Guid>> Pymk(Guid reader) =>
        [.. (await _people.As(reader).GetFromJsonAsync<JsonElement>("/v1/suggestions/people-you-may-know", Ct)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("account_id").GetGuid())];

    private static RedisKey Cached(Guid reader) => $"rec:sugg:{reader:N}:{SuggestionKind.PeopleYouMayKnow}";

    /// <summary>Me and a friend (mutual follows), the friend's friend, and a fan of mine I do not follow back.</summary>
    private async Task<(Guid Me, Guid Friend, Guid FriendOfFriend, Guid Fan)> Circle()
    {
        var (me, friend, fof, fan) = (await _people.SignUp(), await _people.SignUp(), await _people.SignUp(), await _people.SignUp());
        foreach (var (a, b) in new[] { (me, friend), (friend, me), (friend, fof), (fof, friend), (fan, me) })
        {
            await _people.Follow(a, b);
        }

        await Eventually(async () => (await Pymk(me)).Count == 2 || await Retry(me), seconds: 30);
        return (me, friend, fof, fan);
    }

    // Until the graph has every edge, a list computed early stays cached: drop it and ask again.
    private async Task<bool> Retry(Guid me)
    {
        await _valkey.KeyDeleteAsync(Cached(me));
        return false;
    }

    [Fact]
    public async Task Followers_not_followed_back_come_first_then_friends_of_friends()
    {
        var (me, _, fof, fan) = await Circle();

        Assert.Equal([fan, fof], await Pymk(me));
    }

    [Fact]
    public async Task The_endpoint_answers_from_the_cache_on_the_second_call()
    {
        var (me, _, _, _) = await Circle();
        await _valkey.KeyDeleteAsync(Cached(me));

        await Pymk(me);
        var walks = _reads.Walks;
        await Pymk(me);

        Assert.Equal(walks, _reads.Walks);
        Assert.InRange((await _valkey.KeyTimeToLiveAsync(Cached(me)))!.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task A_cached_list_never_shows_someone_who_blocked_the_reader_after_it_was_cached_or_whom_the_reader_blocked()
    {
        var (me, _, fof, fan) = await Circle();
        var walks = _reads.Walks;

        // The blocks reach the graph, but not (yet) the event handler that would drop the cache.
        var projection = _host.Services.GetRequiredService<IGraphProjection>();
        await projection.Apply(new EdgeChange(EdgeKind.Blocks, fof, me, 1_000, true, DateTimeOffset.UtcNow), Ct);
        await projection.Apply(new EdgeChange(EdgeKind.Blocks, me, fan, 1_001, true, DateTimeOffset.UtcNow), Ct);

        Assert.Empty(await Pymk(me));
        Assert.Equal(walks, _reads.Walks); // served from the cache, filtered when served
    }

    [Fact]
    public async Task A_block_drops_both_peoples_cached_lists()
    {
        var (me, _, fof, _) = await Circle();
        await Pymk(fof);
        Assert.True(await _valkey.KeyExistsAsync(Cached(fof)));

        await _people.As(fof).PutAsync($"/v1/accounts/{me}/block", null, Ct);

        await Eventually(async () => !await _valkey.KeyExistsAsync(Cached(me)) && !await _valkey.KeyExistsAsync(Cached(fof)));
    }

    [Fact]
    public async Task A_follow_or_a_dismiss_drops_the_actors_cached_list()
    {
        var (me, _, fof, fan) = await Circle();

        await _people.Follow(me, fan);
        await Eventually(async () => !await _valkey.KeyExistsAsync(Cached(me)));

        await Pymk(me);
        var dismissed = await _people.As(me).PostAsync($"/v1/suggestions/{fof}/dismiss", null, Ct);
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        Assert.False(await _valkey.KeyExistsAsync(Cached(me)));
        Assert.DoesNotContain(fof, await Pymk(me));
        Assert.Equal(1L, await Db.Scalar<long>("SELECT count(*) FROM recommendations.dismissals WHERE reader_id = @me AND account_id = @fof AND until > now() + interval '89 days'", ("me", me), ("fof", fof)));
    }

    [Fact]
    public async Task Dismissing_oneself_is_refused()
    {
        var me = await _people.SignUp();

        var reply = await _people.As(me).PostAsync($"/v1/suggestions/{me}/dismiss", null, Ct);

        Assert.Equal("cannot_dismiss_self", (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }
}
