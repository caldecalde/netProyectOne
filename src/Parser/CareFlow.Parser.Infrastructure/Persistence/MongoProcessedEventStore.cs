using CareFlow.Parser.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CareFlow.Parser.Infrastructure.Persistence;

internal sealed class ProcessedEventDocument
{
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid EventId { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime ProcessedAt { get; set; }
}

/// <summary>Inbox of handled event IDs. Entries expire through a TTL index once a redelivery is no longer possible.</summary>
internal sealed class MongoProcessedEventStore(IMongoDatabase database, TimeProvider timeProvider) : IProcessedEventStore
{
    public const string CollectionName = "processed_events";

    private readonly IMongoCollection<ProcessedEventDocument> _collection = database.GetCollection<ProcessedEventDocument>(CollectionName);

    public Task<bool> IsProcessedAsync(Guid eventId, CancellationToken cancellationToken) =>
        _collection.Find(d => d.EventId == eventId).AnyAsync(cancellationToken);

    public async Task MarkProcessedAsync(Guid eventId, CancellationToken cancellationToken)
    {
        // Upsert: a concurrent duplicate delivery may have recorded the same ID first, which is fine.
        await _collection.ReplaceOneAsync(
            d => d.EventId == eventId,
            new ProcessedEventDocument { EventId = eventId, ProcessedAt = timeProvider.GetUtcNow().UtcDateTime },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public static Task EnsureIndexesAsync(IMongoDatabase database, TimeSpan retention, CancellationToken cancellationToken) =>
        database.GetCollection<ProcessedEventDocument>(CollectionName).Indexes.CreateOneAsync(
            new CreateIndexModel<ProcessedEventDocument>(
                Builders<ProcessedEventDocument>.IndexKeys.Ascending(d => d.ProcessedAt),
                new CreateIndexOptions { Name = "ttl_processed", ExpireAfter = retention }),
            cancellationToken: cancellationToken);
}

internal sealed class MongoIndexInitializer(IMongoDatabase database, IOptions<MongoOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        MongoProcessedEventStore.EnsureIndexesAsync(database, options.Value.ProcessedEventRetention, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
