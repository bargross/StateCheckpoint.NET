using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;
using StateCheckpoint.NET.Stores;
using System.Runtime.CompilerServices;

namespace StateCheckpoint.NET;

internal class FileSystemModelStore : IFileSystemModelStore
{
    private readonly string _rootPath;
    private readonly FileSystemStoreOptions _options;
    private readonly CheckpointIndex _index;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private bool _indexLoaded;

    public FileSystemModelStore(string rootPath, FileSystemStoreOptions? options = null)
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

        _index = new CheckpointIndex(indexFilePath);
    }

    /// <summary>
    /// saves model checkpoint for model training session
    /// </summary>
    /// <param name="checkpoint"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task SaveAsync(ModelCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var binaryData = new Dictionary<string, byte[]>
        {
            ["weights.bin"] = checkpoint.WeightsBytes,
            ["optimizer.bin"] = checkpoint.OptimizerBytes
        };

        var manifest = new ModelManifest
        {
            HyperParams = checkpoint.HyperParams,
            Tokenizer = checkpoint.Tokenizer,
            CurrentEpoch = checkpoint.CurrentEpoch,
            LastTrainingLoss = checkpoint.LastTrainingLoss,
            CreatedAt = checkpoint.CreatedAt,
            Tags = checkpoint.Tags
        };

        await FileSystemHelper.SaveMultipleAsync(
            _rootPath,
            checkpoint.ModelId,
            binaryData,
            manifest,
            _options,
            cancellationToken: cancellationToken);

        await _index.AddOrUpdateAsync(new CheckpointSummary
        {
            CreatedAt  = checkpoint.CreatedAt,
            CurrentEpoch = checkpoint.CurrentEpoch,
            LastTrainingLoss = checkpoint.LastTrainingLoss,
            ModelId = checkpoint.ModelId,
            Tags = checkpoint.Tags
        }, cancellationToken);
    }

    /// <summary>
    /// loads a previously saved model
    /// </summary>
    /// <param name="modelId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        try
        {
            var (binaries, manifest) = await FileSystemHelper.LoadMultipleAsync<ModelManifest>(
                _rootPath,
                modelId,
                new[] { "weights.bin", "optimizer.bin" },
                "manifest.json",
                cancellationToken);

            return new ModelCheckpoint
            {
                ModelId = modelId,
                WeightsBytes = binaries["weights.bin"],
                OptimizerBytes = binaries["optimizer.bin"],
                HyperParams = manifest.HyperParams,
                Tokenizer = manifest.Tokenizer,
                CurrentEpoch = manifest.CurrentEpoch,
                LastTrainingLoss = manifest.LastTrainingLoss,
                CreatedAt = manifest.CreatedAt,
                Tags = manifest.Tags
            };
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        await FileSystemHelper.DeleteAsync(_rootPath, modelId, cancellationToken);

        await _index.RemoveAsync(modelId, cancellationToken);
    }

    public async Task DeleteManyAsync(IEnumerable<Guid> modelIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in modelIds)
            await DeleteAsync(id, cancellationToken);
    }

    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tagKey) || string.IsNullOrWhiteSpace(tagValue))
            return await FileSystemHelper.ListAsync(_rootPath, cancellationToken);

        await EnsureIndexLoadedAsync(cancellationToken);

        return (await _index.GetAllAsync(cancellationToken))
            .Where(s => s.Tags != null && s.Tags.TryGetValue(tagKey, out var val) && val == tagValue)
            .Select(s => s.ModelId)
            .ToList();
    }

    public async Task<List<CheckpointSummary>> QueryAsync(CheckpointQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureIndexLoadedAsync(cancellationToken);

        return await _index.QueryAsync(query, cancellationToken);
    }

    public async IAsyncEnumerable<CheckpointSummary> QueryStreamAsync(
        CheckpointQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureIndexLoadedAsync(cancellationToken);

        var all = await _index.GetAllAsync(cancellationToken);

        // Filter in memory (no sorting/limiting – streaming preserves order of storage)
        foreach (var summary in all)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (query.MinEpoch.HasValue && summary.CurrentEpoch < query.MinEpoch) continue;
            if (query.MaxEpoch.HasValue && summary.CurrentEpoch > query.MaxEpoch) continue;
            if (query.MinLoss.HasValue && summary.LastTrainingLoss < query.MinLoss) continue;
            if (query.MaxLoss.HasValue && summary.LastTrainingLoss > query.MaxLoss) continue;
            if (query.CreatedAfter.HasValue && summary.CreatedAt < query.CreatedAfter) continue;
            if (query.CreatedBefore.HasValue && summary.CreatedAt > query.CreatedBefore) continue;
            if (query.Tags != null && query.Tags.Count > 0 && !TagsHelper.TagsMatch(summary.Tags, query.Tags)) continue;

            yield return summary;
        }
    }

    private async Task EnsureIndexLoadedAsync(CancellationToken cancellationToken)
    {
        if (!_indexLoaded)
        {
            await _buildLock.WaitAsync(cancellationToken);
            try
            {
                if (_indexLoaded) return; // double-check

                // Load the index from disk
                await _index.LoadAsync(cancellationToken);

                var entries = await _index.GetAllAsync(cancellationToken);

                // If index is empty, rebuild it from manifests
                if (!entries.Any())
                {
                    await _index.RebuildAsync(_rootPath, async (string rootPath, Guid id, CancellationToken ct) =>
                    {
                        var manifest = await FileSystemHelper.LoadManifestOnlyAsync<ModelManifest>(rootPath, id, "manifest.json", ct);

                        if (manifest == null) return null;

                        return new CheckpointSummary
                        {
                            ModelId = id,
                            CurrentEpoch = manifest.CurrentEpoch,
                            LastTrainingLoss = manifest.LastTrainingLoss,
                            CreatedAt = manifest.CreatedAt,
                            Tags = manifest.Tags ?? new()
                        };
                    }, cancellationToken);
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