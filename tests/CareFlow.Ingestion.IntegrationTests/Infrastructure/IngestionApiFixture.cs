using Amazon.Runtime;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.LocalStack;
using Testcontainers.MongoDb;

namespace CareFlow.Ingestion.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real Ingestion API in-memory against real MongoDB and LocalStack (S3/SNS/SQS) containers.
/// Only authentication is substituted, so tests can act as any role without an IdP.
/// </summary>
public sealed class IngestionApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Bucket = "careflow-raw";
    private const string Region = "us-east-1";

    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.9")
        .WithEnvironment("SERVICES", "s3,sns,sqs")
        .Build();

    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8.0")
        .Build();

    private string _topicArn = string.Empty;

    public AmazonS3Client S3 { get; private set; } = null!;

    public AmazonSQSClient Sqs { get; private set; } = null!;

    public string FileReceivedQueueUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_localStack.StartAsync(), _mongo.StartAsync());

        // The API resolves AWS credentials through the default chain, exactly as in production.
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "test");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "test");

        var credentials = new BasicAWSCredentials("test", "test");
        var endpoint = _localStack.GetConnectionString();
        S3 = new AmazonS3Client(credentials, new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = Region });
        Sqs = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = endpoint, AuthenticationRegion = Region });
        using var sns = new AmazonSimpleNotificationServiceClient(credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = endpoint, AuthenticationRegion = Region });

        await S3.PutBucketAsync(Bucket);
        _topicArn = (await sns.CreateTopicAsync("careflow-events")).TopicArn;
        FileReceivedQueueUrl = (await Sqs.CreateQueueAsync("parser-file-received")).QueueUrl;
        var queueArn = (await Sqs.GetQueueAttributesAsync(FileReceivedQueueUrl, ["QueueArn"])).QueueARN;
        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = _topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new Dictionary<string, string>
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = """{"event-type":["FileReceived"]}""",
            },
        });
    }

    public HttpClient CreateClientAs(string subject, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, subject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        return client;
    }

    public async Task<Message?> WaitForMessageAsync(string queueUrl, Func<Message, bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var response = await Sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1, MessageAttributeNames = ["All"] },
                CancellationToken.None);
            foreach (var message in response.Messages ?? [])
            {
                if (predicate(message))
                {
                    return message;
                }
            }
        }

        return null;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Aws:ServiceUrl", _localStack.GetConnectionString());
        builder.UseSetting("Aws:Region", Region);
        builder.UseSetting("Aws:RawBucket", Bucket);
        builder.UseSetting("Aws:EventsTopicArn", _topicArn);
        builder.UseSetting("Mongo:ConnectionString", _mongo.GetConnectionString());
        builder.UseSetting("Mongo:Database", "careflow_ingestion_tests");
        builder.UseSetting("Auth:Authority", "http://idp.invalid/realms/careflow");
        builder.UseSetting("RateLimiting:UploadsPerMinute", "1000");

        builder.ConfigureTestServices(services => services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        S3?.Dispose();
        Sqs?.Dispose();
        await DisposeAsync();
        await _localStack.DisposeAsync();
        await _mongo.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class IngestionApiTests : ICollectionFixture<IngestionApiFixture>
{
    public const string Name = "IngestionApi";
}
