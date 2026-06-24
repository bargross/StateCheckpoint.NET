using Checkpoint.NET.Models;
using Checkpoint.NET.Stores;
using Checkpoint.NET.Stores.FileSystem;

namespace Checkpoint.NET.Manager;

public class CheckpointManager
{
    private readonly IModelStore _store;
    private static string _defaultCheckpointPath = "./checkpoints";

    /// <summary>
    /// Initializes the manager with a custom storage provider.
    /// </summary>
    /// <param name="store">Any implementation of IModelStore (FileSystem, PostgreSQL, etc.)</param>
    public CheckpointManager(IModelStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Initializes the manager with the default FileSystem store.
    /// Models are saved to ./checkpoints by default.
    /// </summary>
    public CheckpointManager() : this(new FileSystemModelStore(_defaultCheckpointPath))
    {
    }

    /// <summary>
    /// Initializes the manager with the default FileSystem store at a custom root path.
    /// </summary>
    /// <param name="rootPath">Root directory where checkpoints will be stored.</param>
    public CheckpointManager(string rootPath) : this(new FileSystemModelStore(rootPath))
    {
    }

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