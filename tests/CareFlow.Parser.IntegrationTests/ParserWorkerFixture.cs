using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using CareFlow.BuildingBlocks.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.LocalStack;
using Testcontainers.MongoDb;
using SnsAttribute = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace CareFlow.Parser.IntegrationTests;

/// <summary>
/// Runs the real parser worker against MongoDB and LocalStack, wired like production: SNS topic, a filtered
/// <c>parser-file-received</c> queue with a redrive policy, and a test-only queue that observes parser results.
/// </summary>
public sealed class ParserWorkerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string RawBucket = "careflow-raw";
    public const string ResultsBucket = "careflow-parsed";
    private const string Region = "us-east-1";

    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.9")
        .WithEnvironment("SERVICES", "s3,sns,sqs")
        .Build();

    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8.0").Build();

    public AmazonS3Client S3 { get; private set; } = null!;

    public AmazonSQSClient Sqs { get; private set; } = null!;

    public AmazonSimpleNotificationServiceClient Sns { get; private set; } = null!;

    public string TopicArn { get; private set; } = string.Empty;

    public string InputQueueUrl { get; private set; } = string.Empty;

    public string DeadLetterQueueUrl { get; private set; } = string.Empty;

    public string ResultsQueueUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_localStack.StartAsync(), _mongo.StartAsync());
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "test");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "test");

        var credentials = new BasicAWSCredentials("test", "test");
        var endpoint = _localStack.GetConnectionString();
        S3 = new AmazonS3Client(credentials, new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = Region });
        Sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = endpoint, AuthenticationRegion = Region });
        Sns = new AmazonSimpleNotificationServiceClient(credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = endpoint, AuthenticationRegion = Region });

        await S3.PutBucketAsync(RawBucket);
        await S3.PutBucketAsync(ResultsBucket);
        TopicArn = (await Sns.CreateTopicAsync("careflow-events")).TopicArn;

        DeadLetterQueueUrl = (await Sqs.CreateQueueAsync("parser-file-received-dlq")).QueueUrl;
        var dlqArn = (await Sqs.GetQueueAttributesAsync(DeadLetterQueueUrl, ["QueueArn"])).QueueARN;
        InputQueueUrl = (await Sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "parser-file-received",
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $$"""{"deadLetterTargetArn":"{{dlqArn}}","maxReceiveCount":"5"}""",
                ["VisibilityTimeout"] = "30",
            },
        })).QueueUrl;
        await SubscribeAsync(InputQueueUrl, "FileReceived");

        ResultsQueueUrl = (await Sqs.CreateQueueAsync("test-parse-results")).QueueUrl;
        await SubscribeAsync(ResultsQueueUrl, "MessageParsed", "ParseFailed");

        // Starts the host, and with it the queue consumer.
        CreateClient().Dispose();
    }

    /// <summary>Uploads a sample as Ingestion would and announces it, returning the event that was published.</summary>
    public async Task<FileReceived> SubmitAsync(byte[] content, string format, string? traceparent = null)
    {
        var id = Guid.NewGuid();
        var key = $"raw/2026/01/05/{id:N}";
        await S3.PutObjectAsync(new PutObjectRequest { BucketName = RawBucket, Key = key, InputStream = new MemoryStream(content) });
        var received = new FileReceived
        {
            OccurredAt = new DateTimeOffset(2026, 1, 5, 14, 0, 0, TimeSpan.Zero),
            IngestionId = id,
            Format = format,
            Bucket = RawBucket,
            StorageKey = key,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            SizeBytes = content.Length,
        };
        await PublishAsync(received, traceparent);
        return received;
    }

    public async Task PublishAsync(FileReceived received, string? traceparent = null)
    {
        var envelope = EventSerializer.Serialize(received);
        var attributes = new Dictionary<string, SnsAttribute>
        {
            ["event-type"] = new() { DataType = "String", StringValue = envelope.EventType },
        };
        if (traceparent is not null)
        {
            attributes["traceparent"] = new() { DataType = "String", StringValue = traceparent };
        }

        await Sns.PublishAsync(new PublishRequest { TopicArn = TopicArn, Message = envelope.Body, MessageAttributes = attributes });
    }

    public async Task<List<Message>> ReceiveAsync(string queueUrl, Func<Message, bool> predicate, TimeSpan timeout, int expected = 1)
    {
        var matches = new List<Message>();
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested && matches.Count < expected)
        {
            var response = await Sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1, MessageAttributeNames = ["All"] },
                CancellationToken.None);
            foreach (var message in response.Messages ?? [])
            {
                if (predicate(message))
                {
                    matches.Add(message);
                    await Sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
                }
            }
        }

        return matches;
    }

    public async Task<string> ReadResultAsync(string key)
    {
        using var response = await S3.GetObjectAsync(ResultsBucket, key);
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Aws:ServiceUrl", _localStack.GetConnectionString());
        builder.UseSetting("Aws:Region", Region);
        builder.UseSetting("Aws:ResultsBucket", ResultsBucket);
        builder.UseSetting("Aws:EventsTopicArn", TopicArn);
        builder.UseSetting("Mongo:ConnectionString", _mongo.GetConnectionString());
        builder.UseSetting("Mongo:Database", "careflow_parser_tests");
        builder.UseSetting("Parser:SourceBucket", RawBucket);
        builder.UseSetting("Consumers:FileReceived:QueueUrl", InputQueueUrl);
        builder.UseSetting("Consumers:FileReceived:DeadLetterQueueUrl", DeadLetterQueueUrl);
        builder.UseSetting("Consumers:FileReceived:WaitTimeSeconds", "1");
    }

    private async Task SubscribeAsync(string queueUrl, params string[] eventTypes)
    {
        var queueArn = (await Sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).QueueARN;
        await Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = TopicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = $$"""{"event-type":[{{string.Join(',', eventTypes.Select(t => $"\"{t}\""))}}]}""",
            },
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        S3?.Dispose();
        Sqs?.Dispose();
        Sns?.Dispose();
        await DisposeAsync();
        await _localStack.DisposeAsync();
        await _mongo.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ParserWorkerTests : ICollectionFixture<ParserWorkerFixture>
{
    public const string Name = "ParserWorker";
}
