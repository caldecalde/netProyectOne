using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.Application.Abstractions;
using CareFlow.Ingestion.Domain;
using Microsoft.Extensions.Logging;

namespace CareFlow.Ingestion.Application.Files;

/// <summary>
/// Moves a file to <c>Parsed</c> or <c>Failed</c> when the parser reports back. Idempotent: delivery is
/// at-least-once, so a result for a file that already left <c>Received</c> is acknowledged and ignored.
/// </summary>
public sealed class ParseResultHandler(IIngestionFileRepository repository, ILogger<ParseResultHandler> logger)
    : IMessageHandler<MessageParsed>, IMessageHandler<ParseFailed>
{
    public Task HandleAsync(MessageParsed integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ApplyAsync(integrationEvent.IngestionId, file => file.MarkParsed(), cancellationToken);
    }

    public Task HandleAsync(ParseFailed integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return ApplyAsync(integrationEvent.IngestionId, file => file.MarkFailed(integrationEvent.ReasonCode), cancellationToken);
    }

    private async Task ApplyAsync(Guid ingestionId, Action<IngestionFile> transition, CancellationToken cancellationToken)
    {
        var file = await repository.GetAsync(ingestionId, cancellationToken);
        if (file is null)
        {
            // The file record commits before its event is published, so this is not a timing issue; retrying cannot help.
            logger.LogWarning("Parse result for unknown ingestion {IngestionId} ignored", ingestionId);
            return;
        }

        if (file.Status != IngestionStatus.Received)
        {
            logger.LogInformation("Ingestion {IngestionId} is already {Status}; duplicate parse result ignored", ingestionId, file.Status);
            return;
        }

        transition(file);
        if (await repository.SaveOutcomeAsync(file, cancellationToken))
        {
            logger.LogInformation("Ingestion {IngestionId} is now {Status}", ingestionId, file.Status);
        }
    }
}
