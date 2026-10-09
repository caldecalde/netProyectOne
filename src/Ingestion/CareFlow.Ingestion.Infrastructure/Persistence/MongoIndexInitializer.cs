using CareFlow.Ingestion.Infrastructure.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure.Persistence;

internal sealed class MongoIndexInitializer(IMongoDatabase database, IOptions<OutboxOptions> outboxOptions) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await MongoIngestionFileRepository.EnsureIndexesAsync(database, cancellationToken);
        await OutboxMessage.EnsureIndexesAsync(database, outboxOptions.Value.Retention, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
