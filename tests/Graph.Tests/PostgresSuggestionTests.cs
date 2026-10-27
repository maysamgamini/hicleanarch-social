using Recommendations.Application;
using Recommendations.Infrastructure.Persistence;

using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Graph.Tests;

/// <summary>The suggestion rules on the first adapter: the graph in PostgreSQL.</summary>
public sealed class PostgresSuggestionTests(PostgresGraph postgres) : SuggestionContract
{
    private RecommendationsDbContext _db = null!;

    protected override async Task<(IGraphProjection Projection, IGraphReads Reads, IAsyncDisposable Store)> Connect()
    {
        _db = await postgres.NewContext();
        var graph = new SqlGraph(_db);
        return (graph, graph, _db);
    }

    protected override Task MakeSupernode(Guid account) =>
        _db.Execute("UPDATE recommendations.accounts SET followers = @n WHERE id = @id", Ct, ("n", (long)Limits.SupernodeFollowers), ("id", account));
}
