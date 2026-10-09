using Amazon.S3;
using Amazon.S3.Model;
using CareFlow.Ingestion.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace CareFlow.Ingestion.Infrastructure.Storage;

internal sealed class S3RawFileStore(IAmazonS3 s3, IOptions<AwsOptions> options) : IRawFileStore
{
    private readonly AwsOptions _options = options.Value;

    public string Location => _options.RawBucket;

    public async Task PutAsync(string key, Stream content, string contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            BucketName = _options.RawBucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = contentType,
        };

        // Encryption at rest is mandatory for PHI: SSE-KMS when a key is configured, otherwise SSE-S3.
        if (string.IsNullOrWhiteSpace(_options.KmsKeyId))
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }
        else
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
            request.ServerSideEncryptionKeyManagementServiceKeyId = _options.KmsKeyId;
        }

        foreach (var (name, value) in metadata)
        {
            request.Metadata[name] = value;
        }

        await s3.PutObjectAsync(request, cancellationToken);
    }
}
