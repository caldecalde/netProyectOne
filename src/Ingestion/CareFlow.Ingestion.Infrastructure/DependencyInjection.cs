using Amazon;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.Application.Abstractions;
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

    public static IServiceCollection AddIngestionInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AwsOptions>().Bind(configuration.GetSection(AwsOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MongoOptions>().Bind(configuration.GetSection(MongoOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        // AWS clients use the default credential chain (env vars locally, IRSA / instance profile on AWS).
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            var config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(aws.Region) };
            if (!string.IsNullOrWhiteSpace(aws.ServiceUrl))
            {
                config.ServiceURL = aws.ServiceUrl;
                config.ForcePathStyle = true;
            }

            return new AmazonS3Client(config);
        });

        services.AddSingleton<IAmazonSimpleNotificationService>(sp =>
        {
            var aws = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            var config = new AmazonSimpleNotificationServiceConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(aws.Region) };
            if (!string.IsNullOrWhiteSpace(aws.ServiceUrl))
            {
                config.ServiceURL = aws.ServiceUrl;
            }

            return new AmazonSimpleNotificationServiceClient(config);
        });

        services.AddSingleton<IMongoClient>(sp => new MongoClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));

        services.AddSingleton<IRawFileStore, S3RawFileStore>();
        services.AddSingleton<IEventPublisher, SnsEventPublisher>();
        services.AddSingleton<IIngestionFileRepository, MongoIngestionFileRepository>();
        services.AddHostedService<MongoIndexInitializer>();

        services.AddHealthChecks()
            .AddCheck<MongoHealthCheck>("mongodb", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<S3HealthCheck>("s3", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3));

        return services;
    }
}
