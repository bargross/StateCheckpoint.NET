using StateCheckpoint.NET.Models;

namespace StateCheckpoint.NET;

internal sealed class SessionIndex
{
    private readonly IndexStorage<SessionSummary> _storage;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<SessionSummary> _items;

    public SessionIndex(string indexFilePath)
        : this(new IndexStorage<SessionSummary>(indexFilePath))
    {
    }

    internal SessionIndex(IndexStorage<SessionSummary> storage)
    {
        _storage = storage;
        _items = new List<SessionSummary>();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await _storage.ReadAsync(cancellationToken);
            _items = data.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddOrUpdateAsync(SessionSummary summary, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var existing = _items.FirstOrDefault(e => e.SessionId == summary.SessionId);
            if (existing != null)
            {
                existing.ModelFingerprint = summary.ModelFingerprint;
                existing.LastUpdated = summary.LastUpdated;
                existing.Tags = summary.Tags;
            }
            else
            {
                _items.Add(summary);
            }

            await _storage.WriteAsync(_items, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var removed = _items.RemoveAll(e => e.SessionId == sessionId);
            if (removed > 0)
                await _storage.WriteAsync(_items, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RebuildAsync(
        string rootPath,
        Func<string, Guid, CancellationToken, Task<SessionSummary?>> loadManifestFn,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var allIds = await FileSystemHelper.ListAsync(rootPath, cancellationToken);

            var newItems = new List<SessionSummary>();
            foreach (var id in allIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var summary = await loadManifestFn(rootPath, id, cancellationToken);

                if (summary != null)
                    newItems.Add(summary);
            }

            _items = newItems;

            await _storage.WriteAsync(_items, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            return _items.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<SessionSummary>> QueryAsync(SessionQuery query, CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        var filtered = all.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query.ModelFingerprint))
            filtered = filtered.Where(s => s.ModelFingerprint == query.ModelFingerprint);

        if (query.UpdatedAfter.HasValue)
            filtered = filtered.Where(s => s.LastUpdated >= query.UpdatedAfter.Value);

        if (query.UpdatedBefore.HasValue)
            filtered = filtered.Where(s => s.LastUpdated <= query.UpdatedBefore.Value);

        if (query.Tags != null && query.Tags.Count > 0)
            filtered = filtered.Where(s => TagsMatch(s.Tags, query.Tags));

        // Sort (only LastUpdated for sessions)
        var sorted = query.Descending
            ? filtered.OrderByDescending(s => s.LastUpdated)
            : filtered.OrderBy(s => s.LastUpdated);

        if (query.Limit.HasValue)
            sorted = query.Descending ? sorted.Take(query.Limit.Value).OrderByDescending(s => s.LastUpdated) 
                : sorted.Take(query.Limit.Value).OrderBy(s => s.LastUpdated);

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
