using System.ComponentModel.DataAnnotations;

namespace CareFlow.Parser.Application;

public sealed class ParserOptions
{
    public const string SectionName = "Parser";

    /// <summary>The only bucket raw files are read from. Events pointing anywhere else are rejected (defense in depth on top of IAM).</summary>
    [Required]
    public string SourceBucket { get; set; } = string.Empty;

    /// <summary>Matches Ingestion's upload limit; larger objects are refused without being read into memory.</summary>
    [Range(1, 100 * 1024 * 1024)]
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
}
