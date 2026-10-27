using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

using Timeline.Domain;

namespace Timeline.Application;

/// <summary>The reader's home feed: their newest posts, from the accounts they follow and their own.</summary>
public sealed record ReadFeed(Guid ReaderId, int Limit, string? Cursor = null) : IQuery<FeedPage>;

public sealed record FeedItem(PostCard Post, AuthorCard Author);

/// <summary>
/// <paramref name="NextCursor"/> continues after the last item; null when nothing older remains.
/// <paramref name="Partial"/>: the page was served without one of its sources (a pulled author that
/// answered late or failed), so it may lack posts that are there (s3-l17).
/// </summary>
public sealed record FeedPage(IReadOnlyList<FeedItem> Items, string? NextCursor, bool Partial = false);

/// <summary>
/// A page of the feed: the timeline gives ids in time order, Posts and Accounts give their content in
/// one batched read each. Deleted or missing posts are dropped, so 25 are read for 20 and, if drops
/// leave the page short, the next 25, at most three rounds. A reader with no timeline gets one built
/// first: created empty (with its sentinel) before it is filled, so a fanout running during the
/// build already finds it and is not lost.
/// A later page starts at the cursor's score, inclusive, and drops the entries of that same score at
/// or above the cursor's post id: posts arriving at the top move no boundary, and two posts in the
/// same millisecond are each shown once.
/// The read filter decides what a page may show, whatever the timeline still holds: a post whose
/// author is hidden from the reader (a block either way, or the reader's mute), is not active, or is
/// no longer followed by the reader is dropped; the reader's own posts are always kept.
/// Hybrid fanout: the timeline is merged with the newest posts of the pulled authors the reader
/// follows, read at read time, deduplicated by post id (a post pushed before its author was promoted
/// is shown once) and ordered as the timeline orders: by score, then by post id, newest first.
/// A reader with no timeline (a returning user: it expired) gets page 1 from the 20 followed accounts
/// that posted most recently, and a rebuild job builds the timeline behind them (s3-l07).
/// </summary>
// Not the read side: a page builds and repairs the reader's timeline and asks for its rebuild job.
// Its reads of posts and profiles go through Posts' and Accounts' contracts, which read their reader endpoints.
[ReadsTheWriter]
public sealed class ReadFeedHandler(
    ITimelines timelines, IPostSummaries posts, IProfiles profiles, IFollowing following, IHiddenAuthors hidden, ISeenPosts seen, FeedCursors cursors,
    PulledReads? pulls = null, TimelineRebuilder? rebuilder = null, IRebuildJobs? rebuildJobs = null, FirstPageSources? firstPage = null,
    IReadRepair? repair = null)
    : IQueryHandler<ReadFeed, FeedPage>
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int Rounds = 3;

    /// <summary>
    /// A build takes each followed account's newest posts, 800 shared between them (at least 20 each),
    /// then keeps the newest 800 overall.
    /// </summary>
    public static int BuildPerAuthor(int authors) => Math.Clamp(TimelineRules.MaxEntries / Math.Max(authors, 1), 20, TimelineRules.MaxEntries);

    public async Task<Result<FeedPage>> Handle(ReadFeed query, CancellationToken cancellationToken)
    {
        var reader = query.ReaderId;
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        FeedPosition? after = null;
        if (query.Cursor is not null)
        {
            var position = cursors.Read(reader, query.Cursor);
            if (!position.IsSuccess)
            {
                return Result.Failure<FeedPage>(position.Errors);
            }

            after = position.Value;
        }

        var followed = (await following.Of(reader, cancellationToken)).ToHashSet();
        var filter = new ReadFilter(reader, followed, (await hidden.Of(reader, cancellationToken)).ToHashSet());
        if (!await timelines.Exists(reader, cancellationToken))
        {
            if (rebuilder is null)
            {
                await Build(reader, followed, cancellationToken); // no rebuild jobs here: build on the request
            }
            else if (after is null && rebuildJobs is not null && firstPage is not null)
            {
                // A returning reader: page 1 now, from the most recently active accounts; the timeline later.
                var first = await FirstPage(reader, followed, filter, limit, cancellationToken);
                await rebuildJobs.Request(reader, cancellationToken);
                return Result.Success(first);
            }
            else
            {
                await RebuildNow(reader, cancellationToken);
            }
        }

        var items = new List<FeedItem>();
        var read = 0;
        var exhausted = false;
        for (var round = 0; round < Rounds && items.Count < limit && !exhausted; round++)
        {
            var want = limit + 5;
            var entries = await timelines.Read(reader, after?.Score, read, want, cancellationToken);
            read += entries.Count;
            exhausted = entries.Count < want;
            items.AddRange((await Hydrate(reader, [.. entries.Where(e => After(e, after))], cancellationToken)).Where(filter.Shows));
        }

        // The pulled side, after the timeline: the two share this request's module reads, one at a time.
        var (pulled, pulledFull, partial) = pulls is null ? ([], false, false) : await PullPage(reader, followed, after, limit + 5, cancellationToken);
        var merged = items.Concat(pulled.Where(filter.Shows)).DistinctBy(i => i.Post.PostId)
            .OrderByDescending(i => TimelineRules.Score(i.Post.PostId))
            .ThenByDescending(i => TimelineRules.Member(i.Post.PostId), StringComparer.Ordinal)
            .ToList();

        // Only a read keeps a timeline alive; a fanout never does.
        await timelines.Touch(reader, cancellationToken);
        var page = merged.Take(limit).ToList();
        await seen.Saw(reader, [.. page.Select(i => i.Post)], cancellationToken);
        var more = page.Count > 0 && (merged.Count > limit || !exhausted || pulledFull);
        var next = more ? cursors.Issue(reader, new FeedPosition(TimelineRules.Score(page[^1].Post.PostId), page[^1].Post.PostId)) : null;
        return Result.Success(new FeedPage(page, next, partial));
    }

    /// <summary>A later page before the rebuild job got to it: rebuild here, or wait for the task that is.</summary>
    private async Task RebuildNow(Guid reader, CancellationToken cancellationToken)
    {
        if (await rebuilder!.Run(reader, cancellationToken))
        {
            return;
        }

        for (var waited = 0; waited < 100 && !await timelines.Exists(reader, cancellationToken); waited++)
        {
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>Stage 1: page 1 with no timeline, complete down to the line <see cref="FirstPageSources"/> draws.</summary>
    private async Task<FeedPage> FirstPage(Guid reader, IReadOnlySet<Guid> followed, ReadFilter filter, int limit, CancellationToken cancellationToken)
    {
        var want = limit + 5;
        var (cards, line) = await firstPage!.Read(reader, followed, want, cancellationToken);
        var authors = cards.Count == 0
            ? []
            : (await profiles.Get([.. cards.Select(c => c.AuthorId).Distinct()], cancellationToken)).ToDictionary(a => a.AccountId);
        var items = cards.Where(c => authors.ContainsKey(c.AuthorId)).Select(c => new FeedItem(c, authors[c.AuthorId])).Where(filter.Shows);
        var (pulled, pulledFull, partial) = pulls is null ? ([], false, false) : await PullPage(reader, followed, null, want, cancellationToken);
        var complete = items.Concat(pulled.Where(filter.Shows)).DistinctBy(i => i.Post.PostId)
            .Where(i => line is null || TimelineRules.Score(i.Post.PostId) > line)
            .OrderByDescending(i => TimelineRules.Score(i.Post.PostId))
            .ThenByDescending(i => TimelineRules.Member(i.Post.PostId), StringComparer.Ordinal)
            .ToList();

        var page = complete.Take(limit).ToList();
        await seen.Saw(reader, [.. page.Select(i => i.Post)], cancellationToken);
        var authorFull = cards.GroupBy(c => c.AuthorId).Any(g => g.Count() >= want);
        var more = page.Count > 0 && (complete.Count > limit || line is not null || authorFull || pulledFull);
        var next = more ? cursors.Issue(reader, new FeedPosition(TimelineRules.Score(page[^1].Post.PostId), page[^1].Post.PostId)) : null;
        return new FeedPage(page, next, partial);
    }

    private async Task<(IReadOnlyList<FeedItem> Items, bool Full, bool Partial)> PullPage(Guid reader, IReadOnlySet<Guid> followed, FeedPosition? after, int want, CancellationToken cancellationToken)
    {
        var authors = await pulls!.PullListOf(reader, followed, cancellationToken);
        // A post id is the timeline's (score, id) order: "older than the cursor's post" is the tie filter itself.
        return await pulls.Read([.. authors.Where(a => a != reader)], after?.PostId, want, cancellationToken);
    }

    /// <summary>The tie filter: at the cursor's own score, only ids below the cursor's are still to come.</summary>
    private static bool After(Entry entry, FeedPosition? cursor) =>
        cursor is null || entry.Score < cursor.Score
        || string.CompareOrdinal(TimelineRules.Member(entry.PostId), TimelineRules.Member(cursor.PostId)) < 0;

    private async Task Build(Guid reader, IReadOnlySet<Guid> followed, CancellationToken cancellationToken)
    {
        if (!await timelines.CreateEmpty(reader, cancellationToken))
        {
            return; // another request is building it, or a fanout's reader made it meanwhile
        }

        var authors = followed.Append(reader).Distinct().ToList();
        var recent = await posts.RecentByAuthors(authors, BuildPerAuthor(authors.Count), cancellationToken);
        await timelines.Fill(reader, [.. recent.OrderByDescending(p => TimelineRules.Score(p.PostId)).Take(TimelineRules.MaxEntries).Select(p => p.PostId)], cancellationToken);
    }

    /// <summary>
    /// Two batched reads for any number of entries: the posts, then their authors. An entry whose post
    /// Posts no longer has (deleted) is dropped here, and read repair takes it out of this timeline.
    /// </summary>
    private async Task<IEnumerable<FeedItem>> Hydrate(Guid reader, IReadOnlyList<Entry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var found = (await posts.Get([.. entries.Select(e => e.PostId)], cancellationToken)).ToDictionary(p => p.PostId);
        var gone = entries.Where(e => !found.ContainsKey(e.PostId)).Select(e => e.PostId).ToList();
        if (gone.Count > 0)
        {
            repair?.Remove(reader, gone);
        }

        var authors = (await profiles.Get([.. found.Values.Select(p => p.AuthorId).Distinct()], cancellationToken)).ToDictionary(a => a.AccountId);
        return entries
            .Where(e => found.ContainsKey(e.PostId) && authors.ContainsKey(found[e.PostId].AuthorId))
            .Select(e => new FeedItem(found[e.PostId], authors[found[e.PostId].AuthorId]));
    }
}

/// <summary>What one reader may see on a page, decided at read time from the current relationships.</summary>
public sealed class ReadFilter(Guid reader, IReadOnlySet<Guid> followed, IReadOnlySet<Guid> hidden)
{
    public bool Shows(FeedItem item)
    {
        var author = item.Author.AccountId;
        if (author == reader)
        {
            return true;
        }

        return item.Author.Active && !hidden.Contains(author) && followed.Contains(author);
    }
}
