using System.ComponentModel.DataAnnotations;

namespace CareFlow.Ingestion.Application;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Upper bound for a single upload. X12 batches can be large; 10 MB covers typical clinic submissions.</summary>
    [Range(1, 100 * 1024 * 1024)]
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
}
