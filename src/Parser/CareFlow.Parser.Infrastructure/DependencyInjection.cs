using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using CareFlow.BuildingBlocks.Aws;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Parser.Application.Abstractions;
using CareFlow.Parser.Application.Parsing;
using CareFlow.Parser.Infrastructure.HealthChecks;
using CareFlow.Parser.Infrastructure.Hl7;
using CareFlow.Parser.Infrastructure.Persistence;
using CareFlow.Parser.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CareFlow.Parser.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    /// <summary>Name of the consumer of <c>FileReceived</c> events; its options are bound from <see cref="FileReceivedConsumerSection"/>.</summary>
    public const string FileReceivedQueue = "parser-file-received";

    public const string FileReceivedConsumerSection = "Consumers:FileReceived";

    public static IServiceCollection AddParserInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AwsOptions>().Bind(configuration.GetSection(AwsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MongoOptions>().Bind(configuration.GetSection(MongoOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        // AWS clients use the default credential chain (env vars locally, IRSA / instance profile on AWS).
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            var config = AwsClientConfig.Create<AmazonS3Config>(aws.Region, aws.ServiceUrl);
            config.ForcePathStyle = !string.IsNullOrWhiteSpace(aws.ServiceUrl);
            return new AmazonS3Client(config);
        });
        services.AddSingleton<IAmazonSimpleNotificationService>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            return new AmazonSimpleNotificationServiceClient(AwsClientConfig.Create<AmazonSimpleNotificationServiceConfig>(aws.Region, aws.ServiceUrl));
        });
        services.AddSingleton<IAmazonSQS>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            return new AmazonSQSClient(AwsClientConfig.Create<AmazonSQSConfig>(aws.Region, aws.ServiceUrl));
        });

        services.AddSingleton<IMongoClient>(sp => new MongoClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));
        services.AddHostedService<MongoIndexInitializer>();

        services.AddSingleton<IFileParser, NHapiHl7FileParser>();
        services.AddSingleton<IRawFileReader, S3RawFileReader>();
        services.AddSingleton<IParseResultStore, S3ParseResultStore>();
        services.AddSingleton<IProcessedEventStore, MongoProcessedEventStore>();

        // The parser publishes its results directly: SQS only deletes the input message after the publish succeeds,
        // so a failed publish is retried by redelivery and no outbox is needed here.
        services.AddSnsEventPublisher().Configure<IOptions<AwsOptions>>((sns, aws) => sns.TopicArn = aws.Value.EventsTopicArn);
        services.AddSqsConsumer(FileReceivedQueue)
            .Configure(options => configuration.GetSection(FileReceivedConsumerSection).Bind(options))
            .Handle<FileReceived, ParseFileHandler>();

        services.AddHealthChecks()
            .AddCheck<MongoHealthCheck>("mongodb", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<S3HealthCheck>("s3", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<SqsHealthCheck>("sqs", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
