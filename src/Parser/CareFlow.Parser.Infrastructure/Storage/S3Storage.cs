using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using CareFlow.Parser.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace CareFlow.Parser.Infrastructure.Storage;

internal sealed class S3RawFileReader(IAmazonS3 s3) : IRawFileReader
{
    public async Task<byte[]?> ReadAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await s3.GetObjectAsync(bucket, key, cancellationToken);
            using var buffer = new MemoryStream(response.ContentLength is > 0 and <= int.MaxValue ? (int)response.ContentLength : 0);
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}

internal sealed class S3ParseResultStore(IAmazonS3 s3, IOptions<AwsOptions> options) : IParseResultStore
{
    private readonly AwsOptions _options = options.Value;

    public string Location => _options.ResultsBucket;

    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _options.ResultsBucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
        };

        // Parsed documents contain PHI: encryption at rest is mandatory, exactly as for raw files.
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
