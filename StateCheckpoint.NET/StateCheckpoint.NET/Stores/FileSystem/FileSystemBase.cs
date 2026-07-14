using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

internal abstract class FileSystemBase<TIndex>
{
    protected readonly string _rootPath;
    protected readonly FileSystemStoreOptions _options;
    protected readonly TIndex _index;
    protected bool _indexLoaded;
    protected readonly SemaphoreSlim _buildLock = new(1, 1);

    public FileSystemBase(string rootPath, FileSystemStoreOptions? options, Func<string, TIndex> indexSelector)
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

    protected async Task EnsureIndexLoadedAsync<TSummary>(Func<Task> load, Func<Task<IReadOnlyList<TSummary>>> getAllEntries, Func<string, Task> rebuild, CancellationToken cancellationToken)
    {
        if (!_indexLoaded)
        {
            await _buildLock.WaitAsync(cancellationToken);
            try
            {
                if (_indexLoaded) return;


                //await _index.LoadAsync(cancellationToken);
                await load();

                var entries = await getAllEntries(); //  await _index.GetAllAsync(cancellationToken);
                if (!entries.Any())
                {
                    await rebuild(_rootPath);
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
