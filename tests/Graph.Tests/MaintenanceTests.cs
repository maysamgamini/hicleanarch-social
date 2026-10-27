using Neo4j.Driver;

using Recommendations.Application;
using Recommendations.Domain;
using Recommendations.Infrastructure.Graph;

namespace Graph.Tests;

/// <summary>The jobs walk the whole shared graph, so they run alone, after every other class.</summary>
[CollectionDefinition(nameof(WholeGraph), DisableParallelization = true)]
public sealed class WholeGraph;

/// <summary>The graph's nightly jobs on Neo4j: the reconciliation repairs, the compaction forgets only old off edges.</summary>
[Collection(nameof(WholeGraph))]
public sealed class MaintenanceTests(Neo4jServer neo4j)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Neo4jGraph _graph = new(neo4j.Driver);

    /// <summary>The source of truth, as Relationships would answer: everyone's counts and pairs from a list of follows.</summary>
    private sealed class Truth(List<(Guid From, Guid To)> follows) : IRelationshipSource
    {
        public Task<IReadOnlyDictionary<Guid, (long Followers, long Following)>> Counts(IReadOnlyList<Guid> accounts, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, (long, long)>>(accounts.ToDictionary(a => a,
                a => ((long)follows.Count(f => f.To == a), (long)follows.Count(f => f.From == a))));

        public Task<IReadOnlyList<EdgeChange>> PairsOf(Guid account, DateTimeOffset at, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EdgeChange>>([.. follows.Where(f => f.From == account || f.To == account)
                .Select(f => new EdgeChange(EdgeKind.Follows, f.From, f.To, 1, true, at))]);
    }

    private sealed class NoDismissals : IDismissals
    {
        public List<(Guid Reader, Guid Account, DateTimeOffset Until)> Kept { get; } = [];

        public Task Save(Guid reader, Guid account, DateTimeOffset until, CancellationToken cancellationToken)
        {
            Kept.Add((reader, account, until));
            return Task.CompletedTask;
        }

        public Task<int> PurgeBefore(DateTimeOffset cutoff, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<(Guid Reader, Guid Account, DateTimeOffset Until)>> InForce(IReadOnlyList<Guid> readers, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(Guid, Guid, DateTimeOffset)>>([.. Kept.Where(k => readers.Contains(k.Reader) && k.Until > now)]);
    }

    [Fact]
    public async Task A_dismissal_missing_from_the_graph_is_projected_again_from_its_durable_record()
    {
        var (me, a, candidate) = (await Account(), await Account(), await Account());
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, me, a, 1, true, DateTimeOffset.UtcNow), Ct);
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, candidate, 1, true, DateTimeOffset.UtcNow), Ct);
        // The row exists in PostgreSQL; the edge never reached the graph (or the graph was rebuilt).
        var dismissals = new NoDismissals();
        await dismissals.Save(me, candidate, DateTimeOffset.UtcNow.AddDays(30), Ct);
        Assert.Single(await _graph.WhoToFollow(me, TraversalLimits.Default, Ct));

        await new MaintenanceHandlers(_graph, _graph, new Truth([(me, a), (a, candidate)]), dismissals, TimeProvider.System).Handle(new Reconcile(), Ct);

        Assert.Empty(await _graph.WhoToFollow(me, TraversalLimits.Default, Ct));
    }

    /// <summary>A clock the test moves.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_dismissed_account_is_not_suggested_again_for_90_days()
    {
        var (me, a, candidate) = (await Account(), await Account(), await Account());
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, me, a, 1, true, DateTimeOffset.UtcNow), Ct);
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, candidate, 1, true, DateTimeOffset.UtcNow), Ct);
        var clock = new Clock(DateTimeOffset.UtcNow);
        var dismiss = new DismissHandler(new NoDismissals(), _graph, new NoCache(), clock);
        var compact = new MaintenanceHandlers(_graph, _graph, new Truth([]), new NoDismissals(), clock);

        await dismiss.Handle(new Dismiss(me, candidate), Ct);
        Assert.Empty(await _graph.WhoToFollow(me, TraversalLimits.Default, Ct));

        clock.Now += TimeSpan.FromDays(89);
        await compact.Handle(new Compact(), Ct);
        Assert.Empty(await _graph.WhoToFollow(me, TraversalLimits.Default, Ct));

        clock.Now += TimeSpan.FromDays(2);
        await compact.Handle(new Compact(), Ct);
        Assert.Equal([candidate], (await _graph.WhoToFollow(me, TraversalLimits.Default, Ct)).Select(s => s.AccountId));
    }

    private sealed class NoCache : ISuggestionCache
    {
        public Task<IReadOnlyList<Suggestion>?> Get(Guid reader, string kind, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Suggestion>?>(null);

        public Task Set(Guid reader, string kind, IReadOnlyList<Suggestion> suggestions, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Drop(Guid reader, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Opened(Guid reader, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> OpenedSince(DateTimeOffset since, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<(DateTimeOffset At, long Changes)?> Precomputed(Guid reader, CancellationToken cancellationToken) => Task.FromResult<(DateTimeOffset, long)?>(null);

        public Task SetPrecomputed(Guid reader, IReadOnlyDictionary<string, IReadOnlyList<Suggestion>> lists, DateTimeOffset at, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> CountChange(Guid reader, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private async Task<Guid> Account()
    {
        var id = Guid.CreateVersion7();
        await _graph.UpsertAccount(id, "m" + id.ToString("N")[^8..], Ct);
        return id;
    }

    private async Task<long> Property(Guid account, string name)
    {
        await using var session = neo4j.Driver.AsyncSession();
        var record = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
            $"MATCH (a:Account {{id: $id}}) RETURN a.{name} AS n", new { id = account.ToString("D") })).SingleAsync());
        return record["n"].As<long>();
    }

    [Fact]
    public async Task A_degree_changed_by_hand_in_the_graph_is_repaired_by_the_reconciliation()
    {
        var (a, b) = (await Account(), await Account());
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, b, 1, true, DateTimeOffset.UtcNow), Ct);
        await using (var session = neo4j.Driver.AsyncSession())
        {
            await session.ExecuteWriteAsync(tx => tx.RunAsync("MATCH (x:Account {id: $id}) SET x.followers = 41", new { id = b.ToString("D") }));
        }

        var handlers = new MaintenanceHandlers(_graph, _graph, new Truth([(a, b)]), new NoDismissals(), TimeProvider.System);
        var report = (await handlers.Handle(new Reconcile(), Ct)).Value;

        Assert.Equal(1L, await Property(b, "followers"));
        Assert.True(report.Repaired >= 1);
        Assert.Empty(report.Duplicates); // Neo4j's constraint makes duplicates impossible here
    }

    [Fact]
    public async Task A_missing_edge_is_projected_again_by_the_reconciliation()
    {
        var (a, b) = (await Account(), await Account());

        var handlers = new MaintenanceHandlers(_graph, _graph, new Truth([(a, b)]), new NoDismissals(), TimeProvider.System);
        await handlers.Handle(new Reconcile(), Ct);

        Assert.Equal(1L, await Property(a, "following"));
        Assert.Equal(1L, await Property(b, "followers"));
    }

    [Fact]
    public async Task Compaction_deletes_edges_off_for_more_than_30_days_and_keeps_younger_ones()
    {
        var (a, old, young, on) = (await Account(), await Account(), await Account(), await Account());
        var now = DateTimeOffset.UtcNow;
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, old, 2, false, now.AddDays(-31)), Ct);
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, young, 2, false, now.AddDays(-10)), Ct);
        await _graph.Apply(new EdgeChange(EdgeKind.Follows, a, on, 2, true, now.AddDays(-400)), Ct);

        var handlers = new MaintenanceHandlers(_graph, _graph, new Truth([]), new NoDismissals(), TimeProvider.System);
        await handlers.Handle(new Compact(), Ct);

        await using var session = neo4j.Driver.AsyncSession();
        var left = await session.ExecuteReadAsync(async tx => await (await tx.RunAsync(
            "MATCH (:Account {id: $a})-[r:FOLLOWS]->(b:Account) RETURN b.id AS id", new { a = a.ToString("D") })).ToListAsync());
        Assert.Equal(new[] { young, on }.Select(i => i.ToString("D")).Order(), left.Select(r => r["id"].As<string>()).Order());
    }
}
