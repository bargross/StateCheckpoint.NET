using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

public class CheckpointManager : IAsyncDisposable
{
    private readonly IModelStore _store;
    private readonly BackgroundSaver<ModelCheckpoint>? _backgroundSaver;
    private readonly RetentionPolicy _retentionPolicy;

    /// <summary>
    /// Initializes the manager with a custom storage provider.
    /// </summary>
    /// <param name="storageOptions"></param>
    /// <param name="backgroundOptions"></param>
    public CheckpointManager(StorageOptions storageOptions)
    {
        storageOptions.Validate();

        _retentionPolicy = storageOptions.RetentionPolicy ?? new RetentionPolicy();

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
        ModelCheckpoint modelCheckpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_backgroundSaver != null)
        {
            await _backgroundSaver.EnqueueAsync(async (ct) => await _store.SaveAsync(modelCheckpoint, ct), cancellationToken);

            return modelCheckpoint.ModelId;
        }

        await _store.SaveAsync(modelCheckpoint, cancellationToken);

        if (_retentionPolicy != null)
            await this.InternalApplyRetentionPolicyAsync(cancellationToken);

        return modelCheckpoint.ModelId;
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

    /// <summary>
    /// 
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
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
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<int> ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default) 
        => await this.InternalApplyRetentionPolicyAsync(cancellationToken);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="baseLineId"></param>
    /// <param name="CandidateId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<CheckpointDiff> CompareAsync(Guid baseLineId, Guid CandidateId, CancellationToken cancellationToken = default) 
        => await this.InternalCompareAsync(baseLineId, CandidateId, cancellationToken);

    /// <summary>
    /// Disposes the manager and ensures the background saver finishes all pending operations.
    /// Must be called if background saves are enabled.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_backgroundSaver != null)
            await _backgroundSaver.DisposeAsync();
        
        if (_store is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
    }

    //----------- Internal -------------//

    internal RetentionPolicy RetentionPolicy
    {
        get { return _retentionPolicy; }
    }

    internal IModelStore Store
    {
        get { return _store; }
    }
}