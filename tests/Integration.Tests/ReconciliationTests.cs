using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Neo4j.Driver;

using Recommendations.Application;

using Social.BuildingBlocks.Application;

namespace Integration.Tests;

/// <summary>The nightly reconciliation end to end: Relationships answers through its contract, the graph is repaired.</summary>
[Collection(nameof(Measured))] // it walks the whole shared graph: alone, after the others
public sealed class ReconciliationTests(Postgres postgres) : IntegrationTest(postgres)
{
    [Fact]
    public async Task A_degree_tampered_with_in_the_graph_is_repaired_from_Relationships_counts()
    {
        var host = StartHost();
        var people = new People(host);
        var (me, ada, carol) = (await people.SignUp(), await people.SignUp(), await people.SignUp());
        await people.Follow(me, ada);
        await people.Follow(ada, carol);
        await Eventually(async () => (await people.As(me).GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct)).GetProperty("items").GetArrayLength() == 1, seconds: 30);

        // Someone sets Ada's following count high enough to make her look like no intermediate at all.
        var driver = host.Services.GetRequiredService<IDriver>();
        await using (var session = driver.AsyncSession())
        {
            await session.ExecuteWriteAsync(tx => tx.RunAsync("MATCH (a:Account {id: $id}) SET a.following = 5000", new { id = ada.ToString("D") }));
        }

        Assert.Equal(0, (await people.As(me).GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct)).GetProperty("items").GetArrayLength());

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<Reconcile, ReconciliationReport>(new Reconcile(), Ct);
        }

        Assert.Equal(1, (await people.As(me).GetFromJsonAsync<JsonElement>("/v1/suggestions/who-to-follow", Ct)).GetProperty("items").GetArrayLength());
    }
}
