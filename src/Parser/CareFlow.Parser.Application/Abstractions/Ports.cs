using CareFlow.Parser.Domain;

namespace CareFlow.Parser.Application.Abstractions;

/// <summary>Reads raw files written by Ingestion (Amazon S3).</summary>
public interface IRawFileReader
{
    /// <summary>Returns the object's content, or <c>null</c> when it does not exist.</summary>
    Task<byte[]?> ReadAsync(string bucket, string key, CancellationToken cancellationToken);
}

/// <summary>Stores parser output (normalized JSON and acknowledgments), encrypted at rest.</summary>
public interface IParseResultStore
{
    /// <summary>Bucket the results are written to; carried in events so consumers know where to read.</summary>
    string Location { get; }

    Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken);
}

/// <summary>Inbox of events already handled, so a redelivered message is acknowledged without being processed again.</summary>
public interface IProcessedEventStore
{
    Task<bool> IsProcessedAsync(Guid eventId, CancellationToken cancellationToken);

    Task MarkProcessedAsync(Guid eventId, CancellationToken cancellationToken);
}

/// <param name="IngestionId">The file being parsed; also seeds deterministic values such as acknowledgment control numbers.</param>
/// <param name="Now">Parse time, used in acknowledgments.</param>
public sealed record ParseContext(Guid IngestionId, DateTimeOffset Now);

/// <summary>Parses one file format. Expected content problems are reported in the outcome, never thrown.</summary>
public interface IFileParser
{
    /// <summary>The <c>FileReceived.Format</c> value this parser handles: <c>Hl7v2</c> or <c>X12</c>.</summary>
    string Format { get; }

    ParseOutcome Parse(ReadOnlyMemory<byte> content, ParseContext context);
}
