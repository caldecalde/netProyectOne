using System.Text.Json;
using System.Text.Json.Serialization;
using CareFlow.Parser.Domain;

namespace CareFlow.Parser.Application.Parsing;

/// <summary>
/// The normalized JSON document stored for each parsed file and referenced by <c>MessageParsed</c> / <c>ParseFailed</c>.
/// It contains PHI, so it lives only in encrypted storage; events carry its key, never its content.
/// </summary>
public sealed record ParsedFileDocument
{
    /// <summary>Bumped on breaking shape changes, so readers (the Records service) can migrate older documents.</summary>
    public const int CurrentSchemaVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid IngestionId { get; init; }

    public required string Format { get; init; }

    /// <summary>Hash of the raw content this document was produced from.</summary>
    public required string Sha256 { get; init; }

    public required DateTimeOffset ParsedAt { get; init; }

    /// <summary><c>Parsed</c> or <c>Failed</c>.</summary>
    public required string Outcome { get; init; }

    public string? FailureCode { get; init; }

    public required int AcceptedCount { get; init; }

    public required int RejectedCount { get; init; }

    public required IReadOnlyList<ParsedMessage> Messages { get; init; }

    public required IReadOnlyList<ParseIssue> Issues { get; init; }
}
