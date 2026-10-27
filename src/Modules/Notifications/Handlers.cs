using Microsoft.EntityFrameworkCore;

using Relationships.Contracts;

using Social.BuildingBlocks.Application;

namespace Notifications;

/// <summary>
/// Notifications decides with its own data only: its notifications and its own copy of blocks. It asks
/// no other module anything. Each handler runs inside the module's transaction, after its inbox row,
/// so an event delivered twice is handled once.
/// </summary>
internal sealed class NotificationHandlers(NotificationsDbContext db, INotificationSender sender) :
    IIntegrationEventHandler<FollowStarted>, IIntegrationEventHandler<AccountBlocked>, IIntegrationEventHandler<AccountUnblocked>
{
    public const string NewFollower = "new_follower";

    /// <summary>A follow is announced to the followee, unless a block of the pair is newer than it.</summary>
    public async Task Handle(FollowStarted e, CancellationToken ct)
    {
        var newerBlock = await db.BlockedActors.AnyAsync(b =>
            ((b.BlockerId == e.FollowerId && b.BlockedId == e.FolloweeId) || (b.BlockerId == e.FolloweeId && b.BlockedId == e.FollowerId))
            && b.V > e.PairVersion, ct);
        if (newerBlock)
        {
            return; // the block removed this follow; it is never announced
        }

        var notification = new Notification
        {
            Id = Guid.CreateVersion7(e.OccurredAt),
            RecipientId = e.FolloweeId,
            Kind = NewFollower,
            ActorId = e.FollowerId,
            CreatedAt = e.OccurredAt,
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);
        await sender.Send(new OutgoingNotification(notification.RecipientId, notification.Kind, notification.ActorId, notification.Id), ct);
    }

    /// <summary>The block is copied, and each person's notifications about the other are deleted.</summary>
    public async Task Handle(AccountBlocked e, CancellationToken ct)
    {
        if (await Copy(e.BlockerId, e.BlockedId, e.PairVersion, on: true, e.OccurredAt, ct))
        {
            await db.Notifications
                .Where(n => (n.RecipientId == e.BlockerId && n.ActorId == e.BlockedId) || (n.RecipientId == e.BlockedId && n.ActorId == e.BlockerId))
                .ExecuteDeleteAsync(ct);
        }
    }

    public async Task Handle(AccountUnblocked e, CancellationToken ct) =>
        await Copy(e.BlockerId, e.BlockedId, e.PairVersion, on: false, e.OccurredAt, ct);

    /// <summary>Writes the block's state only if this event's version is newer than the copy's. True when written.</summary>
    private async Task<bool> Copy(Guid blocker, Guid blocked, long version, bool on, DateTimeOffset at, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO notifications.blocked_actors (blocker_id, blocked_id, v, "on", changed_at) VALUES ({blocker}, {blocked}, {version}, {on}, {at})
            ON CONFLICT (blocker_id, blocked_id) DO UPDATE SET v = excluded.v, "on" = excluded."on", changed_at = excluded.changed_at
            WHERE notifications.blocked_actors.v < excluded.v
            """, ct) == 1;
}
