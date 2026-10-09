using CareFlow.BuildingBlocks.Aws.Sns;
using CareFlow.BuildingBlocks.Aws.Sqs;
using CareFlow.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CareFlow.BuildingBlocks.Aws;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SNS publisher as both <see cref="IEventPublisher"/> and <see cref="IEventEnvelopePublisher"/>.
    /// Requires an <c>IAmazonSimpleNotificationService</c> registration. Configure the topic through the returned builder.
    /// </summary>
    public static OptionsBuilder<SnsPublisherOptions> AddSnsEventPublisher(this IServiceCollection services)
    {
        services.TryAddSingleton<SnsEventPublisher>();
        services.TryAddSingleton<IEventPublisher>(sp => sp.GetRequiredService<SnsEventPublisher>());
        services.TryAddSingleton<IEventEnvelopePublisher>(sp => sp.GetRequiredService<SnsEventPublisher>());
        return services.AddOptions<SnsPublisherOptions>().ValidateDataAnnotations().ValidateOnStart();
    }

    /// <summary>
    /// Adds a background consumer for one SQS queue. <paramref name="queueName"/> labels its spans, logs and metrics.
    /// Requires an <c>IAmazonSQS</c> registration.
    /// </summary>
    public static SqsConsumerBuilder AddSqsConsumer(this IServiceCollection services, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        var registration = new SqsConsumerRegistration(queueName);
        services.TryAddSingleton(TimeProvider.System);

        // A factory registration, not AddHostedService: that de-duplicates by type and would keep only one consumer.
        services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<SqsMessagePump>(
            sp,
            registration,
            sp.GetRequiredService<IOptionsMonitor<SqsConsumerOptions>>().Get(queueName)));

        return new SqsConsumerBuilder(services, registration);
    }
}
