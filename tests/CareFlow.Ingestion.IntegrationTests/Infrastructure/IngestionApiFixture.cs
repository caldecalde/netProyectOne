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
using MongoDB.Driver;
using Testcontainers.LocalStack;
using Testcontainers.MongoDb;

namespace CareFlow.Ingestion.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real Ingestion API in-memory against real MongoDB (single-node replica set, for transactions) and
/// LocalStack (S3/SNS/SQS) containers. Only authentication is substituted, so tests can act as any role without an IdP.
/// </summary>
public sealed class IngestionApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Bucket = "careflow-raw";
    public const string Database = "careflow_ingestion_tests";
    private const string Region = "us-east-1";

    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:4.9")
        .WithEnvironment("SERVICES", "s3,sns,sqs")
        .Build();

    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8.0")
        .WithReplicaSet()
        .Build();

    private string _parseResultsQueueUrl = string.Empty;

    public AmazonS3Client S3 { get; private set; } = null!;

    public AmazonSQSClient Sqs { get; private set; } = null!;

    public AmazonSimpleNotificationServiceClient Sns { get; private set; } = null!;

    public IMongoDatabase Mongo { get; private set; } = null!;

    public string TopicArn { get; private set; } = string.Empty;

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
        Sns = new AmazonSimpleNotificationServiceClient(credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = endpoint, AuthenticationRegion = Region });
        Mongo = new MongoClient(_mongo.GetConnectionString()).GetDatabase(Database);

        await S3.PutBucketAsync(Bucket);
        TopicArn = (await Sns.CreateTopicAsync("careflow-events")).TopicArn;
        FileReceivedQueueUrl = await CreateSubscribedQueueAsync("parser-file-received", "FileReceived");
        _parseResultsQueueUrl = await CreateSubscribedQueueAsync("ingestion-parse-results", "MessageParsed", "ParseFailed");
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
        builder.UseSetting("Aws:EventsTopicArn", TopicArn);
        builder.UseSetting("Mongo:ConnectionString", _mongo.GetConnectionString());
        builder.UseSetting("Mongo:Database", Database);
        builder.UseSetting("Outbox:PollInterval", "00:00:01");
        builder.UseSetting("Consumers:ParseResults:QueueUrl", _parseResultsQueueUrl);
        builder.UseSetting("Consumers:ParseResults:WaitTimeSeconds", "1");
        builder.UseSetting("Auth:Authority", "http://idp.invalid/realms/careflow");
        builder.UseSetting("RateLimiting:UploadsPerMinute", "1000");

        builder.ConfigureTestServices(services => services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { }));
    }

    private async Task<string> CreateSubscribedQueueAsync(string name, params string[] eventTypes)
    {
        var queueUrl = (await Sqs.CreateQueueAsync(name)).QueueUrl;
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
        return queueUrl;
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
public sealed class IngestionApiTests : ICollectionFixture<IngestionApiFixture>
{
    public const string Name = "IngestionApi";
}
