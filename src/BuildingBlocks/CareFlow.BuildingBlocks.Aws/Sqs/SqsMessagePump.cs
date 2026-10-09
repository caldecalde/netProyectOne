using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using Polly;
using Polly.Retry;

namespace CareFlow.BuildingBlocks.Aws.Sqs;

/// <summary>
/// Long-polls one SQS queue and dispatches each message to the handler registered for its <c>event-type</c>.
/// <list type="bullet">
/// <item>Success deletes the message. Delivery is at-least-once, so handlers must be idempotent.</item>
/// <item>A failing handler is retried in-process (Polly), then the message is handed back to SQS with an
/// exponential visibility backoff. After <c>maxReceiveCount</c> receives the redrive policy moves it to the DLQ.</item>
/// <item>A message that can never succeed (unknown type, unreadable body) goes to the DLQ at once.</item>
/// </list>
/// Processing continues the producer's trace from the <c>traceparent</c> message attribute.
/// </summary>
internal sealed partial class SqsMessagePump(
    SqsConsumerRegistration registration,
    SqsConsumerOptions options,
    IAmazonSQS sqs,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SqsMessagePump> logger) : BackgroundService
{
    private const string ReceiveCountAttribute = "ApproximateReceiveCount";
    private static readonly TimeSpan MaxVisibilityTimeout = TimeSpan.FromMinutes(15);

    private readonly ILogger<SqsMessagePump> _logger = logger;
    private readonly ResiliencePipeline _handlerRetry = BuildHandlerRetry(options, timeProvider);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(registration.Name, string.Join(", ", registration.Handlers.Keys));

        while (!stoppingToken.IsCancellationRequested)
        {
            List<Message> messages;
            try
            {
                messages = await ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogReceiveFailed(ex, registration.Name, options.ReceiveErrorDelay);
                await DelayAsync(options.ReceiveErrorDelay, stoppingToken);
                continue;
            }

            // In-flight messages finish even when the host is stopping; the host's shutdown timeout bounds the wait.
            await Parallel.ForEachAsync(
                messages,
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency },
                async (message, _) => await ProcessAsync(message, CancellationToken.None));
        }
    }

    internal async Task ProcessAsync(Message message, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var receiveCount = ReceiveCount(message);
        var eventType = StringAttribute(message, MessageAttributes.EventType);

        var parent = Propagators.DefaultTextMapPropagator.Extract(default, message, ExtractAttribute);
        Baggage.Current = parent.Baggage;
        using var activity = CareFlowTelemetry.ActivitySource.StartActivity(
            $"{registration.Name} process", ActivityKind.Consumer, parent.ActivityContext);
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", registration.Name);
        activity?.SetTag("messaging.message.id", message.MessageId);
        activity?.SetTag("careflow.event_type", eventType);
        activity?.SetTag("careflow.receive_count", receiveCount);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["Queue"] = registration.Name,
            ["MessageId"] = message.MessageId,
            ["EventType"] = eventType,
        });

        string outcome;
        try
        {
            outcome = await DispatchAsync(message, eventType, receiveCount, activity, cancellationToken);
        }
        catch (Exception ex)
        {
            // SQS itself failed (delete, DLQ send). The message reappears after its visibility timeout; the pump keeps running.
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            LogQueueOperationFailed(ex, registration.Name);
            outcome = "error";
        }

        activity?.SetTag("careflow.outcome", outcome);
        var tags = new TagList { { "queue", registration.Name }, { "outcome", outcome } };
        CareFlowTelemetry.MessagesConsumed.Add(1, tags);
        CareFlowTelemetry.MessageProcessingDuration.Record(timeProvider.GetElapsedTime(started).TotalSeconds, tags);
    }

    private async Task<string> DispatchAsync(Message message, string? eventType, int receiveCount, Activity? activity, CancellationToken cancellationToken)
    {
        if (eventType is null || !registration.Handlers.TryGetValue(eventType, out var handler))
        {
            await DeadLetterAsync(message, "unknown_event_type", cancellationToken);
            return "dead_lettered";
        }

        IntegrationEvent integrationEvent;
        try
        {
            integrationEvent = EventSerializer.Deserialize(message.Body ?? string.Empty, handler.EventType);
        }
        catch (JsonException)
        {
            // The body is never logged: it may carry identifiers we do not want in log storage.
            await DeadLetterAsync(message, "invalid_body", cancellationToken);
            return "dead_lettered";
        }

        activity?.SetTag("careflow.event_id", integrationEvent.EventId);

        try
        {
            await _handlerRetry.ExecuteAsync(
                async ct =>
                {
                    await using var serviceScope = scopeFactory.CreateAsyncScope();
                    await handler.Invoke(serviceScope.ServiceProvider, integrationEvent, ct);
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.SetTag("error.type", ex.GetType().FullName);
            return await ReturnToQueueAsync(message, integrationEvent.EventId, receiveCount, ex, cancellationToken);
        }

        await sqs.DeleteMessageAsync(options.QueueUrl, message.ReceiptHandle, cancellationToken);
        return "processed";
    }

    private async Task<string> ReturnToQueueAsync(Message message, Guid eventId, int receiveCount, Exception error, CancellationToken cancellationToken)
    {
        var delay = RedeliveryDelay(receiveCount);
        var finalAttempt = receiveCount >= options.MaxReceiveCount;
        if (finalAttempt)
        {
            LogFinalAttemptFailed(error, eventId, receiveCount);
        }
        else
        {
            LogAttemptFailed(error, eventId, receiveCount, options.MaxReceiveCount, delay);
        }

        try
        {
            // On the final attempt, release the message now so the redrive policy moves it to the DLQ without waiting.
            var visibility = finalAttempt ? TimeSpan.Zero : delay;
            await sqs.ChangeMessageVisibilityAsync(options.QueueUrl, message.ReceiptHandle, (int)visibility.TotalSeconds, cancellationToken);
        }
        catch (AmazonSQSException ex)
        {
            // Not fatal: the message reappears when its original visibility timeout ends.
            LogVisibilityChangeFailed(ex, eventId);
        }

        return finalAttempt ? "exhausted" : "retry";
    }

    private async Task DeadLetterAsync(Message message, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.DeadLetterQueueUrl))
        {
            LogPoisonWithoutDlq(reason);
            return;
        }

        var attributes = message.MessageAttributes is null
            ? new Dictionary<string, MessageAttributeValue>()
            : new Dictionary<string, MessageAttributeValue>(message.MessageAttributes);
        attributes[MessageAttributes.DeadLetterReason] = new() { DataType = "String", StringValue = reason };
        attributes[MessageAttributes.SourceQueue] = new() { DataType = "String", StringValue = registration.Name };

        await sqs.SendMessageAsync(
            new SendMessageRequest { QueueUrl = options.DeadLetterQueueUrl, MessageBody = message.Body, MessageAttributes = attributes },
            cancellationToken);
        await sqs.DeleteMessageAsync(options.QueueUrl, message.ReceiptHandle, cancellationToken);
        LogDeadLettered(reason);
    }

    private async Task<List<Message>> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Idle long-polls would otherwise create an empty root trace every few seconds.
        using var suppress = SuppressInstrumentationScope.Begin();
        var response = await sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = options.QueueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = options.WaitTimeSeconds,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = [ReceiveCountAttribute],
            },
            cancellationToken);
        return response.Messages ?? [];
    }

    private static ResiliencePipeline BuildHandlerRetry(SqsConsumerOptions options, TimeProvider timeProvider) =>
        options.HandlerRetryAttempts == 0
            ? ResiliencePipeline.Empty
            : new ResiliencePipelineBuilder { TimeProvider = timeProvider }
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = options.HandlerRetryAttempts,
                    Delay = options.HandlerRetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
                })
                .Build();

    private TimeSpan RedeliveryDelay(int receiveCount)
    {
        var factor = Math.Pow(2, Math.Clamp(receiveCount - 1, 0, 16));
        var delay = TimeSpan.FromSeconds(options.RedeliveryBaseDelay.TotalSeconds * factor);
        return delay < MaxVisibilityTimeout ? delay : MaxVisibilityTimeout;
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private static int ReceiveCount(Message message) =>
        message.Attributes is not null
        && message.Attributes.TryGetValue(ReceiveCountAttribute, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? count
            : 1;

    private static string? StringAttribute(Message message, string name) =>
        message.MessageAttributes is not null && message.MessageAttributes.TryGetValue(name, out var value)
            ? value.StringValue
            : null;

    private static IEnumerable<string> ExtractAttribute(Message message, string name) =>
        StringAttribute(message, name) is { } value ? [value] : [];

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue} for {EventTypes}")]
    private partial void LogStarted(string queue, string eventTypes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receive from {Queue} failed; retrying in {Delay}")]
    private partial void LogReceiveFailed(Exception exception, string queue, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling event {IntegrationEventId} failed on attempt {ReceiveCount} of {MaxReceiveCount}; redelivery in {Delay}")]
    private partial void LogAttemptFailed(Exception exception, Guid integrationEventId, int receiveCount, int maxReceiveCount, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling event {IntegrationEventId} failed on final attempt {ReceiveCount}; the redrive policy moves it to the dead-letter queue")]
    private partial void LogFinalAttemptFailed(Exception exception, Guid integrationEventId, int receiveCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Queue operation on {Queue} failed; the message will be redelivered")]
    private partial void LogQueueOperationFailed(Exception exception, string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not change visibility of event {IntegrationEventId}")]
    private partial void LogVisibilityChangeFailed(Exception exception, Guid integrationEventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message moved to the dead-letter queue: {Reason}")]
    private partial void LogDeadLettered(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unprocessable message ({Reason}) and no dead-letter queue configured; leaving it to the redrive policy")]
    private partial void LogPoisonWithoutDlq(string reason);
}
