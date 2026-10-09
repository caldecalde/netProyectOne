namespace CareFlow.BuildingBlocks.Messaging;

/// <summary>
/// Base type for events published between services. Contracts are versioned by type name:
/// breaking changes get a new type (e.g. <c>FileReceivedV2</c>), never an edited one.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>The event that caused this one, when it was produced by a consumer. Useful for tracing a pipeline.</summary>
    public Guid? CausationId { get; init; }

    /// <summary>Value of the <c>event-type</c> message attribute used for SNS subscription filtering.</summary>
    public string EventType => GetType().Name;
}

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent;
}

/// <summary>Publishes an already-serialized event, e.g. one relayed from a transactional outbox.</summary>
public interface IEventEnvelopePublisher
{
    Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Handles one integration event delivered by a queue consumer. Throwing means "retry later".</summary>
public interface IMessageHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
