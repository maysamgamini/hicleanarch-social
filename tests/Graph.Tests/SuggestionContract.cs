using Recommendations.Application;
using Recommendations.Domain;

namespace Graph.Tests;

/// <summary>
/// The rules of who to follow, as one suite every adapter of IGraphReads and IGraphProjection must
/// pass: PostgreSQL first (s2-l13), the graph database next. The assertions never change between them.
/// </summary>
public abstract class SuggestionContract : IAsyncLifetime
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private long _version;

    protected IGraphProjection Projection { get; private set; } = null!;

    protected IGraphReads Reads { get; private set; } = null!;

    protected static TraversalLimits Limits => TraversalLimits.Default;

    /// <summary>The adapter under test, on a store of its own.</summary>
    protected abstract Task<(IGraphProjection Projection, IGraphReads Reads, IAsyncDisposable Store)> Connect();

    private IAsyncDisposable _store = null!;

    public async ValueTask InitializeAsync() => (Projection, Reads, _store) = await Connect();

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected async Task<Guid> Account(string? handle = null)
    {
        var id = Guid.CreateVersion7();
        await Projection.UpsertAccount(id, handle ?? "a" + id.ToString("N")[^10..], Ct);
        return id;
    }

    protected async Task<Guid[]> Accounts(int count)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await Account();
        }

        return ids;
    }

    protected Task Edge(EdgeKind kind, Guid from, Guid to, bool on = true, long? version = null) =>
        Projection.Apply(new EdgeChange(kind, from, to, version ?? Interlocked.Increment(ref _version), on, DateTimeOffset.UtcNow.AddSeconds(_version)), Ct);

    protected Task Follow(Guid from, Guid to) => Edge(EdgeKind.Follows, from, to);

    protected async Task<IReadOnlyList<Suggestion>> Suggest(Guid reader) =>
        SuggestionRanking.Rank(await Reads.WhoToFollow(reader, Limits, Ct), Limits.Results);

    [Fact]
    public async Task Suggestions_are_ranked_by_mutual_count_and_ties_by_account_id()
    {
        var (me, a, b, c) = (await Account(), await Account(), await Account(), await Account());
        var (two, oneX, oneY) = (await Account(), await Account(), await Account());
        foreach (var friend in new[] { a, b, c })
        {
            await Follow(me, friend);
        }

        await Follow(a, two);
        await Follow(b, two);
        await Follow(c, oneX);
        await Follow(a, oneY);

        var suggestions = await Suggest(me);

        Assert.Equal(two, suggestions[0].AccountId);
        Assert.Equal(2, suggestions[0].Mutual);
        Assert.Equal(new[] { oneX, oneY }.OrderBy(i => i.ToString("N"), StringComparer.Ordinal), suggestions.Skip(1).Select(s => s.AccountId));
    }

    [Fact]
    public async Task Never_the_reader_nor_an_account_already_followed()
    {
        var (me, a, followed) = (await Account(), await Account(), await Account());
        await Follow(me, a);
        await Follow(me, followed);
        await Follow(a, me);
        await Follow(a, followed);

        Assert.Empty(await Suggest(me));
    }

    [Fact]
    public async Task Never_a_blocked_or_blocking_account_nor_a_muted_one()
    {
        var (me, a) = (await Account(), await Account());
        var (blocked, blocking, muted, fine) = (await Account(), await Account(), await Account(), await Account());
        await Follow(me, a);
        foreach (var candidate in new[] { blocked, blocking, muted, fine })
        {
            await Follow(a, candidate);
        }

        await Edge(EdgeKind.Blocks, me, blocked);
        await Edge(EdgeKind.Blocks, blocking, me);
        await Edge(EdgeKind.Mutes, me, muted);

        Assert.Equal([fine], (await Suggest(me)).Select(s => s.AccountId));
    }

    [Fact]
    public async Task A_mute_by_the_candidate_hides_nothing()
    {
        var (me, a, candidate) = (await Account(), await Account(), await Account());
        await Follow(me, a);
        await Follow(a, candidate);
        await Edge(EdgeKind.Mutes, candidate, me);

        Assert.Equal([candidate], (await Suggest(me)).Select(s => s.AccountId));
    }

    [Fact]
    public async Task No_supernode_is_used_as_an_intermediate()
    {
        var (me, star, viaStar) = (await Account(), await Account(), await Account());
        await Follow(me, star);
        await Follow(star, viaStar);
        await MakeSupernode(star);

        Assert.DoesNotContain(viaStar, (await Suggest(me)).Select(s => s.AccountId));
    }

    /// <summary>Gives the account at least the supernode threshold of followers.</summary>
    protected abstract Task MakeSupernode(Guid account);

    [Fact]
    public async Task A_reordered_FollowStarted_and_FollowEnded_end_in_the_state_of_the_higher_pair_version()
    {
        var (me, a, candidate) = (await Account(), await Account(), await Account());
        await Follow(me, a);

        // The pair's follow ended at version 7 after it started at version 6, but the end arrives first.
        await Edge(EdgeKind.Follows, a, candidate, on: false, version: 7);
        await Edge(EdgeKind.Follows, a, candidate, on: true, version: 6);
        Assert.Empty(await Suggest(me));

        // And the other way round on another pair: started at 9 after ended at 8.
        var other = await Account();
        await Edge(EdgeKind.Follows, a, other, on: true, version: 9);
        await Edge(EdgeKind.Follows, a, other, on: false, version: 8);
        Assert.Equal([other], (await Suggest(me)).Select(s => s.AccountId));
    }

    [Fact]
    public async Task A_deactivated_account_is_suggested_to_nobody()
    {
        var (me, a, gone) = (await Account(), await Account(), await Account());
        await Follow(me, a);
        await Follow(a, gone);

        await Projection.Deactivate(gone, Ct);

        Assert.Empty(await Suggest(me));
    }

    [Fact]
    public async Task An_edge_whose_account_is_not_in_the_graph_yet_fails_and_creates_nothing()
    {
        var me = await Account();
        var unknown = Guid.CreateVersion7();

        await Assert.ThrowsAnyAsync<Exception>(() => Follow(me, unknown));
        await Projection.UpsertAccount(unknown, "late", Ct);
        await Follow(me, unknown); // the retry, once the account is there
    }
}
