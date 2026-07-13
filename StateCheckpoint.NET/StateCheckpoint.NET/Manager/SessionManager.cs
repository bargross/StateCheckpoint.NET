using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

public class SessionManager : IAsyncDisposable
{
    private readonly ISessionStore _store;
    private readonly BackgroundSaver<SessionCheckpoint>? _backgroundSaver;

    /// <summary>
    /// Initializes the manager with a custom storage provider.
    /// </summary>
    /// <param name="store">Any implementation of ISessionStore (FileSystem, PostgreSQL, etc.)</param>
    public SessionManager(StorageOptions storageOptions)
    {
        // ... same validation as CheckpointManager
        _store = StoreSessionFactory.Create(storageOptions);

        if (storageOptions?.DbStoreOptions?.EnsureSchemaOnStartup == true && _store is IDbSessionStore dbStore)
        {
            dbStore.EnsureSchemaAsync().GetAwaiter().GetResult();
        }

        if (storageOptions?.BackgroundSaveOptions?.Enabled == true)
        {
            _backgroundSaver = new BackgroundSaver<SessionCheckpoint>(
                capacity: storageOptions.BackgroundSaveOptions.QueueCapacity,
                onError: storageOptions?.BackgroundSaveOptions?.OnError);
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
        Guid sessionId,
        byte[] kvCacheBytes,
        int[] tokenHistory,
        string modelFingerprint,
        SamplingData? samplingConfig = null,
        Dictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = new SessionCheckpoint
        {
            SessionId = sessionId,
            KvCacheBytes = kvCacheBytes,
            TokenHistory = tokenHistory,
            ModelFingerprint = modelFingerprint,
            SamplingConfig = samplingConfig ?? new SamplingData(),
            LastUpdated = DateTime.UtcNow,
            Tags = tags ?? new Dictionary<string, string>()
        };

        if (_backgroundSaver != null)
        {
            var capturedSession = new SessionCheckpoint
            {
                SessionId = session.SessionId,
                KvCacheBytes = session.KvCacheBytes.ToArray(),
                TokenHistory = session.TokenHistory,
                ModelFingerprint = session.ModelFingerprint,
                SamplingConfig = session.SamplingConfig,
                LastUpdated = session.LastUpdated,
                Tags = session.Tags
            };

            await _backgroundSaver.EnqueueAsync(async (cToken) => await _store.SaveAsync(capturedSession, cToken));

            return session.SessionId;
        }

        await _store.SaveAsync(session, cancellationToken);

        return session.SessionId;
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

    public Task<List<SessionSummary>> QueryAsync(SessionQuery query, CancellationToken cancellationToken = default)
        => _store.QueryAsync(query, cancellationToken);

    public IAsyncEnumerable<SessionSummary> QueryStreamAsync(SessionQuery query, CancellationToken cancellationToken = default)
        => _store.QueryStreamAsync(query, cancellationToken);

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
}