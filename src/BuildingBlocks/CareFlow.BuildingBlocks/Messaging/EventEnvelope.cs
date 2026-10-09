using System.Text.Json;

namespace CareFlow.BuildingBlocks.Messaging;

/// <summary>An integration event in wire form: its identity, its type (used for routing) and its JSON body.</summary>
public sealed record EventEnvelope(Guid EventId, string EventType, string Body);

/// <summary>The single place that decides how events look on the wire, so producers and consumers always agree.</summary>
public static class EventSerializer
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes with the runtime type, so an event held as <see cref="IntegrationEvent"/> keeps all its fields.</summary>
    public static EventEnvelope Serialize(IntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return new EventEnvelope(
            integrationEvent.EventId,
            integrationEvent.EventType,
            JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Options));
    }

    /// <exception cref="JsonException">The body is not valid JSON or a required field is missing.</exception>
    public static IntegrationEvent Deserialize(string body, Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return JsonSerializer.Deserialize(body, eventType, Options) as IntegrationEvent
            ?? throw new JsonException($"Message body is not a {eventType.Name}.");
    }
}
