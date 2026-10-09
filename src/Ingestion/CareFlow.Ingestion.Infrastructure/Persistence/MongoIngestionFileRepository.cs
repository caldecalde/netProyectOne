using CareFlow.Ingestion.Application.Abstractions;
using CareFlow.Ingestion.Domain;
using MongoDB.Driver;

namespace CareFlow.Ingestion.Infrastructure.Persistence;

internal sealed class MongoIngestionFileRepository(IMongoDatabase database) : IIngestionFileRepository
{
    public const string CollectionName = "ingestion_files";

    private readonly IMongoCollection<IngestionFileDocument> _collection =
        database.GetCollection<IngestionFileDocument>(CollectionName);

    public async Task<bool> TryAddAsync(IngestionFile file, CancellationToken cancellationToken)
    {
        try
        {
            await _collection.InsertOneAsync(IngestionFileDocument.FromDomain(file), cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
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
