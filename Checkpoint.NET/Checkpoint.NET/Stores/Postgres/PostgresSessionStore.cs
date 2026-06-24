using System.Text.Json;
using Npgsql;
using Checkpoint.NET.Models;
using Checkpoint.NET.Queries;

namespace Checkpoint.NET.Stores.Postgres;

public class PostgresSessionStore : PostgresStoreBase, ISessionStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    // Option 1: Connection String (Library manages DataSource)
    public PostgresSessionStore(string connectionString) : base(connectionString) { }

    // Option 2: Existing DataSource (Caller manages lifetime)
    public PostgresSessionStore(NpgsqlDataSource dataSource) : base(dataSource) { }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(PostgresSessionQueries.EnsureSessionSchema, conn);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAsync(SessionCheckpoint session, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(PostgresSessionQueries.UpsertInferenceSession, conn);

        cmd.Parameters.AddWithValue("@id", session.SessionId);
        cmd.Parameters.AddWithValue("@fp", session.ModelFingerprint);
        cmd.Parameters.AddWithValue("@history", session.TokenHistory);
        cmd.Parameters.AddWithValue("@config", JsonSerializer.Serialize(session.SamplingConfig, _jsonOpts));
        cmd.Parameters.AddWithValue("@kv", session.KvCacheBytes);
        cmd.Parameters.AddWithValue("@now", session.LastUpdated);
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(session.Tags, _jsonOpts));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(PostgresSessionQueries.SelectInferenceSession, conn);
        cmd.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new SessionCheckpoint
        {
            SessionId = sessionId,
            ModelFingerprint = reader.GetString(0),
            TokenHistory = reader.GetFieldValue<int[]>(1),
            SamplingConfig = JsonSerializer.Deserialize<SamplingData>(reader.GetString(2))!,
            KvCacheBytes = reader.GetFieldValue<byte[]>(3),
            LastUpdated = reader.GetDateTime(4),
            Tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5))!
        };
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(PostgresSessionQueries.DeleteInferenceSession, conn);
        cmd.Parameters.AddWithValue("@id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var sql = string.IsNullOrEmpty(tagKey) || string.IsNullOrEmpty(tagValue)
            ? PostgresSessionQueries.ListAllSessionIds
            : PostgresSessionQueries.ListSessionIdsByTag;

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (!string.IsNullOrEmpty(tagKey) && !string.IsNullOrEmpty(tagValue))
        {
            cmd.Parameters.AddWithValue("@key", tagKey);
            cmd.Parameters.AddWithValue("@value", tagValue);
        }

        var list = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetGuid(0));

        return list;
    }
}