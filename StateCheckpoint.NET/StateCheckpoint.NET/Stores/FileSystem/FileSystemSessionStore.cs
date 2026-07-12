using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET.Stores;

public class FileSystemSessionStore : ISessionStore
{
    private readonly string _rootPath;
    private readonly FileSystemStoreOptions _options;

    public FileSystemSessionStore(string rootPath, FileSystemStoreOptions? options = null)
    {
        _options = options ?? new FileSystemStoreOptions();
        _rootPath = Path.Combine(rootPath, "sessions");

        if (_options.ValidatePermissionsOnStartup)
        {
            if (!FileSystemHelper.TryValidateWriteAccess(_rootPath, out var error))
            {
                // If fallback is provided, update the root path
                if (!string.IsNullOrEmpty(_options.FallbackPath))
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
    }

    public async Task SaveAsync(SessionCheckpoint session, CancellationToken ct = default)
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
            cancellationToken: ct);
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

    public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => FileSystemHelper.DeleteAsync(_rootPath, sessionId, cancellationToken);

    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default)
    {
        var allIds = await FileSystemHelper.ListAsync(_rootPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(tagKey) || string.IsNullOrWhiteSpace(tagValue))
            return allIds;

        var filtered = new List<Guid>();
        foreach (var id in allIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifest = await FileSystemHelper.LoadManifestOnlyAsync<SessionManifest>(_rootPath, id, "meta.json", cancellationToken);
            if (manifest != null && manifest.Tags != null && manifest.Tags.TryGetValue(tagKey, out var val) && val == tagValue)
                filtered.Add(id);
        }

        return filtered;
    }
}