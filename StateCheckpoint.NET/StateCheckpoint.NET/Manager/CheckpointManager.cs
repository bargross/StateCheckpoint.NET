using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

public class CheckpointManager : IAsyncDisposable
{
    private readonly IModelStore _store;
    private readonly BackgroundSaver<ModelCheckpoint>? _backgroundSaver;

    /// <summary>
    /// Initializes the manager with a custom storage provider.
    /// </summary>
    /// <param name="storageOptions"></param>
    /// <param name="backgroundOptions"></param>
    public CheckpointManager(StorageOptions storageOptions)
    {
        // Validate options
        if (storageOptions.StoreType == StoreType.SqlDb && string.IsNullOrWhiteSpace(storageOptions?.DbStoreOptions?.ConnectionString))
            throw new InvalidOperationException("ConnectionString is required when StoreType is SqlDb.");

        if (storageOptions.StoreType == StoreType.Local && string.IsNullOrWhiteSpace(storageOptions?.FileSystemStoreOptions?.RootPath))
            throw new InvalidOperationException("RootPath is required when StoreType is Local.");

        _store = StoreModelFactory.Create(storageOptions);

        // Ensure schema only if the store is a database store
        if (storageOptions?.DbStoreOptions?.EnsureSchemaOnStartup == true && _store is IDbModelStore dbStore)
        {
            dbStore.EnsureSchemaAsync().GetAwaiter().GetResult();
        }

        if (storageOptions?.BackgroundSaveOptions?.Enabled == true)
        {
            _backgroundSaver = new BackgroundSaver<ModelCheckpoint>(
                capacity: storageOptions.BackgroundSaveOptions.QueueCapacity,
                onError: storageOptions?.BackgroundSaveOptions?.OnError);
        }
    }

    /// <summary>
    /// Save a checkpoint. If existingId is null, generates a new GUID.
    /// </summary>
    /// <param name="weights">Raw byte array of model weights.</param>
    /// <param name="optimizer">Raw byte array of optimizer state (momentum, variance, etc.).</param>
    /// <param name="hyperParams">Hyperparameters used for this model.</param>
    /// <param name="tokenizer">Complete tokenizer state (vocab, merge rules, special tokens).</param>
    /// <param name="epoch">Current training epoch.</param>
    /// <param name="loss">Current training loss.</param>
    /// <param name="existingId">Optional existing GUID to overwrite. If null, a new one is generated.</param>
    /// <param name="tags">Optional user-defined tags for filtering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Returns the ModelId (GUID) of the saved checkpoint.</returns>
    public async Task<Guid> SaveAsync(
        byte[] weights,
        byte[] optimizer,
        HyperParameters hyperParams,
        TokenizerData tokenizer,
        int epoch,
        float loss,
        Guid? existingId = null,
        Dictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        if (_backgroundSaver != null)
        {
            var capturedCheckpoint = new ModelCheckpoint
            {
                ModelId = checkpoint.ModelId,
                WeightsBytes = checkpoint.WeightsBytes.ToArray(),
                OptimizerBytes = checkpoint.OptimizerBytes.ToArray(),
                HyperParams = checkpoint.HyperParams,
                Tokenizer = checkpoint.Tokenizer,
                CurrentEpoch = checkpoint.CurrentEpoch,
                LastTrainingLoss = checkpoint.LastTrainingLoss,
                CreatedAt = checkpoint.CreatedAt,
                Tags = checkpoint.Tags
            };

            await _backgroundSaver.EnqueueAsync(async (ct) => await _store.SaveAsync(capturedCheckpoint, ct), cancellationToken);

            return checkpoint.ModelId;
        }

        await _store.SaveAsync(checkpoint, cancellationToken);

        return checkpoint.ModelId;
    }

    /// Finds the checkpoint with the highest score according to a user-supplied selector.
    /// Queries only summaries (no binary data) and loads the winning checkpoint.
    /// </summary>
    /// <param name="scoreSelector">Function that maps a CheckpointSummary to a numeric score (higher = better).</param>
    /// <param name="filter">Optional query filter to narrow the search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full ModelCheckpoint with the highest score, or null if none exist.</returns>
    public async Task<ModelCheckpoint?> FindBestAsync(
        Func<CheckpointSummary, float> scoreSelector,
        CheckpointQuery? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new CheckpointQuery();
        var summaries = await _store.QueryAsync(filter, cancellationToken);

        if (summaries.Count == 0) return null;

        var bestSummary = summaries.MaxBy(scoreSelector);

        if (bestSummary == null) return null;

        return await _store.LoadAsync(bestSummary.ModelId, cancellationToken);
    }

    /// <summary>
    /// Finds the checkpoint with the lowest training loss (optionally filtered).
    /// </summary>
    public Task<ModelCheckpoint?> FindBestByLossAsync(CheckpointQuery? filter = null, CancellationToken cancellationToken = default)
        => FindBestAsync(s => -s.LastTrainingLoss, filter, cancellationToken);

    /// <summary>
    /// Finds the checkpoint with the most recent epoch (optionally filtered).
    /// </summary>
    public Task<ModelCheckpoint?> FindLatestEpochAsync(CheckpointQuery? filter = null, CancellationToken cancellationToken = default)
        => FindBestAsync(s => s.CurrentEpoch, filter, cancellationToken);

    public Task<List<CheckpointSummary>> QueryAsync(CheckpointQuery query, CancellationToken cancellationToken = default)
        => _store.QueryAsync(query, cancellationToken);

    public IAsyncEnumerable<CheckpointSummary> QueryStreamAsync(CheckpointQuery query, CancellationToken cancellationToken = default)
        => _store.QueryStreamAsync(query, cancellationToken);

    /// <summary>
    /// Load a checkpoint.
    /// </summary>
    /// <param name="modelId">The GUID of the checkpoint to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Returns the full ModelCheckpoint, including raw weight/optimizer bytes.</returns>
    public async Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken cancellationToken = default)
        => await _store.LoadAsync(modelId, cancellationToken);

    /// <summary>
    /// Delete a checkpoint.
    /// </summary>
    /// <param name="modelId">The GUID of the checkpoint to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteAsync(Guid modelId, CancellationToken cancellationToken = default)
        => await _store.DeleteAsync(modelId, cancellationToken);

    /// <summary>
    /// List all saved model IDs, optionally filtered by a tag key/value pair.
    /// </summary>
    /// <param name="tagKey">Optional tag key to filter by.</param>
    /// <param name="tagValue">Optional tag value to filter by (requires tagKey).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of GUIDs matching the filter (or all if no filter provided).</returns>
    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default)
        => await _store.ListAsync(tagKey, tagValue, cancellationToken);

    /// <summary>
    /// Disposes the manager and ensures the background saver finishes all pending operations.
    /// Must be called if background saves are enabled.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_backgroundSaver != null)
        {
            await _backgroundSaver.DisposeAsync();
        }
    }
}