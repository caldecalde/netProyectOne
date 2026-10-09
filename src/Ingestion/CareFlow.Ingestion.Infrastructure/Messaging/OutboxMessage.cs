using System.Diagnostics;
using CareFlow.BuildingBlocks.Messaging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure.Messaging;

internal enum OutboxStatus
{
    Pending = 0,
    Published = 1,
}

/// <summary>An integration event committed with the state change that produced it, waiting to be relayed to SNS.</summary>
internal sealed class OutboxMessage
{
    public const string CollectionName = "outbox";

    /// <summary>The event's own ID, so a message can never be stored twice.</summary>
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    /// <summary>Serialized event body, exactly as it will be published.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// W3C trace context of the request that produced the event. The relay publishes inside this context,
    /// so the trace continues from the upload through to the consumer even though publishing happens later.
    /// </summary>
    [BsonIgnoreIfNull]
    public string? TraceParent { get; set; }

    [BsonIgnoreIfNull]
    public string? TraceState { get; set; }

    [BsonRepresentation(BsonType.String)]
    public OutboxStatus Status { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    /// <summary>When the message may next be claimed: now for new messages, later while leased or backing off.</summary>
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime AvailableAt { get; set; }

    public int Attempts { get; set; }

    [BsonIgnoreIfNull]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PublishedAt { get; set; }

    /// <summary>Exception type of the last failed attempt. Never the message: it could echo payload content.</summary>
    [BsonIgnoreIfNull]
    public string? LastError { get; set; }

    public static OutboxMessage From(IntegrationEvent integrationEvent, Activity? activity, DateTimeOffset now)
    {
        var envelope = EventSerializer.Serialize(integrationEvent);
        return new OutboxMessage
        {
            Id = envelope.EventId,
            EventType = envelope.EventType,
            Payload = envelope.Body,
            TraceParent = activity?.Id is { } id && activity.IdFormat == ActivityIdFormat.W3C ? id : null,
            TraceState = activity?.TraceStateString,
            Status = OutboxStatus.Pending,
            CreatedAt = now.UtcDateTime,
            AvailableAt = now.UtcDateTime,
        };
    }

    public static Task EnsureIndexesAsync(IMongoDatabase database, TimeSpan retention, CancellationToken cancellationToken)
    {
        var keys = Builders<OutboxMessage>.IndexKeys;
        return database.GetCollection<OutboxMessage>(CollectionName).Indexes.CreateManyAsync(
            [
                new CreateIndexModel<OutboxMessage>(
                    keys.Ascending(m => m.Status).Ascending(m => m.AvailableAt),
                    new CreateIndexOptions { Name = "ix_status_available" }),

                // Published messages are kept for a while for troubleshooting, then expire. Pending ones never do.
                new CreateIndexModel<OutboxMessage>(
                    keys.Ascending(m => m.PublishedAt),
                    new CreateIndexOptions { Name = "ttl_published", ExpireAfter = retention }),
            ],
            cancellationToken);
    }
}
