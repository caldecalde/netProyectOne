using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using CareFlow.BuildingBlocks.Aws;
using CareFlow.BuildingBlocks.Aws.Sqs;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.Application.Abstractions;
using CareFlow.Ingestion.Application.Files;
using CareFlow.Ingestion.Infrastructure.HealthChecks;
using CareFlow.Ingestion.Infrastructure.Messaging;
using CareFlow.Ingestion.Infrastructure.Persistence;
using CareFlow.Ingestion.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    /// <summary>Configuration section of the consumer that receives parse results; disabled when its <c>QueueUrl</c> is empty.</summary>
    public const string ParseResultsConsumerSection = "Consumers:ParseResults";

    public static IServiceCollection AddIngestionInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AwsOptions>().Bind(configuration.GetSection(AwsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MongoOptions>().Bind(configuration.GetSection(MongoOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

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

        services.AddSingleton<IRawFileStore, S3RawFileStore>();
        services.AddSingleton<IIngestionFileRepository, MongoIngestionFileRepository>();
        services.AddHostedService<MongoIndexInitializer>();

        // Transactional outbox: events are committed with the file record and relayed to SNS in the background.
        services.AddSnsEventPublisher().Configure<IOptions<AwsOptions>>((sns, aws) => sns.TopicArn = aws.Value.EventsTopicArn);
        services.AddSingleton<OutboxSignal>();
        services.AddHostedService<OutboxRelay>();

        if (!string.IsNullOrWhiteSpace(configuration[$"{ParseResultsConsumerSection}:QueueUrl"]))
        {
            services.AddSqsConsumer("ingestion-parse-results")
                .Configure(options => configuration.GetSection(ParseResultsConsumerSection).Bind(options))
                .Handle<MessageParsed, ParseResultHandler>()
                .Handle<ParseFailed, ParseResultHandler>();
        }

        services.AddHealthChecks()
            .AddCheck<MongoHealthCheck>("mongodb", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<S3HealthCheck>("s3", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
