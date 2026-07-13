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
                if (!string.IsNullOrEmpty(_options.FallbackPath))
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
        else
        {
            // Still ensure the directory exists if required
            if (_options.EnsureDirectoryExists)
            {
                Directory.CreateDirectory(_rootPath);
            }
        }

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
        var allIds = await FileSystemHelper.ListAsync(_rootPath, cancellationToken);
        var summaries = new List<CheckpointSummary>();

        foreach (var id in allIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifest = await FileSystemHelper.LoadManifestOnlyAsync<ModelManifest>(_rootPath, id, "manifest.json", cancellationToken);
            if (manifest == null) continue;

            // Apply filters
            if (query.MinEpoch.HasValue && manifest.CurrentEpoch < query.MinEpoch) continue;
            if (query.MaxEpoch.HasValue && manifest.CurrentEpoch > query.MaxEpoch) continue;
            if (query.MinLoss.HasValue && manifest.LastTrainingLoss < query.MinLoss) continue;
            if (query.MaxLoss.HasValue && manifest.LastTrainingLoss > query.MaxLoss) continue;
            if (query.CreatedAfter.HasValue && manifest.CreatedAt < query.CreatedAfter) continue;
            if (query.CreatedBefore.HasValue && manifest.CreatedAt > query.CreatedBefore) continue;
            if (query.Tags != null && !TagsMatch(manifest.Tags, query.Tags)) continue;

            summaries.Add(new CheckpointSummary
            {
                ModelId = id,
                CurrentEpoch = manifest.CurrentEpoch,
                LastTrainingLoss = manifest.LastTrainingLoss,
                CreatedAt = manifest.CreatedAt,
                Tags = manifest.Tags ?? new()
            });
        }

        // Sorting
        summaries = query.OrderBy switch
        {
            CheckpointSortField.CreatedAt => query.Descending
                ? summaries.OrderByDescending(s => s.CreatedAt).ToList()
                : summaries.OrderBy(s => s.CreatedAt).ToList(),
            CheckpointSortField.Epoch => query.Descending
                ? summaries.OrderByDescending(s => s.CurrentEpoch).ToList()
                : summaries.OrderBy(s => s.CurrentEpoch).ToList(),
            CheckpointSortField.Loss => query.Descending
                ? summaries.OrderByDescending(s => s.LastTrainingLoss).ToList()
                : summaries.OrderBy(s => s.LastTrainingLoss).ToList(),
            _ => summaries
        };

        if (query.Limit.HasValue && query.Limit.Value > 0)
            summaries = summaries.Take(query.Limit.Value).ToList();

        return summaries;
    }

    public async IAsyncEnumerable<CheckpointSummary> QueryStreamAsync(
        CheckpointQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureIndexLoadedAsync(ct);
        var all = await _index.GetAllAsync(ct);

        // Filter in memory (no sorting/limiting – streaming preserves order of storage)
        foreach (var summary in all)
        {
            ct.ThrowIfCancellationRequested();

            if (query.MinEpoch.HasValue && summary.CurrentEpoch < query.MinEpoch) continue;
            if (query.MaxEpoch.HasValue && summary.CurrentEpoch > query.MaxEpoch) continue;
            if (query.MinLoss.HasValue && summary.LastTrainingLoss < query.MinLoss) continue;
            if (query.MaxLoss.HasValue && summary.LastTrainingLoss > query.MaxLoss) continue;
            if (query.CreatedAfter.HasValue && summary.CreatedAt < query.CreatedAfter) continue;
            if (query.CreatedBefore.HasValue && summary.CreatedAt > query.CreatedBefore) continue;
            if (query.Tags != null && query.Tags.Count > 0 && !TagsMatch(summary.Tags, query.Tags)) continue;

            yield return summary;
        }
    }

    private static bool TagsMatch(Dictionary<string, string>? actual, Dictionary<string, string>? filter)
    {
        if (filter == null) 
            return true;

        if (actual == null) 
            return false;

        foreach (var pair in filter)
            if (!actual.TryGetValue(pair.Key, out var val) || val != pair.Value)
                return false;

        return true;
    }

    private async Task EnsureIndexLoadedAsync(CancellationToken ct)
    {
        // We can check if _items is empty or use a flag; simplest: always load if not loaded.
        // We'll have a separate field to track loaded state.
        // Here we just call LoadAsync if needed. Since it's cheap, we can call it every time.
        // But better to check a flag.
        if (!_indexLoaded)
        {
            await _index.LoadAsync(ct);
            _indexLoaded = true;
        }
    }

    private async Task RebuildIndexAsync(CancellationToken ct)
    {
        await _buildLock.WaitAsync(ct);
        try
        {
            // double-check after lock
            await EnsureIndexLoadedAsync(ct);
            var existing = await _index.GetAllAsync(ct);
            if (existing.Any()) return;

            async Task<CheckpointSummary?> LoadManifestSummaryAsync(string rootPath, Guid id, CancellationToken ct)
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
            }

            // Rebuild
            await _index.RebuildAsync(_rootPath, LoadManifestSummaryAsync, ct);
        }
        finally
        {
            _buildLock.Release();
        }
    }
}