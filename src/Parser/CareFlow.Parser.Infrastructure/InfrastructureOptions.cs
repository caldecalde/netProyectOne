using System.ComponentModel.DataAnnotations;

namespace CareFlow.Parser.Infrastructure;

public sealed class AwsOptions
{
    public const string SectionName = "Aws";

    [Required]
    public string Region { get; set; } = "us-east-1";

    /// <summary>Endpoint override for LocalStack (e.g. <c>http://localhost:4566</c>). Empty on real AWS.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Bucket for normalized documents and acknowledgments. Separate from the raw bucket, so IAM can let the parser
    /// read raw files without being able to change them.
    /// </summary>
    [Required]
    public string ResultsBucket { get; set; } = string.Empty;

    /// <summary>KMS key for SSE-KMS. When empty, S3-managed keys (SSE-S3) are used.</summary>
    public string? KmsKeyId { get; set; }

    [Required]
    public string EventsTopicArn { get; set; } = string.Empty;
}

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    [Required]
    public string Database { get; set; } = "careflow_parser";

    /// <summary>How long processed event IDs are remembered. Must exceed the longest possible redelivery window.</summary>
    public TimeSpan ProcessedEventRetention { get; set; } = TimeSpan.FromDays(14);
}
