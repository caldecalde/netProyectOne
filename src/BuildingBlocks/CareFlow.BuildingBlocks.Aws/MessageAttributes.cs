namespace CareFlow.BuildingBlocks.Aws;

/// <summary>Message attribute names shared by every producer and consumer.</summary>
public static class MessageAttributes
{
    /// <summary>Routing key for SNS filter policies; the event's type name.</summary>
    public const string EventType = "event-type";

    public const string EventId = "event-id";

    /// <summary>W3C trace context, written by the OpenTelemetry AWS instrumentation.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>Why a consumer moved a message straight to its dead-letter queue.</summary>
    public const string DeadLetterReason = "careflow-dead-letter-reason";

    public const string SourceQueue = "careflow-source-queue";
}
