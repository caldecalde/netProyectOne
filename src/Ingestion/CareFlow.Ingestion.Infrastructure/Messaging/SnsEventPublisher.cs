using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using CareFlow.BuildingBlocks.Messaging;
using Microsoft.Extensions.Options;

namespace CareFlow.Ingestion.Infrastructure.Messaging;

/// <summary>
/// Publishes integration events to a single SNS topic. Consumers subscribe SQS queues filtered on the
/// <c>event-type</c> attribute (fan-out without coupling producers to consumers).
/// </summary>
internal sealed class SnsEventPublisher(IAmazonSimpleNotificationService sns, IOptions<AwsOptions> options) : IEventPublisher
{
    public const string EventTypeAttribute = "event-type";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var request = new PublishRequest
        {
            TopicArn = options.Value.EventsTopicArn,
            Message = JsonSerializer.Serialize(integrationEvent, JsonOptions),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [EventTypeAttribute] = new() { DataType = "String", StringValue = integrationEvent.EventType },
            },
        };

        await sns.PublishAsync(request, cancellationToken);
    }
}
