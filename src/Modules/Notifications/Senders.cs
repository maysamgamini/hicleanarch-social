using System.Collections.Concurrent;

namespace Notifications;

/// <summary>
/// What a person would be told on their phone or by email. Count is how many new followers the hour's
/// row holds so far; a celebrity's hourly summary carries the hour's total (s3-l13).
/// </summary>
public sealed record OutgoingNotification(Guid RecipientId, string Kind, Guid ActorId, Guid NotificationId, int Count = 1);

/// <summary>
/// The port to push and email providers. No real provider is wired: the fake below is what runs locally
/// and in every test; a real one (SNS mobile push, SES) is a learner exercise behind this same port.
/// </summary>
public interface INotificationSender
{
    Task Send(OutgoingNotification notification, CancellationToken cancellationToken);
}

/// <summary>Records what would have been sent, and sends nothing.</summary>
internal sealed class FakeNotificationSender : INotificationSender
{
    public ConcurrentQueue<OutgoingNotification> Sent { get; } = new();

    public Task Send(OutgoingNotification notification, CancellationToken cancellationToken)
    {
        Sent.Enqueue(notification);
        return Task.CompletedTask;
    }
}
