using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

using Timeline.Domain;

namespace Timeline.Application;

/// <summary>Which of these authors posted most recently, with when: Posts' author_activity.</summary>
public interface IAuthorActivity
{
    Task<IReadOnlyList<(Guid Author, DateTimeOffset LastPostedAt)>> MostRecent(IReadOnlyList<Guid> authors, int limit, CancellationToken cancellationToken);
}

/// <summary>Timeline's rebuild jobs: one row per reader whose timeline is to be built.</summary>
public interface IRebuildJobs
{
    /// <summary>Asks for a rebuild, unless one is already asked for.</summary>
    Task Request(Guid reader, CancellationToken cancellationToken);

    /// <summary>The oldest request nobody holds, held by the caller for <paramref name="lease"/>.</summary>
    Task<Guid?> Claim(TimeSpan lease, CancellationToken cancellationToken);

    Task Finish(Guid reader, CancellationToken cancellationToken);
}

/// <summary>The rebuild lock in Valkey (rebuild:{reader}, SET NX EX): one rebuild per reader at a time, anywhere.</summary>
public interface IRebuildLock
{
    Task<bool> TryTake(Guid reader, TimeSpan hold, CancellationToken cancellationToken);

    Task Release(Guid reader, CancellationToken cancellationToken);
}

/// <summary>
/// Stage 2 of a returning reader's timeline: under the 60-second lock, create the timeline with its
/// sentinel (from then on fanout adds to it, so a post published during the rebuild is not lost),
/// then fill it from every followed account. False: another task holds the lock.
/// </summary>
public sealed class TimelineRebuilder(ITimelines timelines, IPostSummaries posts, IFollowing following, IRebuildLock rebuildLock)
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(60);

    public async Task<bool> Run(Guid reader, CancellationToken cancellationToken)
    {
        if (await timelines.Exists(reader, cancellationToken))
        {
            return true;
        }

        if (!await rebuildLock.TryTake(reader, Hold, cancellationToken))
        {
            return false;
        }

        try
        {
            if (!await timelines.CreateEmpty(reader, cancellationToken))
            {
                return true; // made meanwhile
            }

            var authors = (await following.Of(reader, cancellationToken)).Append(reader).Distinct().ToList();
            var recent = await posts.RecentByAuthors(authors, ReadFeedHandler.BuildPerAuthor(authors.Count), cancellationToken);
            await timelines.Fill(reader, [.. recent.OrderByDescending(p => TimelineRules.Score(p.PostId)).Take(TimelineRules.MaxEntries).Select(p => p.PostId)], cancellationToken);
            return true;
        }
        finally
        {
            await rebuildLock.Release(reader, CancellationToken.None);
        }
    }
}

/// <summary>Runs one rebuild job, if one is waiting. False when there was none.</summary>
public sealed record RunRebuild(TimeSpan Lease) : ICommand<bool>;

public sealed class RunRebuildHandler(IRebuildJobs jobs, TimelineRebuilder rebuilder) : ICommandHandler<RunRebuild, bool>
{
    public async Task<Result<bool>> Handle(RunRebuild command, CancellationToken cancellationToken)
    {
        if (await jobs.Claim(command.Lease, cancellationToken) is not { } reader)
        {
            return Result.Success(false);
        }

        // Whether this task built it or another task holds the lock and is building it, the request is answered.
        await rebuilder.Run(reader, cancellationToken);
        await jobs.Finish(reader, cancellationToken);
        return Result.Success(true);
    }
}

/// <summary>
/// Stage 1 of a returning reader's first page, on the request: the newest posts of the 20 followed
/// accounts that posted most recently (and the reader's own), with the pulled authors merged in by the
/// feed. A post from any other followed account is older than the 21st account's last post, so only
/// the posts newer than that line are complete, and only those are shown: page 2, from the rebuilt
/// timeline, continues below the line with no gap.
/// </summary>
public sealed class FirstPageSources(IAuthorActivity activity, IPostSummaries posts)
{
    public const int RecentAuthors = 20;

    /// <summary>The cards for page 1, and the line below which they are not complete (null: they all are).</summary>
    public async Task<(IReadOnlyList<PostCard> Posts, long? LineScore)> Read(Guid reader, IReadOnlySet<Guid> followed, int want, CancellationToken cancellationToken)
    {
        var active = await activity.MostRecent([.. followed.Append(reader).Distinct()], RecentAuthors + 1, cancellationToken);
        var line = active.Count > RecentAuthors ? active[RecentAuthors].LastPostedAt.ToUnixTimeMilliseconds() : (long?)null;
        var authors = active.Take(RecentAuthors).Select(a => a.Author).ToList();
        var cards = authors.Count == 0 ? [] : await posts.RecentByAuthors(authors, want, cancellationToken);
        return (cards, line);
    }
}
