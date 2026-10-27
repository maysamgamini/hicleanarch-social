using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;

namespace Integration.Tests;

public sealed class BlockAndMuteTests(Postgres postgres) : IntegrationTest(postgres)
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = null!;
    private People _people = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _host = StartHost();
        _people = new People(_host);
    }

    private Task<HttpResponseMessage> Block(Guid blocker, Guid blocked) => _people.As(blocker).PutAsync($"/v1/accounts/{blocked}/block", null, Ct);

    private Task<HttpResponseMessage> Unblock(Guid blocker, Guid blocked) => _people.As(blocker).DeleteAsync($"/v1/accounts/{blocked}/block", Ct);

    private Task<HttpResponseMessage> Mute(Guid muter, Guid muted) => _people.As(muter).PutAsync($"/v1/accounts/{muted}/mute", null, Ct);

    private Task<long> Follows(Guid a, Guid b) =>
        Db.Scalar<long>("SELECT count(*) FROM relationships.follows WHERE (follower_id = @a AND followee_id = @b) OR (follower_id = @b AND followee_id = @a)", ("a", a), ("b", b));

    /// <summary>The invariant itself: a follow beside a block between the same two accounts, in either direction.</summary>
    private Task<long> FollowsAcrossABlock() => Db.Scalar<long>("""
        SELECT count(*) FROM relationships.follows f JOIN relationships.blocks b
          ON (b.blocker_id = f.follower_id AND b.blocked_id = f.followee_id) OR (b.blocker_id = f.followee_id AND b.blocked_id = f.follower_id)
        """);

    private async Task<HiddenAuthors> Hidden(Guid viewer)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query<GetHiddenAuthors, HiddenAuthors>(new GetHiddenAuthors(viewer), Ct)).Value;
    }

    [Fact]
    public async Task A_block_removes_both_follows_and_their_counts()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(ada, bob);
        await _people.Follow(bob, ada);

        var reply = await Block(ada, bob);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(0L, await Follows(ada, bob));
        await LedgerFolded();
        Assert.Equal(0L, await Db.Scalar<long>("SELECT followers + following FROM relationships.relationship_counts WHERE account_id = @a", ("a", ada)));
        Assert.Equal(0L, await Db.Scalar<long>("SELECT followers + following FROM relationships.relationship_counts WHERE account_id = @a", ("a", bob)));
    }

    [Fact]
    public async Task A_follow_after_a_block_is_refused_both_ways()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await Block(ada, bob);

        var byBlocker = await _people.Follow(ada, bob);
        var byBlocked = await _people.Follow(bob, ada);

        Assert.Equal(HttpStatusCode.Conflict, byBlocker.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, byBlocked.StatusCode);
        Assert.Equal("blocked", (await byBlocked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(0L, await Follows(ada, bob));
    }

    [Fact]
    public async Task After_an_unblock_a_follow_is_allowed_again()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await Block(ada, bob);
        await Unblock(ada, bob);

        Assert.Equal(HttpStatusCode.OK, (await _people.Follow(bob, ada)).StatusCode);
    }

    [Fact]
    public async Task Two_hundred_runs_of_a_block_and_a_follow_in_parallel_never_leave_a_follow_beside_a_block()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());

        for (var run = 0; run < 200; run++)
        {
            (await Unblock(ada, bob)).EnsureSuccessStatusCode();
            var replies = await Task.WhenAll(_people.Follow(bob, ada), Block(ada, bob));

            // Whichever ran first: a follow that committed first was removed by the block, one that ran after was refused.
            Assert.Contains(replies[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            Assert.Equal(HttpStatusCode.OK, replies[1].StatusCode);
            Assert.Equal(0L, await FollowsAcrossABlock());
        }
    }

    [Fact]
    public async Task A_mute_is_one_way_and_changes_no_follow()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        await _people.Follow(ada, bob);
        await _people.Follow(bob, ada);

        var reply = await Mute(ada, bob);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.True((await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("muting").GetBoolean());
        Assert.Equal(2L, await Follows(ada, bob));
        Assert.Equal([bob], (await Hidden(ada)).AccountIds);
        Assert.Empty((await Hidden(bob)).AccountIds);
    }

    [Fact]
    public async Task The_hidden_authors_are_the_blocked_the_blocking_and_the_muted()
    {
        var (viewer, blocked, blocking, muted, other) = (await _people.SignUp(), await _people.SignUp(), await _people.SignUp(), await _people.SignUp(), await _people.SignUp());
        await Block(viewer, blocked);
        await Block(blocking, viewer);
        await Mute(viewer, muted);
        await Mute(other, viewer);

        var hidden = await Hidden(viewer);

        Assert.Equal(new[] { blocked, blocking, muted }.Order(), hidden.AccountIds.Order());
    }

    [Fact]
    public async Task Blocking_or_muting_oneself_is_refused()
    {
        var ada = await _people.SignUp();

        Assert.Equal("cannot_block_self", (await (await Block(ada, ada)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal("cannot_mute_self", (await (await Mute(ada, ada)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }
}
