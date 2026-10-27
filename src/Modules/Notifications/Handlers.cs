using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Infrastructure.Persistence;

namespace Notifications;

/// <summary>
/// Notifications decides with its own data only: its notifications and its own copy of blocks. It asks
/// no other module anything. Each handler runs inside the module's transaction, after its inbox row,
/// so an event delivered twice is handled once.
/// </summary>
internal sealed class NotificationHandlers(NotificationsDbContext db, INotificationSender sender) :
    IIntegrationEventHandler<FollowStarted>, IIntegrationEventHandler<AccountBlocked>, IIntegrationEventHandler<AccountUnblocked>,
    IIntegrationEventHandler<FollowerCountChanged>
{
    public const string NewFollower = "new_follower";

    /// <summary>The pulled-author threshold (Timeline's, s3-l05): above it an account gets one summary an hour.</summary>
    public const long Celebrity = 25_000;

    /// <summary>
    /// A follow is announced to the followee, unless a block of the pair is newer than it. The follows of
    /// one hour are one row, counted (s3-l13). An ordinary account is told of each follow as it comes; an
    /// account above the threshold is sent one summary when the hour is over (<see cref="HourlySummaries"/>).
    /// </summary>
    public async Task Handle(FollowStarted e, CancellationToken ct)
    {
        var newerBlock = await db.BlockedActors.AnyAsync(b =>
            ((b.BlockerId == e.FollowerId && b.BlockedId == e.FolloweeId) || (b.BlockerId == e.FolloweeId && b.BlockedId == e.FollowerId))
            && b.V > e.PairVersion, ct);
        if (newerBlock)
        {
            return; // the block removed this follow; it is never announced
        }

        var celebrity = await db.FollowerCounts.AnyAsync(c => c.AccountId == e.FolloweeId && c.Followers >= Celebrity, ct);
        var window = new DateTimeOffset(e.OccurredAt.Year, e.OccurredAt.Month, e.OccurredAt.Day, e.OccurredAt.Hour, 0, 0, TimeSpan.Zero);
        // One statement: the hour's row is made by its first follow and counted by every other. The
        // inbox row written before this handler makes a redelivered event count nothing.
        var row = (await db.Read("""
            INSERT INTO notifications.notifications (id, recipient_id, kind, actor_id, actors, count, window_start, created_at, updated_at, summary_due)
            VALUES (@id, @recipient, @kind, @actor, ARRAY[@actor]::uuid[], 1, @window, @at, @at, @celebrity)
            ON CONFLICT (recipient_id, kind, window_start) DO UPDATE SET
                count = notifications.notifications.count + 1,
                actors = CASE WHEN cardinality(notifications.notifications.actors) < 3
                              THEN notifications.notifications.actors || excluded.actors ELSE notifications.notifications.actors END,
                updated_at = excluded.updated_at,
                summary_due = notifications.notifications.summary_due OR excluded.summary_due
            RETURNING id, count
            """, r => (Id: r.GetGuid(0), Count: r.GetInt32(1)), ct,
            ("id", Guid.CreateVersion7(e.OccurredAt)), ("recipient", e.FolloweeId), ("kind", NewFollower), ("actor", e.FollowerId),
            ("window", window), ("at", e.OccurredAt), ("celebrity", celebrity))).Single();

        if (!celebrity)
        {
            await sender.Send(new OutgoingNotification(e.FolloweeId, NewFollower, e.FollowerId, row.Id, row.Count), ct);
        }
    }

    /// <summary>Keeps each account's latest count: whether its new followers come one by one or as an hourly summary.</summary>
    public async Task Handle(FollowerCountChanged e, CancellationToken ct) =>
        await db.Execute("""
            INSERT INTO notifications.follower_counts (account_id, followers, at) VALUES (@account, @followers, @at)
            ON CONFLICT (account_id) DO UPDATE SET followers = excluded.followers, at = excluded.at
            WHERE notifications.follower_counts.at < excluded.at
            """, ct, ("account", e.AccountId), ("followers", e.Followers), ("at", e.OccurredAt));

    /// <summary>The block is copied, and each person's notifications about the other are deleted.</summary>
    public async Task Handle(AccountBlocked e, CancellationToken ct)
    {
        if (await Copy(e.BlockerId, e.BlockedId, e.PairVersion, on: true, e.OccurredAt, ct))
        {
            // An hour's row names at most three actors: the blocked one is taken out of the rows that
            // name them, and a row left with no one is deleted. (A follower beyond the first three is
            // only in the count, which is left as it is.)
            await TakeOut(recipient: e.BlockerId, actor: e.BlockedId, ct);
            await TakeOut(recipient: e.BlockedId, actor: e.BlockerId, ct);
            await db.Notifications.Where(n => n.Count <= 0 || n.Actors.Length == 0).Where(n => n.RecipientId == e.BlockerId || n.RecipientId == e.BlockedId)
                .ExecuteDeleteAsync(ct);
        }
    }

    public async Task Handle(AccountUnblocked e, CancellationToken ct) =>
        await Copy(e.BlockerId, e.BlockedId, e.PairVersion, on: false, e.OccurredAt, ct);

    private Task<int> TakeOut(Guid recipient, Guid actor, CancellationToken ct) =>
        db.Execute("""
            UPDATE notifications.notifications
            SET actors = array_remove(actors, @actor), count = count - 1, actor_id = coalesce((array_remove(actors, @actor))[1], actor_id)
            WHERE recipient_id = @recipient AND @actor = ANY(actors)
            """, ct, ("recipient", recipient), ("actor", actor));

    /// <summary>Writes the block's state only if this event's version is newer than the copy's. True when written.</summary>
    private async Task<bool> Copy(Guid blocker, Guid blocked, long version, bool on, DateTimeOffset at, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO notifications.blocked_actors (blocker_id, blocked_id, v, "on", changed_at) VALUES ({blocker}, {blocked}, {version}, {on}, {at})
            ON CONFLICT (blocker_id, blocked_id) DO UPDATE SET v = excluded.v, "on" = excluded."on", changed_at = excluded.changed_at
            WHERE notifications.blocked_actors.v < excluded.v
            """, ct) == 1;
}

/// <summary>
/// A celebrity's new followers as one summary an hour (s3-l13): every hour's row of an account above
/// the threshold, once its hour is over, is sent once with its count. Every five minutes on workers;
/// the row is marked in the statement that picks it, so two tasks never send one summary twice.
/// </summary>
internal sealed class HourlySummaries(IServiceScopeFactory scopes, TimeProvider clock) : BackgroundService
{
    public async Task<int> SendDue(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        var now = clock.GetUtcNow();
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var due = await db.Read("""
            UPDATE notifications.notifications SET summarized_at = @now
            WHERE id IN (SELECT id FROM notifications.notifications
                         WHERE summary_due AND summarized_at IS NULL AND window_start < @hour
                         ORDER BY window_start LIMIT 1000 FOR UPDATE SKIP LOCKED)
            RETURNING id, recipient_id, actor_id, count
            """, r => new OutgoingNotification(r.GetGuid(1), NotificationHandlers.NewFollower, r.GetGuid(2), r.GetGuid(0), r.GetInt32(3)),
            cancellationToken, ("now", now), ("hour", hour));
        foreach (var summary in due)
        {
            await sender.Send(summary, cancellationToken);
        }

        return due.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SendDue(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException and not ObjectDisposedException)
            {
                // the next pass tries again; a summary marked but not sent is lost (best-effort, like any push)
            }
        }
    }
}
