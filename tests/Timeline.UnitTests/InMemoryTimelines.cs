using Timeline.Application;
using Timeline.Domain;

namespace Timeline.UnitTests;

/// <summary>Timelines in memory, with the same rules as the Valkey adapter: add only if it exists, keep the newest 800.</summary>
internal sealed class InMemoryTimelines : ITimelines
{
    private readonly Dictionary<Guid, SortedSet<(long Score, Guid Post)>> _timelines = [];

    public List<Guid> Written { get; } = [];

    public int Touched { get; private set; }

    public IReadOnlyCollection<(long Score, Guid Post)> Of(Guid owner) => _timelines[owner];

    public Task<int> AddToExisting(IReadOnlyList<Guid> owners, Guid postId, CancellationToken cancellationToken)
    {
        Written.AddRange(owners);
        var took = 0;
        foreach (var owner in owners.Where(_timelines.ContainsKey))
        {
            Add(owner, postId);
            took++;
        }

        return Task.FromResult(took);
    }

    public Task<bool> Exists(Guid owner, CancellationToken cancellationToken) => Task.FromResult(_timelines.ContainsKey(owner));

    public Task<bool> CreateEmpty(Guid owner, CancellationToken cancellationToken) => Task.FromResult(_timelines.TryAdd(owner, []));

    public Task Fill(Guid owner, IReadOnlyList<Guid> postIds, CancellationToken cancellationToken)
    {
        foreach (var post in postIds.Where(_ => _timelines.ContainsKey(owner)))
        {
            Add(owner, post);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Entry>> Read(Guid owner, long? maxScore, int skip, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Entry>>(_timelines.TryGetValue(owner, out var set)
            ? [.. set.Reverse().Where(e => e.Score <= (maxScore ?? long.MaxValue)).Skip(skip).Take(count).Select(e => new Entry(e.Post, e.Score))]
            : []);

    public Task Touch(Guid owner, CancellationToken cancellationToken)
    {
        Touched++;
        return Task.CompletedTask;
    }

    private void Add(Guid owner, Guid post)
    {
        var set = _timelines[owner];
        set.Add((TimelineRules.Score(post), post));
        while (set.Count > TimelineRules.MaxEntries)
        {
            set.Remove(set.Min);
        }
    }
}
