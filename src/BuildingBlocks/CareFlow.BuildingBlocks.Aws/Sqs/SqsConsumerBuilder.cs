using CareFlow.BuildingBlocks.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CareFlow.BuildingBlocks.Aws.Sqs;

/// <summary>Configures one queue consumer: its options and the handler for each event type the queue receives.</summary>
public sealed class SqsConsumerBuilder
{
    private readonly IServiceCollection _services;
    private readonly SqsConsumerRegistration _registration;

    internal SqsConsumerBuilder(IServiceCollection services, SqsConsumerRegistration registration)
    {
        _services = services;
        _registration = registration;
        Options = services.AddOptions<SqsConsumerOptions>(registration.Name).ValidateDataAnnotations().ValidateOnStart();
    }

    public OptionsBuilder<SqsConsumerOptions> Options { get; }

    public SqsConsumerBuilder Configure(Action<SqsConsumerOptions> configure)
    {
        Options.Configure(configure);
        return this;
    }

    /// <summary>Routes messages whose <c>event-type</c> is <typeparamref name="TEvent"/>'s name to a scoped <typeparamref name="THandler"/>.</summary>
    public SqsConsumerBuilder Handle<TEvent, THandler>()
        where TEvent : IntegrationEvent
        where THandler : class, IMessageHandler<TEvent>
    {
        _services.TryAddScoped<THandler>();
        _registration.Handlers[typeof(TEvent).Name] = new SqsHandlerRegistration(
            typeof(TEvent),
            (services, integrationEvent, cancellationToken) =>
                services.GetRequiredService<THandler>().HandleAsync((TEvent)integrationEvent, cancellationToken));
        return this;
    }
}

internal sealed record SqsHandlerRegistration(Type EventType, Func<IServiceProvider, IntegrationEvent, CancellationToken, Task> Invoke);

internal sealed class SqsConsumerRegistration(string name)
{
    public string Name { get; } = name;

    public Dictionary<string, SqsHandlerRegistration> Handlers { get; } = new(StringComparer.Ordinal);
}
