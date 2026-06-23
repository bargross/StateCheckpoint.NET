using Checkpoint.NET.Models;
using Checkpoint.NET.Stores;

namespace Checkpoint.NET.Manager;

public class SessionManager
{
    private readonly ISessionStore _store;

    public SessionManager(ISessionStore store) => _store = store;

    // Save a session state
    public async Task<Guid> SaveAsync(
        Guid sessionId,
        byte[] kvCacheBytes,
        int[] tokenHistory,
        string modelFingerprint,
        SamplingData? samplingConfig = null,
        Dictionary<string, string>? tags = null,
        CancellationToken ct = default)
    {
        var session = new SessionCheckpoint
        {
            SessionId = sessionId,
            KvCacheBytes = kvCacheBytes,
            TokenHistory = tokenHistory,
            ModelFingerprint = modelFingerprint,
            SamplingConfig = samplingConfig ?? new SamplingData(),
            LastUpdated = DateTime.UtcNow,
            Tags = tags ?? new Dictionary<string, string>()
        };

        await _store.SaveAsync(session, ct);

        return session.SessionId;
    }

    // Load a session state
    public async Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken ct = default)
        => await _store.LoadAsync(sessionId, ct);

    // Delete a session
    public async Task DeleteAsync(Guid sessionId, CancellationToken ct = default)
        => await _store.DeleteAsync(sessionId, ct);

    // List all session IDs (optionally filter by tag)
    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken ct = default)
        => await _store.ListAsync(tagKey, tagValue, ct);
}