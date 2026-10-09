namespace CareFlow.BuildingBlocks.Messaging;

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
