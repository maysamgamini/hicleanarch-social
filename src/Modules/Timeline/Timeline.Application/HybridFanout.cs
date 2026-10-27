using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

using Timeline.Domain;

namespace Timeline.Application;

/// <summary>The registry of pulled authors (fanout:registry): few entries, read whole.</summary>
public interface IPullRegistry
{
    Task<PulledAuthor?> Find(Guid author, CancellationToken cancellationToken);

    Task<IReadOnlyList<PulledAuthor>> All(CancellationToken cancellationToken);

    Task Save(PulledAuthor entry, CancellationToken cancellationToken);

    Task Remove(Guid author, CancellationToken cancellationToken);
}

/// <summary>Each reader's pull list (pulls:{reader}): the followed authors in the registry, cached.</summary>
public interface IPullLists
{
    /// <summary>The cached list, or null when there is none.</summary>
    Task<IReadOnlyList<Guid>?> Get(Guid reader, CancellationToken cancellationToken);

    Task Set(Guid reader, IReadOnlyList<Guid> authors, TimeSpan lifetime, CancellationToken cancellationToken);
}

/// <summary>One author's newest active posts, older than a post when one is given, newest first.</summary>
public interface IRecentPosts
{
    Task<IReadOnlyList<PostCard>> Of(Guid author, Guid? before, int limit, CancellationToken cancellationToken);
}

/// <summary>An author's follower count changed, as Relationships announced it.</summary>
public sealed record ObserveFollowerCount(Guid AuthorId, long Followers) : ICommand<PulledAuthor?>;

/// <summary>Keeps the registry: promotes at 25,000 followers, demotes below 20,000, forgets three days after a demotion.</summary>
public sealed class ObserveFollowerCountHandler(IPullRegistry registry, TimeProvider clock) : ICommandHandler<ObserveFollowerCount, PulledAuthor?>
{
    public async Task<Result<PulledAuthor?>> Handle(ObserveFollowerCount command, CancellationToken cancellationToken)
    {
        // Milliseconds, as the registry keeps them: an entry read back equals the one written.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
        var current = await registry.Find(command.AuthorId, cancellationToken);
        var next = PulledAuthor.Next(current, command.AuthorId, command.Followers, now);
        if (next is not null && !next.Pulled(now))
        {
            await registry.Remove(command.AuthorId, cancellationToken);
            return Result.Success<PulledAuthor?>(null);
        }

        if (next is not null && next != current)
        {
            await registry.Save(next, cancellationToken);
        }

        return Result.Success(next);
    }
}

/// <summary>How long a feed read gives each pulled author (Timeline:Pull:Budget, 25 ms).</summary>
public sealed record PullOptions(TimeSpan Budget);

/// <summary>
/// The pull side of a feed read: the reader's pull list (cached 10 minutes), then each pulled author's
/// newest posts after the page's cursor, all at once, each within its budget. A late author, or one
/// whose read failed, is skipped for this page; the page is still served.
/// </summary>
public sealed class PulledReads(IPullRegistry registry, IPullLists lists, IRecentPosts recent, IProfiles profiles, PullOptions options, TimeProvider clock)
{
    private static readonly Counter<long> Skipped = new Meter("Social.Timeline").CreateCounter<long>(
        "social.feed.pulls_skipped", "{author}", "Pulled authors left out of a page for answering after their budget");

    private static readonly Counter<long> Failed = new Meter("Social.Timeline").CreateCounter<long>(
        "social.feed.pulls_failed", "{author}", "Pulled authors left out of a page because their read failed");

    public async Task<IReadOnlyList<Guid>> PullListOf(Guid reader, IReadOnlySet<Guid> followed, CancellationToken cancellationToken)
    {
        if (await lists.Get(reader, cancellationToken) is { } cached)
        {
            return cached;
        }

        var now = clock.GetUtcNow();
        var list = (await registry.All(cancellationToken)).Where(e => e.Pulled(now) && followed.Contains(e.AuthorId)).Select(e => e.AuthorId).ToList();
        await lists.Set(reader, list, PullRules.PullListCache, cancellationToken);
        return list;
    }

    /// <summary>
    /// The newest posts of each pulled author after the cursor (at most <paramref name="want"/> each);
    /// Full when an author may have more; Partial when an author was left out, late or failed (s3-l17).
    /// </summary>
    public async Task<(IReadOnlyList<FeedItem> Items, bool Full, bool Partial)> Read(IReadOnlyList<Guid> authors, Guid? before, int want, CancellationToken cancellationToken)
    {
        if (authors.Count == 0)
        {
            return ([], false, false);
        }

        var answers = await Task.WhenAll(authors.Select(a => WithinBudget(a, before, want, cancellationToken)));
        var pages = answers.Where(p => p is not null).Select(p => p!).ToList();
        var partial = pages.Count < answers.Length;
        var posts = pages.SelectMany(p => p).ToList();
        if (posts.Count == 0)
        {
            return ([], false, partial);
        }

        var cards = (await profiles.Get([.. posts.Select(p => p.AuthorId).Distinct()], cancellationToken)).ToDictionary(a => a.AccountId);
        return ([.. posts.Where(p => cards.ContainsKey(p.AuthorId)).Select(p => new FeedItem(p, cards[p.AuthorId]))], pages.Any(p => p.Count >= want), partial);
    }

    private async Task<IReadOnlyList<PostCard>?> WithinBudget(Guid author, Guid? before, int want, CancellationToken cancellationToken)
    {
        using var late = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var call = recent.Of(author, before, want, late.Token);
        if (await Task.WhenAny(call, Task.Delay(options.Budget, cancellationToken)) == call)
        {
            if (call.IsCompletedSuccessfully)
            {
                return call.Result;
            }

            Failed.Add(1); // a pulled author Posts could not answer for is left out, like a late one
            return null;
        }

        late.Cancel();
        _ = call.ContinueWith(t => t.Exception, TaskScheduler.Default); // observed: a late call's failure is not news
        Skipped.Add(1);
        return null;
    }
}

public static class HybridFanoutApplication
{
    public static IServiceCollection AddHybridFanout(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<ObserveFollowerCount, PulledAuthor?>, ObserveFollowerCountHandler>()
            .AddScoped<PulledReads>();
}
