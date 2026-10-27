using System.Net.Http.Json;
using System.Text.Json;

using StackExchange.Redis;

namespace Integration.Tests;

/// <summary>Who to follow, end to end: relationship events project the graph, the endpoint walks it.</summary>
public sealed class SuggestionTests(Postgres postgres) : IntegrationTest(postgres)
{
    /// <summary>
    /// A fresh list: the cached one (an hour, by design) is dropped first, since a friend's follow does
    /// not drop the reader's list and these tests wait for the graph to catch up.
    /// </summary>
    internal static async Task<JsonElement> Fresh(HttpClient client, string valkey, Guid reader)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(valkey);
        await connection.GetDatabase().KeyDeleteAsync([$"rec:sugg:{reader:N}:who-to-follow", $"rec:sugg:{reader:N}:people-you-may-know"]);
        return await client.GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Friends_of_friends_are_suggested_from_the_projected_graph()
    {
        var people = new People(StartHost());
        var (me, ada, bob, carol, dan) = (await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(me, ada);
        await people.Follow(me, bob);
        await people.Follow(ada, carol);
        await people.Follow(bob, carol);
        await people.Follow(ada, dan);

        List<(Guid Id, long Mutual)> suggested = [];
        await Eventually(async () =>
        {
            var reply = await Fresh(people.As(me), Db.ValkeyShort, me);
            suggested = [.. reply.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("account_id").GetGuid(), i.GetProperty("mutual").GetInt64()))];
            // Every follow projected: carol counted through both friends, not only the first to arrive.
            return suggested.SequenceEqual([(carol, 2L), (dan, 1L)]);
        }, seconds: 30);

        Assert.Equal([(carol, 2L), (dan, 1L)], suggested);
    }

    [Fact]
    public async Task A_block_through_the_api_removes_the_suggestion()
    {
        var people = new People(StartHost());
        var (me, ada, carol) = (await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(me, ada);
        await people.Follow(ada, carol);
        await Eventually(async () => (await Fresh(people.As(me), Db.ValkeyShort, me)).GetProperty("items").GetArrayLength() == 1, seconds: 30);

        await people.As(carol).PutAsync($"/v1/accounts/{me}/block", null, Ct);

        // From the cache or not, the list served is filtered against the graph's current blocks.
        await Eventually(async () => (await people.As(me).GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct)).GetProperty("items").GetArrayLength() == 0, seconds: 30);
    }
}
