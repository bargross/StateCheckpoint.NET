using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;
using System.Runtime.CompilerServices;

namespace StateCheckpoint.NET;

internal class FileSystemSessionStore : FileSystemBase<SessionIndex>, IFileSystemSessionStore
{
    public FileSystemSessionStore(string rootPath, FileSystemStoreOptions? options = null) 
        : base(rootPath, options, indexFilePath => new SessionIndex(indexFilePath))
    {
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

    public async Task DeleteManyAsync(IEnumerable<Guid> sessionIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in sessionIds)
            await DeleteAsync(id, cancellationToken);
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

    public async IAsyncEnumerable<SessionSummary> QueryStreamAsync(
        SessionQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureIndexLoadedAsync(cancellationToken);
        var all = await _index.GetAllAsync(cancellationToken);

        foreach (var summary in all)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply filters
            if (!string.IsNullOrWhiteSpace(query.ModelFingerprint) && summary.ModelFingerprint != query.ModelFingerprint) continue;
            if (query.UpdatedAfter.HasValue && summary.LastUpdated < query.UpdatedAfter.Value) continue;
            if (query.UpdatedBefore.HasValue && summary.LastUpdated > query.UpdatedBefore.Value) continue;
            if (query.Tags != null && query.Tags.Count > 0 && !TagsHelper.TagsMatch(summary.Tags, query.Tags)) continue;

            yield return summary;
        }
    }

    public async Task<SessionSummary?> GetSessionSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var manifest = await FileSystemHelper.LoadManifestOnlyAsync<SessionManifest>(
            _rootPath, sessionId, "meta.json", cancellationToken);

        if (manifest == null) return null;

        return new SessionSummary
        {
            SessionId = sessionId,
            ModelFingerprint = manifest.ModelFingerprint,
            LastUpdated = manifest.LastUpdated,
            Tags = manifest.Tags ?? new Dictionary<string, string>()
        };
    }

    private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken) => await EnsureIndexLoadedAsync(
        () => _index.LoadAsync(cancellationToken),
        () => _index.GetAllAsync(cancellationToken),
        async path => await _index.RebuildAsync(_rootPath, async (string rootPath, Guid id, CancellationToken _) =>
        {
            var manifest = await FileSystemHelper.LoadManifestOnlyAsync<SessionManifest>(rootPath, id, "meta.json", cancellationToken);

            if (manifest == null) return null;

            return new SessionSummary
            {
                SessionId = id,
                ModelFingerprint = manifest.ModelFingerprint,
                LastUpdated = manifest.LastUpdated,
                Tags = manifest.Tags ?? new Dictionary<string, string>()
            };
        }, cancellationToken),
        cancellationToken
    );
}