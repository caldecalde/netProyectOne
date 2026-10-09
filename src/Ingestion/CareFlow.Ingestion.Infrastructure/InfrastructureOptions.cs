using System.ComponentModel.DataAnnotations;

namespace CareFlow.Ingestion.Infrastructure;

public sealed class AwsOptions
{
    public const string SectionName = "Aws";

    [Required]
    public string Region { get; set; } = "us-east-1";

    /// <summary>Endpoint override for LocalStack (e.g. <c>http://localhost:4566</c>). Empty on real AWS.</summary>
    public string? ServiceUrl { get; set; }

    [Required]
    public string RawBucket { get; set; } = string.Empty;

    /// <summary>KMS key for SSE-KMS. When empty, S3-managed keys (SSE-S3) are used.</summary>
    public string? KmsKeyId { get; set; }

    [Required]
    public string EventsTopicArn { get; set; } = string.Empty;
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// Fallback polling interval. Events committed by this instance are relayed immediately; polling picks up
    /// messages left behind by a crashed instance or deferred after a failed publish.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    /// <summary>How long a claimed message is reserved for one relay before another may take it over.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long published messages are kept for troubleshooting.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
}

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    [Required]
    public string Database { get; set; } = "careflow_ingestion";
}
