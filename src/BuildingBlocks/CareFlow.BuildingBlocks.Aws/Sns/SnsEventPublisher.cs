using System.ComponentModel.DataAnnotations;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using CareFlow.BuildingBlocks.Messaging;
using Microsoft.Extensions.Options;

namespace CareFlow.BuildingBlocks.Aws.Sns;

public sealed class SnsPublisherOptions
{
    [Required]
    public string TopicArn { get; set; } = string.Empty;
}

/// <summary>
/// Publishes integration events to a single SNS topic. Consumers subscribe SQS queues filtered on the
/// <c>event-type</c> attribute (fan-out without coupling producers to consumers). The OpenTelemetry AWS
/// instrumentation adds <c>traceparent</c> to the message attributes, parented to the current activity.
/// </summary>
internal sealed class SnsEventPublisher(IAmazonSimpleNotificationService sns, IOptions<SnsPublisherOptions> options)
    : IEventPublisher, IEventEnvelopePublisher
{
    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent =>
        PublishAsync(EventSerializer.Serialize(integrationEvent), cancellationToken);

    public async Task PublishAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var request = new PublishRequest
        {
            TopicArn = options.Value.TopicArn,
            Message = envelope.Body,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [MessageAttributes.EventType] = new() { DataType = "String", StringValue = envelope.EventType },
                [MessageAttributes.EventId] = new() { DataType = "String", StringValue = envelope.EventId.ToString() },
            },
        };

        await sns.PublishAsync(request, cancellationToken);
    }
}
