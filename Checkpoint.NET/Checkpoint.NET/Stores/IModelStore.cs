using Checkpoint.NET.Models;

namespace Checkpoint.NET.Stores
{
    public interface IModelStore
    {
        Task SaveAsync(ModelCheckpoint checkpoint, CancellationToken ct = default);
        Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken ct = default);
        Task DeleteAsync(Guid modelId, CancellationToken ct = default);
        Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken ct = default);
    }
}
