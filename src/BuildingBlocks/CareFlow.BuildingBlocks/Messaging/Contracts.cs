namespace CareFlow.BuildingBlocks.Messaging;

// Events carry identifiers, keys and counts only, never message content: PHI stays in encrypted storage.

/// <summary>A raw healthcare file was accepted and stored; it is ready to be parsed.</summary>
public sealed record FileReceived : IntegrationEvent
{
    public required Guid IngestionId { get; init; }

    /// <summary><c>Hl7v2</c> or <c>X12</c>.</summary>
    public required string Format { get; init; }

    public required string Bucket { get; init; }

    public required string StorageKey { get; init; }

    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }
}

/// <summary>
/// A file was parsed and at least one message (HL7 message or X12 transaction set) was accepted.
/// The normalized document is in <see cref="Bucket"/>/<see cref="ResultKey"/>.
/// </summary>
public sealed record MessageParsed : IntegrationEvent
{
    public required Guid IngestionId { get; init; }

    public required string Format { get; init; }

    /// <summary>Distinct message types found, e.g. <c>ADT^A01</c>, <c>ORU^R01</c>, <c>837P</c>.</summary>
    public required IReadOnlyList<string> MessageTypes { get; init; }

    public required string Bucket { get; init; }

    /// <summary>Normalized JSON document (accepted messages plus any issues).</summary>
    public required string ResultKey { get; init; }

    /// <summary>X12 999 implementation acknowledgment, when the source was X12.</summary>
    public string? AcknowledgmentKey { get; init; }

    public required int AcceptedCount { get; init; }

    /// <summary>Messages rejected while others in the same file were accepted.</summary>
    public required int RejectedCount { get; init; }
}

/// <summary>A file could not be used: nothing in it was accepted, or it could not be read or verified.</summary>
public sealed record ParseFailed : IntegrationEvent
{
    public required Guid IngestionId { get; init; }

    public required string Format { get; init; }

    /// <summary>Machine-readable reason, e.g. <c>x12.interchange_invalid</c> or <c>parser.integrity_mismatch</c>.</summary>
    public required string ReasonCode { get; init; }

    /// <summary>Bucket holding <see cref="ResultKey"/> and <see cref="AcknowledgmentKey"/>, when they exist.</summary>
    public string? Bucket { get; init; }

    /// <summary>Document listing the issues found, when parsing got far enough to produce one.</summary>
    public string? ResultKey { get; init; }

    /// <summary>X12 999 acknowledgment rejecting the content, when one could be produced.</summary>
    public string? AcknowledgmentKey { get; init; }

    public required int IssueCount { get; init; }
}
