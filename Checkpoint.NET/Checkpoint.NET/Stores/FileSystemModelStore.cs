using Checkpoint.NET.Models;

namespace Checkpoint.NET.Stores;

public class FileSystemModelStore : IModelStore
{
    private readonly string _rootPath;
    private readonly FileSystemStoreOptions _options;

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
    }

    public async Task SaveAsync(ModelCheckpoint checkpoint, CancellationToken ct = default)
    {
        var manifest = new ModelManifest
        {
            HyperParams = checkpoint.HyperParams,
            Tokenizer = checkpoint.Tokenizer,
            CurrentEpoch = checkpoint.CurrentEpoch,
            LastTrainingLoss = checkpoint.LastTrainingLoss,
            CreatedAt = checkpoint.CreatedAt,
            Tags = checkpoint.Tags
        };

        await FileSystemHelper.SaveAsync(
            _rootPath,
            checkpoint.ModelId,
            checkpoint.WeightsBytes,
            manifest,
            _options,
            binaryFileName: "weights.bin",
            metaFileName: "manifest.json",
            ct: ct);
    }

    public async Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken ct = default)
    {
        try
        {
            var (weights, manifest) = await FileSystemHelper.LoadAsync<ModelManifest>(
                _rootPath, modelId, "weights.bin", "manifest.json", ct);

            return new ModelCheckpoint
            {
                ModelId = modelId,
                WeightsBytes = weights,
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

    public Task DeleteAsync(Guid modelId, CancellationToken ct = default)
        => FileSystemHelper.DeleteAsync(_rootPath, modelId, ct);

    public Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken ct = default)
    {
        // Tag filtering is ignored for Phase 1 FileSystem.
        // The manager can filter in-memory if needed.
        return FileSystemHelper.ListAsync(_rootPath, ct);
    }
}