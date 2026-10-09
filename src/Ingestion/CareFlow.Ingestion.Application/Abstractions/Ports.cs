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
    /// <summary>Adds the file. Returns <c>false</c> when a file with the same SHA-256 already exists.</summary>
    Task<bool> TryAddAsync(IngestionFile file, CancellationToken cancellationToken);

    Task<IngestionFile?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IngestionFile?> FindBySha256Async(string sha256, CancellationToken cancellationToken);
}
