using Microsoft.Extensions.DependencyInjection;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

using Timeline.Domain;

namespace Timeline.Application;

/// <summary>A stored post's fanout is recorded as a job; worker tasks write it a page at a time.</summary>
public sealed record StartFanout(Guid PostId, Guid AuthorId, DateTimeOffset StoredAt) : ICommand<FanoutPool?>;

/// <summary>A fanout job as a worker holds it: where it stands, and the lease that makes it this worker's.</summary>
public sealed record FanoutJob(Guid PostId, Guid AuthorId, FanoutPool Pool, Guid? Cursor, bool AuthorDone, long Reached, Guid LeaseToken, FanoutKind Kind = FanoutKind.Add);

/// <summary>
/// The durable fanout jobs, in Timeline's own schema. A job is claimed with a lease; every step moves
/// it only while the lease is still the caller's, so two workers never write the same page at once,
/// and a worker that dies leaves a job the next one takes up from its last confirmed page.
/// </summary>
public interface IFanoutJobs
{
    /// <summary>Creates the job, unless the post already has one of this kind (a redelivered event).</summary>
    Task Create(Guid postId, Guid authorId, long audience, FanoutPool pool, CancellationToken cancellationToken, FanoutKind kind = FanoutKind.Add);

    /// <summary>The oldest job of the pool that nobody holds, now held by the caller for <paramref name="lease"/>.</summary>
    Task<FanoutJob?> Claim(FanoutPool pool, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Records a written page and renews the lease. False: the lease was lost, and the job is someone else's.</summary>
    Task<bool> Advance(FanoutJob job, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Marks the job done. False: the lease was lost.</summary>
    Task<bool> Finish(FanoutJob job, CancellationToken cancellationToken);

    /// <summary>Gives the job back at once, for the next worker, when this one stops.</summary>
    Task Release(FanoutJob job, CancellationToken cancellationToken);
}

/// <summary>A pool's pace: each page waits its turn, so the pool writes no faster than its current rate.</summary>
public interface IFanoutPace
{
    Task WaitTurn(FanoutPool pool, CancellationToken cancellationToken);
}

/// <summary>How many followers an author has, as the source of truth counts them now.</summary>
public interface IAudience
{
    Task<long> Followers(Guid author, CancellationToken cancellationToken);
}

/// <summary>
/// PostPublished becomes a job row (and its progress row) in Timeline's schema; the audience size,
/// read once here, picks the pool. Creating it twice changes nothing.
/// A pulled author's post is not fanned out: it goes into the author's own timeline only, and is
/// fanned out (complete) at once, since its followers read it at read time. Null: not fanned out.
/// </summary>
public sealed class StartFanoutHandler(IFanoutJobs jobs, IAudience audience, IFanoutProgress progress, IPullRegistry registry, ITimelines timelines, TimeProvider clock)
    : ICommandHandler<StartFanout, FanoutPool?>
{
    public async Task<Result<FanoutPool?>> Handle(StartFanout command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (await registry.Find(command.AuthorId, cancellationToken) is { } pulled && !pulled.Pushed(now))
        {
            await progress.Start(command.PostId, command.AuthorId, command.StoredAt, cancellationToken);
            await timelines.AddToExisting([command.AuthorId], command.PostId, cancellationToken);
            await progress.Complete(command.PostId, now, cancellationToken);
            return Result.Success<FanoutPool?>(null);
        }

        var size = await audience.Followers(command.AuthorId, cancellationToken);
        var pool = FanoutPools.For(size);
        await progress.Start(command.PostId, command.AuthorId, command.StoredAt, cancellationToken);
        await jobs.Create(command.PostId, command.AuthorId, size, pool, cancellationToken);
        return Result.Success<FanoutPool?>(pool);
    }
}

/// <summary>
/// One worker's turn: claim a job of its pool, then write it a page at a time (the author first), moving
/// the cursor only after Valkey has confirmed the page. A page written again after a crash changes
/// nothing (the add is idempotent); a page is never skipped.
/// </summary>
public sealed class FanoutRunner(IFanoutJobs jobs, ITimelines timelines, IFollowerPages followers, IFanoutProgress progress, IFanoutPace pace, TimeProvider clock)
{
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(30);

    /// <summary>Runs one job to its end, or until its lease is lost. False when the pool had no job to give.</summary>
    public async Task<bool> RunOne(FanoutPool pool, TimeSpan lease, Action<FanoutJob>? holding, CancellationToken cancellationToken)
    {
        var job = await jobs.Claim(pool, lease, cancellationToken);
        if (job is null)
        {
            return false;
        }

        holding?.Invoke(job);
        if (!job.AuthorDone)
        {
            // The author reads their own post in their own feed.
            await Write(job, [job.AuthorId], cancellationToken);
            job = job with { AuthorDone = true };
            if (!await jobs.Advance(job, lease, cancellationToken))
            {
                return true;
            }
        }

        while (true)
        {
            var (page, next) = await followers.Page(job.AuthorId, job.Cursor, FanoutPools.PageSize, cancellationToken);
            await pace.WaitTurn(job.Pool, cancellationToken);
            await Write(job, page, cancellationToken);
            job = job with { Cursor = next ?? (page.Count > 0 ? page[^1] : job.Cursor), Reached = job.Reached + page.Count };
            holding?.Invoke(job);
            if (next is null)
            {
                if (await jobs.Finish(job, cancellationToken) && job.Kind == FanoutKind.Add)
                {
                    // Fanned out: the last existing follower timeline has the post.
                    await progress.Reached(job.PostId, job.Reached, cancellationToken);
                    await progress.Complete(job.PostId, clock.GetUtcNow(), cancellationToken);
                }

                return true;
            }

            if (!await jobs.Advance(job, lease, cancellationToken))
            {
                return true; // the lease ran out; the job is another worker's now
            }

            if (job.Kind == FanoutKind.Add)
            {
                await progress.Reached(job.PostId, job.Reached, cancellationToken);
            }
        }
    }

    private Task Write(FanoutJob job, IReadOnlyList<Guid> owners, CancellationToken cancellationToken) => job.Kind == FanoutKind.Add
        ? timelines.AddToExisting(owners, job.PostId, cancellationToken)
        : timelines.RemoveFrom(owners, job.PostId, cancellationToken);
}

public static class TimelineApplication
{
    public static IServiceCollection AddTimelineApplication(this IServiceCollection services) =>
        services
            .AddScoped<ICommandHandler<StartFanout, FanoutPool?>, StartFanoutHandler>()
            .AddScoped<FanoutRunner>()
            .AddScoped<IQueryHandler<ReadFeed, FeedPage>, ReadFeedHandler>()
            .AddScoped<ICommandHandler<RemoveAuthor, int>, RemoveAuthorHandler>()
            .AddScoped<ICommandHandler<AddRecentPosts, int>, AddRecentPostsHandler>()
            .AddScoped<IQueryHandler<GetDelivery, Delivery>, GetDeliveryHandler>()
            .AddHybridFanout()
            .AddScoped<TimelineRebuilder>()
            .AddScoped<ICommandHandler<RemoveDeletedPost, bool>, RemoveDeletedPostHandler>()
            .AddScoped<FirstPageSources>()
            .AddScoped<ICommandHandler<RunRebuild, bool>, RunRebuildHandler>();
}
