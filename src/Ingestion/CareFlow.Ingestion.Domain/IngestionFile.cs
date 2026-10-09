namespace CareFlow.Ingestion.Domain;

public enum IngestionStatus
{
    Received = 0,
    Parsed = 1,
    Failed = 2,
}

/// <summary>
/// A healthcare file submitted for processing. The raw content lives in object storage;
/// this aggregate tracks its identity, integrity hash and lifecycle.
/// </summary>
public sealed class IngestionFile
{
    public const int MaxFileNameLength = 255;

    private IngestionFile(
        Guid id,
        string fileName,
        FileFormat format,
        long sizeBytes,
        string sha256,
        string storageKey,
        string submittedBy,
        DateTimeOffset receivedAt,
        IngestionStatus status,
        string? failureReason)
    {
        Id = id;
        FileName = fileName;
        Format = format;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        SubmittedBy = submittedBy;
        ReceivedAt = receivedAt;
        Status = status;
        FailureReason = failureReason;
    }

    public Guid Id { get; }

    public string FileName { get; }

    public FileFormat Format { get; }

    public long SizeBytes { get; }

    /// <summary>Lowercase hex SHA-256 of the raw content; used for integrity checks and duplicate detection.</summary>
    public string Sha256 { get; }

    public string StorageKey { get; }

    /// <summary>Subject (<c>sub</c>) of the authenticated caller.</summary>
    public string SubmittedBy { get; }

    public DateTimeOffset ReceivedAt { get; }

    public IngestionStatus Status { get; private set; }

    public string? FailureReason { get; private set; }

    public static IngestionFile Receive(
        Guid id,
        string fileName,
        FileFormat format,
        long sizeBytes,
        string sha256,
        string submittedBy,
        DateTimeOffset receivedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id is required.", nameof(id));
        }

        if (format == FileFormat.Unknown)
        {
            throw new ArgumentException("A supported file format is required.", nameof(format));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedBy);
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("SHA-256 must be 64 lowercase hex characters.", nameof(sha256));
        }

        var receivedUtc = receivedAt.ToUniversalTime();
        var storageKey = $"raw/{receivedUtc:yyyy/MM/dd}/{id:N}{Extension(format)}";

        return new IngestionFile(
            id,
            SanitizeFileName(fileName),
            format,
            sizeBytes,
            sha256,
            storageKey,
            submittedBy,
            receivedUtc,
            IngestionStatus.Received,
            failureReason: null);
    }

    /// <summary>Reconstitutes an aggregate from persistence without re-running creation rules.</summary>
    public static IngestionFile Rehydrate(
        Guid id,
        string fileName,
        FileFormat format,
        long sizeBytes,
        string sha256,
        string storageKey,
        string submittedBy,
        DateTimeOffset receivedAt,
        IngestionStatus status,
        string? failureReason) =>
        new(id, fileName, format, sizeBytes, sha256, storageKey, submittedBy, receivedAt, status, failureReason);

    public void MarkParsed()
    {
        EnsureStatus(IngestionStatus.Received);
        Status = IngestionStatus.Parsed;
    }

    public void MarkFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureStatus(IngestionStatus.Received);
        Status = IngestionStatus.Failed;
        FailureReason = reason;
    }

    /// <summary>
    /// Client-supplied names are display-only: strip any path, control characters and cap the length.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0)
        {
            return "upload";
        }

        return name.Length <= MaxFileNameLength ? name : name[..MaxFileNameLength];
    }

    private static string Extension(FileFormat format) => format switch
    {
        FileFormat.Hl7v2 => ".hl7",
        FileFormat.X12 => ".x12",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private void EnsureStatus(IngestionStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Ingestion {Id} is {Status}; expected {expected}.");
        }
    }
}
