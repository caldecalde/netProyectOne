using CareFlow.Ingestion.Domain;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CareFlow.Ingestion.Infrastructure.Persistence;

/// <summary>Persistence model, kept separate so the domain has no MongoDB dependency.</summary>
internal sealed class IngestionFileDocument
{
    /// <summary>Bumped on breaking shape changes; readers migrate older documents lazily.</summary>
    public const int CurrentSchemaVersion = 1;

    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string FileName { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public FileFormat Format { get; set; }

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string StorageKey { get; set; } = string.Empty;

    public string SubmittedBy { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ReceivedAt { get; set; }

    [BsonRepresentation(BsonType.String)]
    public IngestionStatus Status { get; set; }

    [BsonIgnoreIfNull]
    public string? FailureReason { get; set; }

    public static IngestionFileDocument FromDomain(IngestionFile file) => new()
    {
        Id = file.Id,
        FileName = file.FileName,
        Format = file.Format,
        SizeBytes = file.SizeBytes,
        Sha256 = file.Sha256,
        StorageKey = file.StorageKey,
        SubmittedBy = file.SubmittedBy,
        ReceivedAt = file.ReceivedAt.UtcDateTime,
        Status = file.Status,
        FailureReason = file.FailureReason,
    };

    public IngestionFile ToDomain() => IngestionFile.Rehydrate(
        Id,
        FileName,
        Format,
        SizeBytes,
        Sha256,
        StorageKey,
        SubmittedBy,
        new DateTimeOffset(DateTime.SpecifyKind(ReceivedAt, DateTimeKind.Utc)),
        Status,
        FailureReason);
}
