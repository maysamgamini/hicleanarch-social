using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests;

public sealed class FollowTests(Postgres postgres) : IntegrationTest(postgres)
{
    private People _people = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _people = new People(StartHost());
    }

    private Task<long> Rows(Guid follower, Guid followee) =>
        Db.Scalar<long>("SELECT count(*) FROM relationships.follows WHERE follower_id = @f AND followee_id = @e", ("f", follower), ("e", followee));

    private async Task<(long Followers, long Following)> Counts(Guid account)
    {
        // Followers are folded from the ledger within a second; following is exact at once.
        await LedgerFolded();
        var followers = await Db.Scalar<long>("SELECT coalesce(max(followers), 0) FROM relationships.relationship_counts WHERE account_id = @a", ("a", account));
        var following = await Db.Scalar<long>("SELECT coalesce(max(following), 0) FROM relationships.relationship_counts WHERE account_id = @a", ("a", account));
        return (followers, following);
    }

    [Fact]
    public async Task Following_twice_gives_one_row_and_the_same_reply()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());

        var first = await _people.Follow(ada, bob);
        var second = await _people.Follow(ada, bob);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await second.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1L, await Rows(ada, bob));
        Assert.Equal((1L, 0L), await Counts(bob));
        Assert.Equal((0L, 1L), await Counts(ada));
    }

    [Fact]
    public async Task Unfollowing_an_account_not_followed_is_200_and_changes_nothing()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());

        var reply = await _people.Unfollow(ada, bob);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.False((await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("following").GetBoolean());
        Assert.Equal((0L, 0L), await Counts(bob));
    }

    [Fact]
    public async Task An_account_cannot_follow_itself()
    {
        var ada = await _people.SignUp();

        var reply = await _people.Follow(ada, ada);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reply.StatusCode);
        Assert.Equal("cannot_follow_self", (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Following_an_account_that_does_not_exist_is_404()
    {
        var reply = await _people.Follow(await _people.SignUp(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        Assert.Equal("account_not_found", (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_hundred_concurrent_follows_and_unfollows_of_one_pair_leave_counts_equal_to_the_rows()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());

        var replies = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? _people.Follow(ada, bob) : _people.Unfollow(ada, bob)));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var rows = await Rows(ada, bob);
        Assert.Equal(rows, (await Counts(bob)).Followers);
        Assert.Equal(rows, (await Counts(ada)).Following);
        // Every change, even one that found nothing to do, took the pair's next version.
        Assert.Equal(100L, await Db.Scalar<long>("SELECT version FROM relationships.pair_versions WHERE low_id = LEAST(@a, @b) AND high_id = GREATEST(@a, @b)", ("a", ada), ("b", bob)));
    }

    [Fact]
    public async Task Concurrent_follows_across_overlapping_pairs_never_deadlock_and_keep_every_count_exact()
    {
        var people = await _people.SignUp(5);
        var moves = from a in people from b in people where a != b select (a, b);

        var replies = await Task.WhenAll(moves.SelectMany(m => new[] { _people.Follow(m.a, m.b), _people.Follow(m.b, m.a) }));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var person in people)
        {
            Assert.Equal((4L, 4L), await Counts(person));
        }
    }

    [Fact]
    public async Task The_5001st_follow_is_409_follow_limit()
    {
        var (ada, bob) = (await _people.SignUp(), await _people.SignUp());
        // 5,000 follows already, as the API would have left them: the rows and their counter.
        await Db.Execute("""
            INSERT INTO relationships.follows (follower_id, followee_id) SELECT @ada, gen_random_uuid() FROM generate_series(1, 5000);
            INSERT INTO relationships.relationship_counts (account_id, followers, following) VALUES (@ada, 0, 5000);
            """, ("ada", ada));

        var reply = await _people.Follow(ada, bob);

        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
        Assert.Equal("follow_limit", (await reply.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());
        Assert.Equal(0L, await Rows(ada, bob));
        Assert.Equal((0L, 5000L), await Counts(ada));
        Assert.Equal((0L, 0L), await Counts(bob));
    }

    [Fact]
    public async Task Followers_and_following_come_in_keyset_pages_in_id_order()
    {
        var star = await _people.SignUp();
        var fans = await _people.SignUp(5);
        foreach (var fan in fans)
        {
            (await _people.Follow(fan, star)).EnsureSuccessStatusCode();
        }

        var client = _people.As(star);
        var first = await client.GetFromJsonAsync<JsonElement>($"/v1/accounts/{star}/followers?limit=3", Ct);
        var after = first.GetProperty("next_after").GetGuid();
        var second = await client.GetFromJsonAsync<JsonElement>($"/v1/accounts/{star}/followers?limit=3&after={after}", Ct);
        var following = await client.GetFromJsonAsync<JsonElement>($"/v1/accounts/{fans[0]}/following", Ct);

        var listed = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("account_id").GetGuid()).ToList();
        Assert.Equal(5, listed.Count);
        Assert.Equal(fans.Order(new PostgresUuidOrder()), listed);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_after").ValueKind);
        Assert.Equal(star, Assert.Single(following.GetProperty("items").EnumerateArray()).GetProperty("account_id").GetGuid());
    }

    /// <summary>PostgreSQL compares uuids byte by byte, in the order they are written.</summary>
    private sealed class PostgresUuidOrder : IComparer<Guid>
    {
        public int Compare(Guid x, Guid y) => string.CompareOrdinal(x.ToString("N"), y.ToString("N"));
    }
}
