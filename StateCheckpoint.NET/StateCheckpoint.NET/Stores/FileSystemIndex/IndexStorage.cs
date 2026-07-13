using System.Text.Json;

namespace StateCheckpoint.NET;


internal sealed class IndexStorage<T>
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOpts;

    public IndexStorage(string filePath, JsonSerializerOptions? jsonOpts = null)
    {
        _filePath = filePath;
        _jsonOpts = jsonOpts ?? new JsonSerializerOptions { WriteIndented = true };
    }

    public async Task<IReadOnlyList<T>> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return Array.Empty<T>();

        var json = await File.ReadAllTextAsync(_filePath, ct);

        return JsonSerializer.Deserialize<List<T>>(json, _jsonOpts) ?? new List<T>();
    }

    public async Task WriteAsync(IEnumerable<T> data, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(data, _jsonOpts);
        var tempPath = _filePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json, ct);

        File.Move(tempPath, _filePath, overwrite: true);
    }
}
