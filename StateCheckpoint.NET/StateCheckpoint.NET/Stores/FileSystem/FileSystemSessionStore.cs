using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

internal class FileSystemSessionStore : IFileSystemSessionStore
{
    private readonly string _rootPath;
    private readonly FileSystemStoreOptions _options;
    private readonly SessionIndex _index;
    private bool _indexLoaded;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public FileSystemSessionStore(string rootPath, FileSystemStoreOptions? options = null)
    {
        _options = options ?? new FileSystemStoreOptions();
        _rootPath = Path.Combine(rootPath, "sessions");

        if (_options.ValidatePermissionsOnStartup)
        {
            if (!FileSystemHelper.TryValidateWriteAccess(_rootPath, out var error))
            {
                // If fallback is provided, update the root path
                if (!string.IsNullOrWhiteSpace(_options.FallbackPath))
                {
                    _rootPath = Path.Combine(_options.FallbackPath, "sessions");

                    Directory.CreateDirectory(_rootPath);
                }
                else
                {
                    throw error!;
                }
            }
        }
        else
        {
            // Still ensure the directory exists if required
            if (_options.EnsureDirectoryExists)
            {
                Directory.CreateDirectory(_rootPath);
            }
        }

        // Index file path
        var indexFilePath = Path.Combine(_rootPath, "_sessions_index.json");

        _index = new SessionIndex(indexFilePath);
    }

    public async Task SaveAsync(SessionCheckpoint session, CancellationToken cancellationToken = default)
    {
        var manifest = new SessionManifest
        {
            ModelFingerprint = session.ModelFingerprint,
            TokenHistory = session.TokenHistory,
            SamplingConfig = session.SamplingConfig,
            LastUpdated = session.LastUpdated,
            Tags = session.Tags
        };

        await FileSystemHelper.SaveAsync(
            _rootPath,
            session.SessionId,
            session.KvCacheBytes,
            manifest,
            _options,
            binaryFileName: "kv.bin",
            metaFileName: "meta.json",
            cancellationToken: cancellationToken);

        var summary = new SessionSummary
        {
            SessionId = session.SessionId,
            ModelFingerprint = session.ModelFingerprint,
            LastUpdated = session.LastUpdated,
            Tags = session.Tags ?? new Dictionary<string, string>()
        };

        await _index.AddOrUpdateAsync(summary, cancellationToken);

        if (!_indexLoaded) _indexLoaded = true; // index is now loaded
    }

    public async Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (kv, manifest) = await FileSystemHelper.LoadAsync<SessionManifest>(
                _rootPath, sessionId, "kv.bin", "meta.json", cancellationToken);

            return new SessionCheckpoint
            {
                SessionId = sessionId,
                KvCacheBytes = kv,
                ModelFingerprint = manifest.ModelFingerprint,
                TokenHistory = manifest.TokenHistory,
                SamplingConfig = manifest.SamplingConfig,
                LastUpdated = manifest.LastUpdated,
                Tags = manifest.Tags
            };
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await FileSystemHelper.DeleteAsync(_rootPath, sessionId, cancellationToken);

        await _index.RemoveAsync(sessionId, cancellationToken);
    }

    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tagKey) || string.IsNullOrWhiteSpace(tagValue))
            return await FileSystemHelper.ListAsync(_rootPath, cancellationToken);

        await EnsureIndexLoadedAsync(cancellationToken);

        return (await _index.GetAllAsync(cancellationToken))
            .Where(s => s.Tags != null && s.Tags.TryGetValue(tagKey, out var val) && val == tagValue)
            .Select(s => s.SessionId)
            .ToList();
    }

    public async Task<List<SessionSummary>> QueryAsync(SessionQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureIndexLoadedAsync(cancellationToken);

        return await _index.QueryAsync(query, cancellationToken);
    }

    // Rebuild index from all session manifest files (fallback)
    public async Task RebuildIndexAsync(CancellationToken cancellationToken)
    {
        await _buildLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring the lock
            await EnsureIndexLoadedAsync(cancellationToken);

            var existing = await _index.GetAllAsync(cancellationToken);

            if (existing.Any()) return; // already rebuilt by another thread

            await _index.RebuildAsync(_rootPath, LoadManifestSummaryAsync, cancellationToken);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    // Ensure the index is loaded into memory (lazy)
    private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
    {
        if (!_indexLoaded)
        {
            await _index.LoadAsync(cancellationToken);

            _indexLoaded = true;
        }
    }

    private async Task<SessionSummary?> LoadManifestSummaryAsync(string rootPath, Guid id, CancellationToken ct)
    {
        var manifest = await FileSystemHelper.LoadManifestOnlyAsync<SessionManifest>(
            rootPath, id, "meta.json", ct);
        if (manifest == null) return null;
        return new SessionSummary
        {
            SessionId = id,
            ModelFingerprint = manifest.ModelFingerprint,
            LastUpdated = manifest.LastUpdated,
            Tags = manifest.Tags ?? new Dictionary<string, string>()
        };
    }
}