using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure.HealthChecks;

internal sealed class MongoHealthCheck(IMongoDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "MongoDB ping failed.", ex);
        }
    }
}

internal sealed class S3HealthCheck(IAmazonS3 s3, IOptions<AwsOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Value.RawBucket, MaxKeys = 1 }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (AmazonS3Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Raw bucket is not reachable.", ex);
        }
    }
}
