using System.Diagnostics;
using CareFlow.BuildingBlocks.Messaging;
using CareFlow.Ingestion.Application.Abstractions;
using CareFlow.Ingestion.Domain;
using CareFlow.Ingestion.Infrastructure.Messaging;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure.Persistence;

internal sealed class MongoIngestionFileRepository(IMongoDatabase database, OutboxSignal outboxSignal, TimeProvider timeProvider)
    : IIngestionFileRepository
{
    public const string CollectionName = "ingestion_files";

    private readonly IMongoCollection<IngestionFileDocument> _collection =
        database.GetCollection<IngestionFileDocument>(CollectionName);

    private readonly IMongoCollection<OutboxMessage> _outbox =
        database.GetCollection<OutboxMessage>(OutboxMessage.CollectionName);

    public async Task<bool> TryAddAsync(IngestionFile file, IReadOnlyCollection<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var now = timeProvider.GetUtcNow();
        var outboxMessages = events.Select(e => OutboxMessage.From(e, Activity.Current, now)).ToList();

        // Multi-document transactions need a replica set; locally MongoDB runs as a single-node one.
        using var session = await database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        try
        {
            await session.WithTransactionAsync(
                async (s, ct) =>
                {
                    await _collection.InsertOneAsync(s, IngestionFileDocument.FromDomain(file), cancellationToken: ct);
                    if (outboxMessages.Count > 0)
                    {
                        await _outbox.InsertManyAsync(s, outboxMessages, cancellationToken: ct);
                    }

                    return true;
                },
                cancellationToken: cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }

        if (outboxMessages.Count > 0)
        {
            outboxSignal.Notify();
        }

        return true;
    }

    public async Task<IngestionFile?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = await _collection.Find(d => d.Id == id).FirstOrDefaultAsync(cancellationToken);
        return document?.ToDomain();
    }

    public async Task<IngestionFile?> FindBySha256Async(string sha256, CancellationToken cancellationToken)
    {
        var document = await _collection.Find(d => d.Sha256 == sha256).FirstOrDefaultAsync(cancellationToken);
        return document?.ToDomain();
    }

    public async Task<bool> SaveOutcomeAsync(IngestionFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Conditional on the stored status, so two concurrent deliveries of a result cannot both apply it.
        var result = await _collection.UpdateOneAsync(
            d => d.Id == file.Id && d.Status == IngestionStatus.Received,
            Builders<IngestionFileDocument>.Update
                .Set(d => d.Status, file.Status)
                .Set(d => d.FailureReason, file.FailureReason),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }

    /// <summary>Idempotent; the unique hash index is what makes duplicate detection race-safe.</summary>
    public static Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<IngestionFileDocument>(CollectionName);
        var keys = Builders<IngestionFileDocument>.IndexKeys;
        return collection.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<IngestionFileDocument>(keys.Ascending(d => d.Sha256), new CreateIndexOptions { Unique = true, Name = "ux_sha256" }),
                new CreateIndexModel<IngestionFileDocument>(keys.Ascending(d => d.SubmittedBy).Descending(d => d.ReceivedAt), new CreateIndexOptions { Name = "ix_submitter_received" }),
            ],
            cancellationToken);
    }
}
