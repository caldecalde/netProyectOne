using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure.Persistence;

internal sealed class MongoIndexInitializer(IMongoDatabase database) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        MongoIngestionFileRepository.EnsureIndexesAsync(database, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
