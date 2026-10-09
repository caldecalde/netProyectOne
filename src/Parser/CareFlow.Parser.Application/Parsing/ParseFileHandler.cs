using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Observability;
using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CareFlow.Parser.Application.Parsing;

/// <summary>
/// Handles <see cref="FileReceived"/>: reads the raw file, verifies its hash, parses it, stores the normalized document
/// (and the X12 999), and publishes <see cref="MessageParsed"/> or <see cref="ParseFailed"/>.
/// </summary>
/// <remarks>
/// <para>Content problems are outcomes, not errors: they end in <see cref="ParseFailed"/> and the message is acknowledged.
/// Only infrastructure failures (and parser bugs) throw, which sends the message through retries and finally to the DLQ.</para>
/// <para>Idempotent: processed event IDs are recorded, output keys are derived from the ingestion, and the result event's
/// ID is derived from the incoming event's ID. A redelivery therefore rewrites the same objects and republishes the same
/// event ID, which downstream consumers de-duplicate.</para>
/// </remarks>
public sealed class ParseFileHandler(
    IEnumerable<IFileParser> parsers,
    IRawFileReader rawFiles,
    IParseResultStore results,
    IProcessedEventStore processedEvents,
    IEventPublisher publisher,
    TimeProvider timeProvider,
    IOptions<ParserOptions> options,
    ILogger<ParseFileHandler> logger) : IMessageHandler<FileReceived>
{
    /// <summary>Namespace for deriving result event IDs from <c>FileReceived</c> event IDs.</summary>
    private static readonly Guid ResultEventNamespace = new("4f0f7f43-3c1e-4d5b-9a51-6f2d7e0c8b21");

    private readonly ParserOptions _options = options.Value;

    public async Task HandleAsync(FileReceived integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        using var activity = CareFlowTelemetry.ActivitySource.StartActivity("parser.parse_file");
        activity?.SetTag("careflow.ingestion_id", integrationEvent.IngestionId);
        activity?.SetTag("careflow.file.format", integrationEvent.Format);
        activity?.SetTag("careflow.file.size", integrationEvent.SizeBytes);

        if (await processedEvents.IsProcessedAsync(integrationEvent.EventId, cancellationToken))
        {
            logger.LogInformation("Event {IntegrationEventId} for ingestion {IngestionId} was already processed; skipping", integrationEvent.EventId, integrationEvent.IngestionId);
            activity?.SetTag("careflow.outcome", "duplicate");
            return;
        }

        var result = await ProcessAsync(integrationEvent, timeProvider.GetUtcNow(), cancellationToken);
        await publisher.PublishAsync(result, cancellationToken);
        await processedEvents.MarkProcessedAsync(integrationEvent.EventId, cancellationToken);

        var outcome = result is MessageParsed ? "parsed" : "failed";
        activity?.SetTag("careflow.outcome", outcome);
        CareFlowTelemetry.FilesParsed.Add(1, new TagList { { "format", integrationEvent.Format }, { "outcome", outcome } });
    }

    private async Task<IntegrationEvent> ProcessAsync(FileReceived received, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!string.Equals(received.Bucket, _options.SourceBucket, StringComparison.Ordinal))
        {
            return Failed(received, now, "parser.untrusted_location");
        }

        if (received.SizeBytes > _options.MaxFileBytes)
        {
            return Failed(received, now, "parser.file_too_large");
        }

        var parser = parsers.FirstOrDefault(p => string.Equals(p.Format, received.Format, StringComparison.Ordinal));
        if (parser is null)
        {
            return Failed(received, now, "parser.unsupported_format");
        }

        var content = await rawFiles.ReadAsync(received.Bucket, received.StorageKey, cancellationToken);
        if (content is null)
        {
            return Failed(received, now, "parser.raw_file_missing");
        }

        // The hash recorded at upload proves the content was not altered or swapped in storage.
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), received.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(received, now, "parser.integrity_mismatch");
        }

        var outcome = parser.Parse(content, new ParseContext(received.IngestionId, now));

        // Keys depend only on the ingestion, so a redelivery overwrites instead of duplicating.
        var keySuffix = $"{received.OccurredAt.UtcDateTime:yyyy/MM/dd}/{received.IngestionId:N}";
        var metadata = new Dictionary<string, string>
        {
            ["ingestion-id"] = received.IngestionId.ToString(),
            ["source-sha256"] = received.Sha256,
        };

        string? acknowledgmentKey = null;
        if (outcome.Acknowledgment is not null)
        {
            acknowledgmentKey = $"acks/{keySuffix}.999";
            await results.PutAsync(acknowledgmentKey, Encoding.UTF8.GetBytes(outcome.Acknowledgment), "application/edi-x12", metadata, cancellationToken);
        }

        var document = new ParsedFileDocument
        {
            IngestionId = received.IngestionId,
            Format = received.Format,
            Sha256 = received.Sha256,
            ParsedAt = now,
            Outcome = outcome.IsSuccess ? "Parsed" : "Failed",
            FailureCode = outcome.FailureCode,
            AcceptedCount = outcome.Messages.Count,
            RejectedCount = outcome.RejectedCount,
            Messages = outcome.Messages,
            Issues = outcome.Issues,
        };
        var resultKey = $"parsed/{keySuffix}.json";
        await results.PutAsync(resultKey, JsonSerializer.SerializeToUtf8Bytes(document, ParsedFileDocument.JsonOptions), "application/json", metadata, cancellationToken);

        if (!outcome.IsSuccess)
        {
            return Failed(received, now, outcome.FailureCode ?? "parser.no_messages", resultKey, acknowledgmentKey, outcome.Issues.Count);
        }

        logger.LogInformation(
            "Parsed ingestion {IngestionId}: {AcceptedCount} accepted, {RejectedCount} rejected, {IssueCount} issues",
            received.IngestionId, outcome.Messages.Count, outcome.RejectedCount, outcome.Issues.Count);

        return new MessageParsed
        {
            EventId = ResultEventId(received),
            CausationId = received.EventId,
            OccurredAt = now,
            IngestionId = received.IngestionId,
            Format = received.Format,
            MessageTypes = outcome.Messages.Select(m => m.MessageType).Distinct(StringComparer.Ordinal).ToArray(),
            Bucket = results.Location,
            ResultKey = resultKey,
            AcknowledgmentKey = acknowledgmentKey,
            AcceptedCount = outcome.Messages.Count,
            RejectedCount = outcome.RejectedCount,
        };
    }

    private ParseFailed Failed(FileReceived received, DateTimeOffset now, string reasonCode, string? resultKey = null, string? acknowledgmentKey = null, int issueCount = 0)
    {
        logger.LogWarning("Ingestion {IngestionId} failed parsing: {ReasonCode}", received.IngestionId, reasonCode);
        return new ParseFailed
        {
            EventId = ResultEventId(received),
            CausationId = received.EventId,
            OccurredAt = now,
            IngestionId = received.IngestionId,
            Format = received.Format,
            ReasonCode = reasonCode,
            Bucket = resultKey is null && acknowledgmentKey is null ? null : results.Location,
            ResultKey = resultKey,
            AcknowledgmentKey = acknowledgmentKey,
            IssueCount = issueCount,
        };
    }

    private static Guid ResultEventId(FileReceived received) =>
        DeterministicGuid.Create(ResultEventNamespace, received.EventId.ToString());
}
