using System.Text.Json;
using System.Threading.Channels;

using Social.BuildingBlocks.Application;

namespace Social.BuildingBlocks.Infrastructure.Events;

/// <summary>
/// A committed outbox row, as it travels inside the process. <see cref="TraceParent"/> is the W3C id
/// of the trace that wrote it, so the handlers' work continues that trace, across modules.
/// </summary>
public sealed record OutboxRecord(string Module, Guid EventId, string Type, string Payload, string? TraceParent = null);

/// <summary>
/// The in-process transport: committed events wait here for the dispatcher. A task that runs no
/// dispatcher (a web task) holds nothing: its committed rows wait in the outbox for a worker.
/// </summary>
public sealed class EventBus(bool holdsCommittedEvents = true)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Channel<OutboxRecord> _channel = Channel.CreateUnbounded<OutboxRecord>();

    public void Publish(OutboxRecord record)
    {
        if (holdsCommittedEvents)
        {
            _channel.Writer.TryWrite(record);
        }
    }

    internal ChannelReader<OutboxRecord> Reader => _channel.Reader;

    internal static OutboxMessage ToMessage(IntegrationEvent integrationEvent) => new()
    {
        Id = integrationEvent.EventId,
        Type = integrationEvent.GetType().Name,
        Version = integrationEvent.Version,
        OccurredAt = integrationEvent.OccurredAt,
        Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Json),
        TraceParent = System.Diagnostics.Activity.Current?.Id,
    };
}
