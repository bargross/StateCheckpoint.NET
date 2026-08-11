using StateCheckpoint.NET.Models;

namespace StateCheckpoint.NET;

internal interface IModelStore
{
    Task SaveAsync(ModelCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid modelId, CancellationToken cancellationToken = default);
    Task DeleteManyAsync(IEnumerable<Guid> modelIds, CancellationToken cancellationToken = default);
    Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default);
    Task<List<CheckpointSummary>> QueryAsync(CheckpointQuery query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CheckpointSummary> QueryStreamAsync(CheckpointQuery query, CancellationToken cancellationToken = default);
    Task<CheckpointSummary?> GetCheckpointSummaryAsync(Guid modelId, CancellationToken cancellationToken = default);
}
