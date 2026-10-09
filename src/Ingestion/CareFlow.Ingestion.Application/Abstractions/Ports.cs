using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.Domain;

namespace CareFlow.Ingestion.Application.Abstractions;

/// <summary>Object storage for raw files (Amazon S3, encrypted at rest).</summary>
public interface IRawFileStore
{
    /// <summary>Name of the bucket/container files are written to; carried in events so consumers know where to read.</summary>
    string Location { get; }

    Task PutAsync(string key, Stream content, string contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken);
}

public interface IIngestionFileRepository
{
    /// <summary>
    /// Adds the file and writes <paramref name="events"/> to the transactional outbox in one transaction:
    /// both are committed or neither is. A background relay publishes the events after the commit.
    /// Returns <c>false</c>, storing nothing, when a file with the same SHA-256 already exists.
    /// </summary>
    Task<bool> TryAddAsync(IngestionFile file, IReadOnlyCollection<IntegrationEvent> events, CancellationToken cancellationToken);

    Task<IngestionFile?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IngestionFile?> FindBySha256Async(string sha256, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a transition out of <see cref="IngestionStatus.Received"/>. Returns <c>false</c> when the stored file
    /// had already left that state, e.g. because a concurrent delivery of the same result got there first.
    /// </summary>
    Task<bool> SaveOutcomeAsync(IngestionFile file, CancellationToken cancellationToken);
}
