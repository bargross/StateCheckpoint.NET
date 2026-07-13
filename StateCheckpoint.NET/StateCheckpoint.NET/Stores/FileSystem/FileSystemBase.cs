using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

internal abstract class FileSystemBase<TIndex>
{
    private readonly string _rootPath;
    private readonly FileSystemStoreOptions _options;
    private readonly TIndex _index;
    private bool _indexLoaded;
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    public FileSystemBase(string rootPath, FileSystemStoreOptions options, Func<string, TIndex> indexSelector)
    {
        _options = options ?? new FileSystemStoreOptions();
        _rootPath = Path.Combine(rootPath, "models");

        if (_options.ValidatePermissionsOnStartup)
        {
            if (!FileSystemHelper.TryValidateWriteAccess(_rootPath, out var error))
            {
                // If fallback is provided, update the root path
                if (!string.IsNullOrWhiteSpace(_options.FallbackPath))
                {
                    _rootPath = Path.Combine(_options.FallbackPath, "models");

                    Directory.CreateDirectory(_rootPath);
                }
                else
                {
                    throw error!;
                }
            }
        }

        // Still ensure the directory exists if required
        else if (_options.EnsureDirectoryExists)
            Directory.CreateDirectory(_rootPath);

        var indexFilePath = Path.Combine(_rootPath, "_index.json");

        _index = indexSelector(indexFilePath);
    }

    private async Task EnsureIndexLoadedAsync<TSummary>(Action getAll, Func<Task<IReadOnlyList<TSummary>>> getAllEntries, Func<string, Task> rebuild, CancellationToken cancellationToken)
    {
        if (!_indexLoaded)
        {
            await _buildLock.WaitAsync(cancellationToken);
            try
            {
                if (_indexLoaded) return;


                await _index.LoadAsync(cancellationToken);

                var entries = await getAllEntries(); //  await _index.GetAllAsync(cancellationToken);
                if (!entries.Any())
                {
                    await _index.RebuildAsync(_rootPath, async (rootPath, id, ct) =>
                    {
                        var manifest = await FileSystemHelper
                            .LoadManifestOnlyAsync<SessionManifest>(rootPath, id, "meta.json", ct);

                        if (manifest == null) return null;

                        return new SessionSummary
                        {
                            SessionId = id,
                            ModelFingerprint = manifest.ModelFingerprint,
                            LastUpdated = manifest.LastUpdated,
                            Tags = manifest.Tags ?? new Dictionary<string, string>()
                        };
                    }, cancellationToken);

                    rebuild(_rootPath);
                }
                _indexLoaded = true;
            }
            finally
            {
                _buildLock.Release();
            }
        }
    }
}
