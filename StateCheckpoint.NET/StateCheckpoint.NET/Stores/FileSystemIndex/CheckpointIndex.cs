using StateCheckpoint.NET.Models;

namespace StateCheckpoint.NET;

internal sealed class CheckpointIndex
{
    private readonly IndexStorage<CheckpointSummary> _storage;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<CheckpointSummary> _items;

    public CheckpointIndex(string indexFilePath)
        : this(new IndexStorage<CheckpointSummary>(indexFilePath))
    {
    }

    internal CheckpointIndex(IndexStorage<CheckpointSummary> storage) // for testing
    {
        _storage = storage;
        _items = new List<CheckpointSummary>();
    }

    /// <summary>
    /// Loads the index from storage; if no file exists, starts with an empty list.
    /// Must be called before any other operations.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var data = await _storage.ReadAsync(ct);
            _items = data.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddOrUpdateAsync(CheckpointSummary summary, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var existing = _items.FirstOrDefault(e => e.ModelId == summary.ModelId);
            if (existing != null)
            {
                existing.CurrentEpoch = summary.CurrentEpoch;
                existing.LastTrainingLoss = summary.LastTrainingLoss;
                existing.CreatedAt = summary.CreatedAt;
                existing.Tags = summary.Tags;
            }
            else
            {
                _items.Add(summary);
            }
            await _storage.WriteAsync(_items, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(Guid modelId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var removed = _items.RemoveAll(e => e.ModelId == modelId);
            if (removed > 0)
                await _storage.WriteAsync(_items, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Rebuilds the index by enumerating all manifest files.
    /// Acquires the lock and overwrites the file.
    /// </summary>
    public async Task RebuildAsync(
        string rootPath,
        Func<string, Guid, CancellationToken, Task<CheckpointSummary?>> loadManifestFn,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var allIds = await FileSystemHelper.ListAsync(rootPath, ct);
            var newItems = new List<CheckpointSummary>();
            foreach (var id in allIds)
            {
                ct.ThrowIfCancellationRequested();
                var summary = await loadManifestFn(rootPath, id, ct);
                if (summary != null)
                    newItems.Add(summary);
            }
            _items = newItems;
            await _storage.WriteAsync(_items, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns a copy of all items (for querying).
    /// </summary>
    public async Task<IReadOnlyList<CheckpointSummary>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _items.ToList(); // defensive copy
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Queries the index with a predicate and optional sorting/limit.
    /// </summary>
    public async Task<List<CheckpointSummary>> QueryAsync(
        CheckpointQuery query,
        CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        var filtered = all.AsEnumerable();

        // Apply filters (same as before)
        if (query.MinEpoch.HasValue) filtered = filtered.Where(s => s.CurrentEpoch >= query.MinEpoch.Value);
        if (query.MaxEpoch.HasValue) filtered = filtered.Where(s => s.CurrentEpoch <= query.MaxEpoch.Value);
        if (query.MinLoss.HasValue) filtered = filtered.Where(s => s.LastTrainingLoss >= query.MinLoss.Value);
        if (query.MaxLoss.HasValue) filtered = filtered.Where(s => s.LastTrainingLoss <= query.MaxLoss.Value);
        if (query.CreatedAfter.HasValue) filtered = filtered.Where(s => s.CreatedAt >= query.CreatedAfter.Value);
        if (query.CreatedBefore.HasValue) filtered = filtered.Where(s => s.CreatedAt <= query.CreatedBefore.Value);
        if (query.Tags != null && query.Tags.Count > 0)
            filtered = filtered.Where(s => TagsMatch(s.Tags, query.Tags));

        // Sort
        var sorted = query.OrderBy switch
        {
            CheckpointSortField.CreatedAt => query.Descending
                ? filtered.OrderByDescending(s => s.CreatedAt)
                : filtered.OrderBy(s => s.CreatedAt),
            CheckpointSortField.Epoch => query.Descending
                ? filtered.OrderByDescending(s => s.CurrentEpoch)
                : filtered.OrderBy(s => s.CurrentEpoch),
            CheckpointSortField.Loss => query.Descending
                ? filtered.OrderByDescending(s => s.LastTrainingLoss)
                : filtered.OrderBy(s => s.LastTrainingLoss),
            _ => filtered
        };

        if (query.Limit.HasValue)
            sorted = sorted.Take(query.Limit.Value);

        return sorted.ToList();
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
}
