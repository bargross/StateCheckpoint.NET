using System.Text.Json;

namespace Checkpoint.NET.Stores;

internal static class FileSystemHelper
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public static async Task SaveAsync<TMetadata>(
        string rootPath,
        Guid id,
        byte[] binaryData,
        TMetadata metadata,
        FileSystemStoreOptions options,
        string binaryFileName = "data.bin",
        string metaFileName = "meta.json",
        CancellationToken ct = default) where TMetadata : class
    {
        var dir = Path.Combine(rootPath, id.ToString());

        if (options.EnsureDirectoryExists)
        {
            Directory.CreateDirectory(dir);
        }
        else if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException(
                $"The target directory '{dir}' does not exist and " +
                $"{nameof(options.EnsureDirectoryExists)} is set to false. " +
                "Please create the directory manually or enable EnsureDirectoryExists.");
        }

        // Validate write access (optional runtime check)
        if (!TryValidateWriteAccess(dir, out var error))
        {
            if (!string.IsNullOrEmpty(options.FallbackPath))
            {
                var fallbackDir = Path.Combine(options.FallbackPath, id.ToString());

                Directory.CreateDirectory(fallbackDir);

                // Recursively call with the fallback path
                await SaveAsync(
                    options.FallbackPath,
                    id,
                    binaryData,
                    metadata,
                    options,
                    binaryFileName,
                    metaFileName,
                    ct);

                return;
            }

            throw error!;
        }

        await File.WriteAllBytesAsync(Path.Combine(dir, binaryFileName), binaryData, ct);

        var json = JsonSerializer.Serialize(metadata, _jsonOpts);

        await File.WriteAllTextAsync(Path.Combine(dir, metaFileName), json, ct);
    }

    public static async Task<(byte[] Binary, TMetadata Metadata)> LoadAsync<TMetadata>(
        string rootPath,
        Guid id,
        string binaryFileName = "data.bin",
        string metaFileName = "meta.json",
        CancellationToken ct = default) where TMetadata : class, new()
    {
        var dir = Path.Combine(rootPath, id.ToString());
        var binaryPath = Path.Combine(dir, binaryFileName);
        var metaPath = Path.Combine(dir, metaFileName);

        if (!File.Exists(metaPath) || !File.Exists(binaryPath))
            throw new FileNotFoundException($"Checkpoint {id} not found in {rootPath}");

        var binary = await File.ReadAllBytesAsync(binaryPath, ct);
        var json = await File.ReadAllTextAsync(metaPath, ct);
        var metadata = JsonSerializer.Deserialize<TMetadata>(json)!;

        return (binary, metadata);
    }

    public static Task DeleteAsync(string rootPath, Guid id, CancellationToken ct = default)
    {
        var dir = Path.Combine(rootPath, id.ToString());
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
        return Task.CompletedTask;
    }

    public static Task<List<Guid>> ListAsync(string rootPath, CancellationToken ct = default)
    {
        var dirs = Directory.GetDirectories(rootPath);
        var guids = new List<Guid>();
        foreach (var dir in dirs)
        {
            if (Guid.TryParse(Path.GetFileName(dir), out var id))
                guids.Add(id);
        }

        return Task.FromResult(guids);
    }

    public static bool TryValidateWriteAccess(string path, out Exception? error)
    {
        error = null;

        try
        {
            // Ensure the directory exists (if we are allowed to create it)
            Directory.CreateDirectory(path);

            // Test write access by creating and deleting a temporary file
            var testFile = Path.Combine(path, $".checkpoint_net_test_{Guid.NewGuid()}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = new UnauthorizedAccessException(
                $"The process does not have write permission to '{path}'. " +
                $"Please run the application with elevated privileges or choose a different path.",
                ex);
            return false;
        }
        catch (IOException ex) when (ex.Message.Contains("disk") || ex.Message.Contains("space"))
        {
            error = new IOException(
                $"The storage location '{path}' is unavailable or out of space. Please check your disk.",
                ex);
            return false;
        }
        catch (Exception ex)
        {
            error = new IOException(
                $"The directory '{path}' could not be accessed. {ex.Message}",
                ex);
            return false;
        }
    }
}