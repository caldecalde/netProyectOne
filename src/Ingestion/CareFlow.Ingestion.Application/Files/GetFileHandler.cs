using CareFlow.BuildingBlocks.Results;
using CareFlow.Ingestion.Application.Abstractions;

namespace CareFlow.Ingestion.Application.Files;

public sealed class GetFileHandler(IIngestionFileRepository repository)
{
    public async Task<Result<IngestionFileDto>> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = await repository.GetAsync(id, cancellationToken);
        return file is null ? IngestionErrors.NotFound(id) : IngestionFileDto.From(file);
    }
}
