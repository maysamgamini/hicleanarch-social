using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

using Timeline.Domain;

namespace Timeline.Application;

/// <summary>The reader's home feed: their newest posts, from the accounts they follow and their own.</summary>
public sealed record ReadFeed(Guid ReaderId, int Limit, string? Cursor = null) : IQuery<FeedPage>;

public sealed record FeedItem(PostCard Post, AuthorCard Author);

/// <summary><paramref name="NextCursor"/> continues after the last item; null when nothing older remains.</summary>
public sealed record FeedPage(IReadOnlyList<FeedItem> Items, string? NextCursor);

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
/// </summary>
public sealed class ReadFeedHandler(
    ITimelines timelines, IPostSummaries posts, IProfiles profiles, IFollowing following, IHiddenAuthors hidden, ISeenPosts seen, FeedCursors cursors,
    PulledReads? pulls = null)
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
        if (!await timelines.Exists(reader, cancellationToken))
        {
            await Build(reader, followed, cancellationToken);
        }

        var filter = new ReadFilter(reader, followed, (await hidden.Of(reader, cancellationToken)).ToHashSet());

        var items = new List<FeedItem>();
        var read = 0;
        var exhausted = false;
        for (var round = 0; round < Rounds && items.Count < limit && !exhausted; round++)
        {
            var want = limit + 5;
            var entries = await timelines.Read(reader, after?.Score, read, want, cancellationToken);
            read += entries.Count;
            exhausted = entries.Count < want;
            items.AddRange((await Hydrate([.. entries.Where(e => After(e, after))], cancellationToken)).Where(filter.Shows));
        }

        // The pulled side, after the timeline: the two share this request's module reads, one at a time.
        var (pulled, pulledFull) = pulls is null ? ([], false) : await PullPage(reader, followed, after, limit + 5, cancellationToken);
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
        return Result.Success(new FeedPage(page, next));
    }

    private async Task<(IReadOnlyList<FeedItem> Items, bool Full)> PullPage(Guid reader, IReadOnlySet<Guid> followed, FeedPosition? after, int want, CancellationToken cancellationToken)
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

    /// <summary>Two batched reads for any number of entries: the posts, then their authors.</summary>
    private async Task<IEnumerable<FeedItem>> Hydrate(IReadOnlyList<Entry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var found = (await posts.Get([.. entries.Select(e => e.PostId)], cancellationToken)).ToDictionary(p => p.PostId);
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
