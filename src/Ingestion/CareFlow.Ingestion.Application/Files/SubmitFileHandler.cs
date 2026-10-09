using System.Security.Cryptography;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.BuildingBlocks.Observability;
using CareFlow.BuildingBlocks.Results;
using CareFlow.Ingestion.Application.Abstractions;
using CareFlow.Ingestion.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CareFlow.Ingestion.Application.Files;

/// <param name="FileName">Client-supplied name; display only.</param>
/// <param name="Content">Upload stream. Read once; not disposed by the handler.</param>
/// <param name="SubmittedBy">Subject of the authenticated caller.</param>
public sealed record SubmitFileCommand(string? FileName, Stream Content, string SubmittedBy);

public sealed class SubmitFileHandler(
    IRawFileStore fileStore,
    IIngestionFileRepository repository,
    IEventPublisher publisher,
    TimeProvider timeProvider,
    IOptions<IngestionOptions> options,
    ILogger<SubmitFileHandler> logger)
{
    private readonly long _maxFileBytes = options.Value.MaxFileBytes;

    public async Task<Result<SubmitFileResult>> HandleAsync(SubmitFileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var activity = CareFlowTelemetry.ActivitySource.StartActivity("ingestion.submit_file");

        // Buffer once (bounded by the size limit) so we can hash, sniff the format and upload without re-reading the request.
        using var buffer = new MemoryStream();
        if (!await TryCopyBoundedAsync(command.Content, buffer, _maxFileBytes, cancellationToken))
        {
            return Reject(IngestionErrors.TooLarge(_maxFileBytes));
        }

        if (buffer.Length == 0)
        {
            return Reject(IngestionErrors.EmptyFile);
        }

        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        var format = FileFormatDetector.Detect(bytes.Span[..Math.Min(bytes.Length, FileFormatDetector.HeaderLength)]);
        if (format == FileFormat.Unknown)
        {
            return Reject(IngestionErrors.UnsupportedFormat);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        activity?.SetTag("careflow.file.format", format.ToString());
        activity?.SetTag("careflow.file.size", bytes.Length);

        var existing = await repository.FindBySha256Async(sha256, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Duplicate submission of ingestion {IngestionId}", existing.Id);
            return new SubmitFileResult(IngestionFileDto.From(existing), IsDuplicate: true);
        }

        var file = IngestionFile.Receive(
            Guid.NewGuid(),
            command.FileName ?? string.Empty,
            format,
            bytes.Length,
            sha256,
            command.SubmittedBy,
            timeProvider.GetUtcNow());

        buffer.Position = 0;
        await fileStore.PutAsync(
            file.StorageKey,
            buffer,
            ContentType(format),
            new Dictionary<string, string>
            {
                ["ingestion-id"] = file.Id.ToString(),
                ["format"] = format.ToString(),
                ["sha256"] = sha256,
            },
            cancellationToken);

        if (!await repository.TryAddAsync(file, cancellationToken))
        {
            // Lost a race with a concurrent identical upload; the winner owns the event.
            // The object we just wrote is orphaned under a unique key and is removed by the bucket lifecycle rule.
            var winner = await repository.FindBySha256Async(sha256, cancellationToken)
                ?? throw new InvalidOperationException($"Duplicate key reported for {sha256} but no record found.");
            return new SubmitFileResult(IngestionFileDto.From(winner), IsDuplicate: true);
        }

        // TODO(week 2): replace with a transactional outbox so a broker outage can't leave a stored file without its event.
        await publisher.PublishAsync(
            new FileReceived
            {
                OccurredAt = file.ReceivedAt,
                IngestionId = file.Id,
                Format = format.ToString(),
                Bucket = fileStore.Location,
                StorageKey = file.StorageKey,
                Sha256 = sha256,
                SizeBytes = file.SizeBytes,
            },
            cancellationToken);

        CareFlowTelemetry.FilesReceived.Add(1, new KeyValuePair<string, object?>("format", format.ToString()));
        CareFlowTelemetry.FileSize.Record(file.SizeBytes, new KeyValuePair<string, object?>("format", format.ToString()));
        logger.LogInformation("Accepted ingestion {IngestionId} ({Format}, {SizeBytes} bytes)", file.Id, format, file.SizeBytes);

        return new SubmitFileResult(IngestionFileDto.From(file), IsDuplicate: false);
    }

    private Result<SubmitFileResult> Reject(Error error)
    {
        CareFlowTelemetry.FilesRejected.Add(1, new KeyValuePair<string, object?>("reason", error.Code));
        logger.LogInformation("Rejected submission: {ErrorCode}", error.Code);
        return error;
    }

    private static async Task<bool> TryCopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return false;
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return true;
    }

    private static string ContentType(FileFormat format) => format switch
    {
        FileFormat.Hl7v2 => "x-application/hl7-v2+er7",
        FileFormat.X12 => "application/edi-x12",
        _ => "application/octet-stream",
    };
}
