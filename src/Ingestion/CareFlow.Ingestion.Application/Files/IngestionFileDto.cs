using CareFlow.Ingestion.Domain;

namespace CareFlow.Ingestion.Application.Files;

public sealed record IngestionFileDto(
    Guid Id,
    string FileName,
    string Format,
    long SizeBytes,
    string Sha256,
    string Status,
    string? FailureReason,
    DateTimeOffset ReceivedAt)
{
    public static IngestionFileDto From(IngestionFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new IngestionFileDto(
            file.Id,
            file.FileName,
            file.Format.ToString(),
            file.SizeBytes,
            file.Sha256,
            file.Status.ToString(),
            file.FailureReason,
            file.ReceivedAt);
    }
}

/// <param name="File">The stored file.</param>
/// <param name="IsDuplicate"><c>true</c> when identical content was already submitted; nothing new was stored.</param>
public sealed record SubmitFileResult(IngestionFileDto File, bool IsDuplicate);
