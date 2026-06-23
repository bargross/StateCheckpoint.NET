using Checkpoint.NET.Models;
using Checkpoint.NET.Stores;

namespace Checkpoint.NET.Manager;

public class CheckpointManager
{
    private readonly IModelStore _store;

    public CheckpointManager(IModelStore store) => _store = store;

    // Save a checkpoint. If existingId is null, generates a new GUID.
    public async Task<Guid> SaveAsync(
        byte[] weights,
        byte[] optimizer,
        HyperParameters hyperParams,
        TokenizerData tokenizer,
        int epoch,
        float loss,
        Guid? existingId = null,
        Dictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        var checkpoint = new ModelCheckpoint
        {
            ModelId = existingId ?? Guid.NewGuid(),
            WeightsBytes = weights,
            OptimizerBytes = optimizer,
            HyperParams = hyperParams,
            Tokenizer = tokenizer,
            CurrentEpoch = epoch,
            LastTrainingLoss = loss,
            CreatedAt = DateTime.UtcNow,
            Tags = tags ?? new Dictionary<string, string>()
        };

        await _store.SaveAsync(checkpoint, ct);

        return checkpoint.ModelId;
    }

    // Load a checkpoint (returns full state, including raw bytes)
    public async Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken ct = default)
        => await _store.LoadAsync(modelId, ct);

    // Delete a checkpoint
    public async Task DeleteAsync(Guid modelId, CancellationToken ct = default)
        => await _store.DeleteAsync(modelId, ct);

    // List all saved model IDs (optionally filter by tag)
    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken ct = default)
        => await _store.ListAsync(tagKey, tagValue, ct);
}