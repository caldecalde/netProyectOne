using System.Diagnostics;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure.Messaging;

/// <summary>Wakes the relay as soon as a transaction with outbox messages commits, so publishing does not wait for the next poll.</summary>
internal sealed class OutboxSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled; one wake-up drains everything pending.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _signal.Dispose();
}

/// <summary>
/// Publishes committed outbox messages to SNS. Each message is claimed with a short lease, so several API replicas
/// can run relays side by side without publishing the same message concurrently. A failed publish backs off and is
/// retried indefinitely: an event is never dropped. Delivery is at-least-once (a crash after publishing but before
/// marking the message re-publishes it), which consumers handle by de-duplicating on <c>EventId</c>.
/// </summary>
internal sealed partial class OutboxRelay(
    IMongoDatabase database,
    IEventEnvelopePublisher publisher,
    OutboxSignal signal,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private readonly IMongoCollection<OutboxMessage> _outbox = database.GetCollection<OutboxMessage>(OutboxMessage.CollectionName);
    private readonly OutboxOptions _options = options.Value;
    private readonly ILogger<OutboxRelay> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishPendingAsync(stoppingToken);
                if (published < _options.BatchSize)
                {
                    await signal.WaitAsync(_options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // MongoDB unreachable or similar: nothing is lost, pending messages stay in the outbox.
                LogRelayFailed(ex, _options.PollInterval);
                try
                {
                    await Task.Delay(_options.PollInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Publishes up to one batch of due messages. Returns how many were claimed.</summary>
    internal async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var claimed = 0;
        while (claimed < _options.BatchSize && await TryClaimAsync(cancellationToken) is { } message)
        {
            claimed++;
            await PublishAsync(message, cancellationToken);
        }

        return claimed;
    }

    private async Task<OutboxMessage?> TryClaimAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await _outbox.FindOneAndUpdateAsync<OutboxMessage>(
            m => m.Status == OutboxStatus.Pending && m.AvailableAt <= now,
            Builders<OutboxMessage>.Update
                .Set(m => m.AvailableAt, now + _options.LeaseDuration)
                .Inc(m => m.Attempts, 1),
            new FindOneAndUpdateOptions<OutboxMessage>
            {
                Sort = Builders<OutboxMessage>.Sort.Ascending(m => m.AvailableAt),
                ReturnDocument = ReturnDocument.After,
            },
            cancellationToken);
    }

    private async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ActivityContext.TryParse(message.TraceParent, message.TraceState, isRemote: true, out var parent);
        using var activity = CareFlowTelemetry.ActivitySource.StartActivity($"outbox relay {message.EventType}", ActivityKind.Internal, parent);
        activity?.SetTag("careflow.event_id", message.Id);
        activity?.SetTag("careflow.event_type", message.EventType);
        activity?.SetTag("careflow.outbox.attempt", message.Attempts);

        try
        {
            await publisher.PublishAsync(new EventEnvelope(message.Id, message.EventType, message.Payload), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var retryIn = Backoff(message.Attempts);
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            LogPublishFailed(ex, message.Id, message.EventType, message.Attempts, retryIn);
            await _outbox.UpdateOneAsync(
                m => m.Id == message.Id,
                Builders<OutboxMessage>.Update
                    .Set(m => m.AvailableAt, timeProvider.GetUtcNow().UtcDateTime + retryIn)
                    .Set(m => m.LastError, ex.GetType().Name),
                cancellationToken: cancellationToken);
            return;
        }

        var publishedAt = timeProvider.GetUtcNow().UtcDateTime;
        await _outbox.UpdateOneAsync(
            m => m.Id == message.Id,
            Builders<OutboxMessage>.Update
                .Set(m => m.Status, OutboxStatus.Published)
                .Set(m => m.PublishedAt, publishedAt)
                .Unset(m => m.LastError),
            cancellationToken: cancellationToken);

        var eventType = new KeyValuePair<string, object?>("event_type", message.EventType);
        CareFlowTelemetry.OutboxPublished.Add(1, eventType);
        CareFlowTelemetry.OutboxLag.Record((publishedAt - message.CreatedAt).TotalSeconds, eventType);
    }

    private TimeSpan Backoff(int attempts)
    {
        var delay = TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(attempts, 1, 16)));
        return delay < _options.MaxBackoff ? delay : _options.MaxBackoff;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox relay pass failed; retrying in {Delay}")]
    private partial void LogRelayFailed(Exception exception, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing outbox event {IntegrationEventId} ({EventType}) failed on attempt {Attempt}; retrying in {RetryIn}")]
    private partial void LogPublishFailed(Exception exception, Guid integrationEventId, string eventType, int attempt, TimeSpan retryIn);
}
