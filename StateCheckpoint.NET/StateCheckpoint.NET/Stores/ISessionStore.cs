using StateCheckpoint.NET.Models;

namespace StateCheckpoint.NET;

internal interface ISessionStore
{
    Task SaveAsync(SessionCheckpoint session, CancellationToken cancellationToken = default);
    Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DeleteManyAsync(IEnumerable<Guid> modelIds, CancellationToken cancellationToken = default);
    Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default);
    Task<List<SessionSummary>> QueryAsync(SessionQuery query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SessionSummary> QueryStreamAsync(SessionQuery query, CancellationToken cancellationToken = default);
    Task<SessionSummary?> GetSessionSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
