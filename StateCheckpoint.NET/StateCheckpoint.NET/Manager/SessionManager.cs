using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

/// <summary>
/// 
/// </summary>
public class SessionManager : IAsyncDisposable
{
    private readonly ISessionStore _store;
    private readonly BackgroundSaver<SessionCheckpoint>? _backgroundSaver;
    private readonly RetentionPolicy _retentionPolicy;

    /// <summary>
    /// Initializes the manager with a custom storage provider.
    /// </summary>
    /// <param name="store">Any implementation of ISessionStore (FileSystem, PostgreSQL, etc.)</param>
    public SessionManager(StorageOptions storageOptions)
    {
        storageOptions.Validate();

        _retentionPolicy = storageOptions.RetentionPolicy ?? new RetentionPolicy();

        _store = StoreSessionFactory.Create(storageOptions);

        if (storageOptions.DbStoreOptions?.EnsureSchemaOnStartup == true && _store is IDbSessionStore dbStore)
        {
            dbStore.EnsureSchemaAsync().GetAwaiter().GetResult();
        }

        if (storageOptions.BackgroundSaveOptions?.Enabled == true)
        {
            _backgroundSaver = new BackgroundSaver<SessionCheckpoint>(
                capacity: storageOptions.BackgroundSaveOptions.QueueCapacity,
                onError: storageOptions.BackgroundSaveOptions?.OnError);
        }
    }

    /// <summary>
    /// Save a session state.
    /// </summary>
    /// <param name="sessionId">The GUID identifier for this session.</param>
    /// <param name="kvCacheBytes">Raw byte array of the KV-cache from the inference engine.</param>
    /// <param name="tokenHistory">Array of token IDs processed so far.</param>
    /// <param name="modelFingerprint">Unique identifier for the model (e.g., SHA256 hash of weights).</param>
    /// <param name="samplingConfig">Sampling configuration (temperature, top-p, etc.).</param>
    /// <param name="tags">Optional user-defined tags for filtering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Returns the SessionId (GUID) of the saved session.</returns>
    public async Task<Guid> SaveAsync(
        SessionCheckpoint sessionCheckpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_backgroundSaver != null)
        { 
            await _backgroundSaver.EnqueueAsync(async (cToken) => await _store.SaveAsync(sessionCheckpoint, cToken));

            return sessionCheckpoint.SessionId;
        }

        await _store.SaveAsync(sessionCheckpoint, cancellationToken);

        if (_retentionPolicy != null)
            await this.InternalApplyRetentionPolicyAsync(cancellationToken);

        return sessionCheckpoint.SessionId;
    }

    /// <summary>
    /// Load a session state.
    /// </summary>
    /// <param name="sessionId">The GUID of the session to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Returns the full SessionCheckpoint, including raw KV-cache bytes.</returns>
    public async Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => await _store.LoadAsync(sessionId, cancellationToken);

    /// <summary>
    /// Delete a session.
    /// </summary>
    /// <param name="sessionId">The GUID of the session to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => await _store.DeleteAsync(sessionId, cancellationToken);

    /// <summary>
    /// List all saved session IDs, optionally filtered by a tag key/value pair.
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
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<List<SessionSummary>> QueryAsync(SessionQuery query, CancellationToken cancellationToken = default)
        => _store.QueryAsync(query, cancellationToken);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public IAsyncEnumerable<SessionSummary> QueryStreamAsync(SessionQuery query, CancellationToken cancellationToken = default)
        => _store.QueryStreamAsync(query, cancellationToken);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="baseLineId"></param>
    /// <param name="CandidateId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SessionDiff> CompareAsync(Guid baseLineId, Guid CandidateId, CancellationToken cancellationToken = default)
        => await this.InternalCompareAsync(baseLineId, CandidateId, cancellationToken);

    /// <summary>
    /// Disposes the manager and ensures the background saver finishes all pending operations.
    /// <para>
    /// <strong>IMPORTANT:</strong> You MUST call this method when background saves are enabled.
    /// Failure to do so will leave background threads running and may prevent your application from exiting.
    /// </para>
    /// <para>
    /// Use <c>await using (var manager = new CheckpointManager(options))</c> to automatically call DisposeAsync.
    /// </para>
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

    internal ISessionStore Store
    {
        get { return _store; }
    }
}