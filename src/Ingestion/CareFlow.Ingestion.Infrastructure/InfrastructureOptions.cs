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

public sealed class MongoOptions
{
    public const string SectionName = "Mongo";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    [Required]
    public string Database { get; set; } = "careflow_ingestion";
}
