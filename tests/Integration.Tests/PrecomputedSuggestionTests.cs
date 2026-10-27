using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Recommendations.Application;
using Recommendations.Domain;

using Social.BuildingBlocks.Application;

namespace Integration.Tests;

/// <summary>Supernodes and precomputed suggestions: a reader's request walks nothing, a celebrity adds no second hop.</summary>
public sealed class PrecomputedSuggestionTests(Postgres postgres) : IntegrationTest(postgres)
{
    /// <summary>Counts every walk of the graph, per reader.</summary>
    private sealed class Counted(IGraphReads inner, ConcurrentDictionary<Guid, int> walks) : IGraphReads
    {
        private void Walk(Guid reader) => walks.AddOrUpdate(reader, 1, (_, n) => n + 1);

        public Task<IReadOnlyList<Suggestion>> WhoToFollow(Guid reader, TraversalLimits limits, CancellationToken cancellationToken)
        {
            Walk(reader);
            return inner.WhoToFollow(reader, limits, cancellationToken);
        }

        public Task<IReadOnlyList<Suggestion>> FriendsOfFriends(Guid reader, TraversalLimits limits, CancellationToken cancellationToken)
        {
            Walk(reader);
            return inner.FriendsOfFriends(reader, limits, cancellationToken);
        }

        public Task<IReadOnlyList<Suggestion>> FollowersNotFollowedBack(Guid reader, int limit, TraversalLimits limits, CancellationToken cancellationToken)
        {
            Walk(reader);
            return inner.FollowersNotFollowedBack(reader, limit, limits, cancellationToken);
        }

        public Task<IReadOnlySet<Guid>> HiddenNow(Guid reader, IReadOnlyList<Guid> accounts, CancellationToken cancellationToken) =>
            inner.HiddenNow(reader, accounts, cancellationToken);
    }

    private readonly ConcurrentDictionary<Guid, int> _walks = new();

    private WebApplicationFactory<Program> Start() => StartHost(services =>
    {
        var real = services.Single(d => d.ServiceType == typeof(IGraphReads));
        services.Remove(real);
        services.AddSingleton<IGraphReads>(sp => new Counted((IGraphReads)real.ImplementationFactory!(sp), _walks));
    });

    private static async Task<List<Guid>> WhoToFollow(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("account_id").GetGuid())];

    private static async Task<int> Precompute(WebApplicationFactory<Program> host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<PrecomputeSuggestions, int>(new PrecomputeSuggestions(), Ct)).Value;
    }

    [Fact]
    public async Task A_reader_with_precomputed_suggestions_triggers_no_traversal()
    {
        var host = Start();
        var people = new People(host);
        var (me, ada, carol) = (await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(me, ada);
        await people.Follow(ada, carol);
        await Delivered();
        await Eventually(async () =>
        {
            await SuggestionTests.Fresh(people.As(me), Db.ValkeyShort, me); // opens suggestions, walks on request
            return (await WhoToFollow(people.As(me))).Contains(carol);
        }, seconds: 30);

        Assert.True(await Precompute(host) >= 1);
        _walks.Clear();
        await using (var shortLived = new Timelines(Db.ValkeyShort))
        {
            await shortLived.Db.KeyDeleteAsync([$"rec:sugg:{me:N}:who-to-follow", $"rec:sugg:{me:N}:people-you-may-know"]); // only the precomputed lists remain
        }

        Assert.Contains(carol, await WhoToFollow(people.As(me)));
        Assert.Equal(0, _walks.GetValueOrDefault(me));
    }

    /// <summary>When this reader's lists were last precomputed (Unix ms), or null. The store is shared by every test.</summary>
    private async Task<long?> PrecomputedAt(Guid reader)
    {
        await using var shortLived = new Timelines(Db.ValkeyShort);
        var at = await shortLived.Db.HashGetAsync($"rec:pre:{reader:N}", "at");
        return at.IsNull ? null : (long)at;
    }

    [Fact]
    public async Task Ten_follow_changes_send_the_lists_to_be_computed_again_and_nine_do_not()
    {
        var host = Start();
        var people = new People(host);
        var me = await people.SignUp();
        var others = await people.SignUp(10);
        await WhoToFollow(people.As(me)); // opened
        await Precompute(host);
        var first = await PrecomputedAt(me);
        Assert.NotNull(first);

        foreach (var other in others.Take(9))
        {
            await people.Follow(me, other);
        }

        await Delivered();
        await Task.Delay(5, Ct);
        await Precompute(host);
        Assert.Equal(first, await PrecomputedAt(me));

        await people.Follow(me, others[9]);
        await Delivered();
        await Task.Delay(5, Ct);
        await Precompute(host);
        Assert.True(await PrecomputedAt(me) > first);
    }

    [Fact]
    public async Task A_celebrity_among_the_readers_follows_adds_no_second_hop()
    {
        var host = Start();
        var people = new People(host);
        var (me, star, friend, viaStar, viaFriend) = (await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(me, star);
        await people.Follow(me, friend);
        await people.Follow(star, viaStar);
        await people.Follow(friend, viaFriend);
        await Delivered();
        // The star's count as Relationships would announce it with 25,000 followers: the graph's degree follows it.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<ProjectFollowerCount, bool>(
                new ProjectFollowerCount(star, 25_000, DateTimeOffset.UtcNow), Ct);
        }

        List<Guid> suggested = [];
        await Eventually(async () =>
        {
            suggested = [.. (await SuggestionTests.Fresh(people.As(me), Db.ValkeyShort, me)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("account_id").GetGuid())];
            return suggested.Contains(viaFriend);
        }, seconds: 30);

        Assert.DoesNotContain(viaStar, suggested);
    }
}
