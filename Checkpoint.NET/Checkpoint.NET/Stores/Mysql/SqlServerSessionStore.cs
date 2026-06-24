using Checkpoint.NET.Models;
using Checkpoint.NET.Queries;
using Checkpoint.NET.Stores.Mysql;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Checkpoint.NET.Stores;

public class SqlServerSessionStore : SqlServerStoreBase, ISessionStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public SqlServerSessionStore(string connectionString) : base(connectionString) { }
    public SqlServerSessionStore(SqlConnection connection) : base(connection) { }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        await using var cmd = new SqlCommand(SqlServerSessionQueries.EnsureSessionSchema, conn);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(SessionCheckpoint session, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var sqlTx = (SqlTransaction)tx;

        await using var cmd = new SqlCommand(SqlServerSessionQueries.UpsertInferenceSession, conn, sqlTx);

        cmd.Parameters.AddWithValue("@Id", session.SessionId);
        cmd.Parameters.AddWithValue("@ModelFingerprint", session.ModelFingerprint);
        cmd.Parameters.AddWithValue("@TokenHistory", JsonSerializer.Serialize(session.TokenHistory, _jsonOpts));
        cmd.Parameters.AddWithValue("@SamplingConfig", JsonSerializer.Serialize(session.SamplingConfig, _jsonOpts));
        cmd.Parameters.AddWithValue("@KvCacheData", session.KvCacheBytes);
        cmd.Parameters.AddWithValue("@LastUpdated", session.LastUpdated);
        cmd.Parameters.AddWithValue("@Tags", JsonSerializer.Serialize(session.Tags, _jsonOpts));

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        await using var cmd = new SqlCommand(SqlServerSessionQueries.SelectInferenceSession, conn);

        cmd.Parameters.AddWithValue("@Id", sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new SessionCheckpoint
        {
            SessionId = sessionId,
            ModelFingerprint = reader.GetString(0),
            TokenHistory = JsonSerializer.Deserialize<int[]>(reader.GetString(1))!,
            SamplingConfig = JsonSerializer.Deserialize<SamplingData>(reader.GetString(2))!,
            KvCacheBytes = reader.GetFieldValue<byte[]>(3),
            LastUpdated = reader.GetDateTime(4),
            Tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5))!
        };
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        await using var cmd = new SqlCommand(SqlServerSessionQueries.DeleteInferenceSession, conn);

        cmd.Parameters.AddWithValue("@Id", sessionId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        string sql;
        SqlCommand cmd;

        if (string.IsNullOrWhiteSpace(tagKey) || string.IsNullOrWhiteSpace(tagValue))
        {
            sql = SqlServerSessionQueries.ListAllSessionIds;
            cmd = new SqlCommand(sql, conn);
        }
        else
        {
            sql = SqlServerSessionQueries.ListSessionIdsByTag;
            cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@TagPattern", $"%\"{tagKey}\":\"{tagValue}\"%");
        }

        await using (cmd)
        {
            var list = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
                list.Add(reader.GetGuid(0));

            return list;
        }
    }
}