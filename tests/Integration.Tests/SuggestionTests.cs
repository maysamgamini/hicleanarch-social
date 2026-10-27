using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests;

/// <summary>Who to follow, end to end: relationship events project the graph, the endpoint walks it.</summary>
public sealed class SuggestionTests(Postgres postgres) : IntegrationTest(postgres)
{
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
            var reply = await people.As(me).GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct);
            suggested = [.. reply.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("account_id").GetGuid(), i.GetProperty("mutual").GetInt64()))];
            return suggested.Count == 2;
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
        await Eventually(async () => (await people.As(me).GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct)).GetProperty("items").GetArrayLength() == 1, seconds: 30);

        await people.As(carol).PutAsync($"/v1/accounts/{me}/block", null, Ct);

        await Eventually(async () => (await people.As(me).GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct)).GetProperty("items").GetArrayLength() == 0, seconds: 30);
    }
}
