using Npgsql;
using StateCheckpoint.NET.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StateCheckpoint.NET;

internal class PostgresSessionStore : PostgresStoreBase, IDbSessionStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public PostgresSessionStore(string connectionString) : base(connectionString) { }

    public PostgresSessionStore(NpgsqlDataSource dataSource) : base(dataSource) { }

    /// <summary>
    /// Ensures schema is created
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(PostgresSessionQueries.EnsureSessionSchema, connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }


    /// <summary>
    /// Saves a new session
    /// </summary>
    /// <param name="session"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>Session id created for the given session</returns>
    public async Task SaveAsync(SessionCheckpoint session, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(PostgresSessionQueries.UpsertInferenceSession, connection);

        command.Parameters.AddWithValue("@id", session.SessionId);
        command.Parameters.AddWithValue("@fp", session.ModelFingerprint);
        command.Parameters.AddWithValue("@history", session.TokenHistory);
        command.Parameters.AddWithValue("@config", JsonSerializer.Serialize(session.SamplingConfig, _jsonOpts));
        command.Parameters.AddWithValue("@kv", session.KvCacheBytes);
        command.Parameters.AddWithValue("@now", session.LastUpdated);
        command.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(session.Tags, _jsonOpts));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Loads a specific session checkpoint by session Id
    /// </summary>
    /// <param name="sessionId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new NpgsqlCommand(PostgresSessionQueries.SelectInferenceSession, connection);
        command.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

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

    /// <summary>
    /// Deletes a saved session by session Id
    /// </summary>
    /// <param name="sessionId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await DeleteInternalAsync(connection, transaction, sessionId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);

            throw;
        }
    }

    public async Task DeleteManyAsync(IEnumerable<Guid> sessionIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var id in sessionIds)
                await DeleteInternalAsync(connection, transaction, id, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Gets a list of all session ids
    /// </summary>
    /// <param name="tagKey"></param>
    /// <param name="tagValue"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);
        string sqlQuery;
        NpgsqlCommand command;

        if (string.IsNullOrWhiteSpace(tagKey) || string.IsNullOrWhiteSpace(tagValue))
        {
            sqlQuery = PostgresSessionQueries.ListAllSessionIds;
            command = new NpgsqlCommand(sqlQuery, connection);
        }
        else
        {
            // Build the JSON object in C# and pass it as a JSONB parameter
            var jsonObject = $"{{ \"{tagKey}\": \"{tagValue}\" }}";
            sqlQuery = "SELECT sessionId FROM inferenceSessions WHERE tags @> @tag::jsonb";

            command = new NpgsqlCommand(sqlQuery, connection);
            command.Parameters.AddWithValue("@tag", jsonObject);
        }

        await using var dataReader = await command.ExecuteReaderAsync(cancellationToken);

        var sessionIds = new List<Guid>();
        while (await dataReader.ReadAsync(cancellationToken))
            sessionIds.Add(dataReader.GetGuid(0));

        return sessionIds;
    }

    public async Task<List<SessionSummary>> QueryAsync(SessionQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);
        var (sql, parameters) = BuildQuerySql(query, includeOrderBy: true, includeLimit: true);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SessionSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SessionSummary
            {
                SessionId = reader.GetGuid(0),
                ModelFingerprint = reader.GetString(1),
                LastUpdated = reader.GetDateTime(2),
                Tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3)) ?? new()
            });
        }
        return results;
    }

    // New streaming method
    public async IAsyncEnumerable<SessionSummary> QueryStreamAsync(
        SessionQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await GetConnectionAsync(ct);
        var (sql, parameters) = BuildQuerySql(query, includeOrderBy: true, includeLimit: true);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return new SessionSummary
            {
                SessionId = reader.GetGuid(0),
                ModelFingerprint = reader.GetString(1),
                LastUpdated = reader.GetDateTime(2),
                Tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3)) ?? new()
            };
        }
    }

    public async Task<SessionSummary?> GetSessionSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);

        const string sql = @"
        SELECT model_fingerprint, last_updated, tags
        FROM inference_sessions
        WHERE session_id = @id";
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("@id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new SessionSummary
        {
            SessionId = sessionId,
            ModelFingerprint = reader.GetString(0),
            LastUpdated = reader.GetDateTime(1),
            Tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2)) ?? new()
        };
    }

    //---------------- Private Methods ---------------//

    private async Task DeleteInternalAsync(
       NpgsqlConnection connection,
       NpgsqlTransaction transaction,
       Guid modelId,
       CancellationToken cancellationToken)
    {
        // Delete from ModelBlobs (cascades or manual)
        await using var deleteBlobs = new NpgsqlCommand("DELETE FROM ModelBlobs WHERE sessionId = @id", connection, transaction);
        deleteBlobs.Parameters.AddWithValue("@id", modelId);

        await deleteBlobs.ExecuteNonQueryAsync(cancellationToken);

        // Delete from ModelManifests
        await using var deleteManifest = new NpgsqlCommand("DELETE FROM ModelManifests WHERE sessionId = @id", connection, transaction);
        deleteManifest.Parameters.AddWithValue("@id", modelId);

        await deleteManifest.ExecuteNonQueryAsync(cancellationToken);
    }

    private (string Sql, List<NpgsqlParameter> Parameters) BuildQuerySql(SessionQuery query, bool includeOrderBy = true, bool includeLimit = true)
    {
        var sql = new StringBuilder(@"
            SELECT session_id, model_fingerprint, last_updated, tags
            FROM inference_sessions
            WHERE 1=1
        ");

        var parameters = new List<NpgsqlParameter>();
        int paramIndex = 0;

        if (!string.IsNullOrEmpty(query.ModelFingerprint))
        {
            sql.Append($" AND model_fingerprint = @p{paramIndex}");
            parameters.Add(new NpgsqlParameter($"@p{paramIndex}", query.ModelFingerprint));
            paramIndex++;
        }
        if (query.UpdatedAfter.HasValue)
        {
            sql.Append($" AND last_updated >= @p{paramIndex}");
            parameters.Add(new NpgsqlParameter($"@p{paramIndex}", query.UpdatedAfter.Value));
            paramIndex++;
        }
        if (query.UpdatedBefore.HasValue)
        {
            sql.Append($" AND last_updated <= @p{paramIndex}");
            parameters.Add(new NpgsqlParameter($"@p{paramIndex}", query.UpdatedBefore.Value));
            paramIndex++;
        }
        if (query.Tags != null && query.Tags.Count > 0)
        {
            var json = JsonSerializer.Serialize(query.Tags);
            sql.Append($" AND tags @> @p{paramIndex}::jsonb");
            parameters.Add(new NpgsqlParameter($"@p{paramIndex}", json));
            paramIndex++;
        }

        if (includeOrderBy)
        {
            var orderColumn = query.OrderBy switch
            {
                SessionSortField.LastUpdated => "last_updated",
                _ => "last_updated"
            };
            sql.Append($" ORDER BY {orderColumn} {(query.Descending ? "DESC" : "ASC")}");
        }

        if (includeLimit && query.Limit.HasValue && query.Limit.Value > 0)
        {
            sql.Append($" LIMIT @p{paramIndex}");
            parameters.Add(new NpgsqlParameter($"@p{paramIndex}", query.Limit.Value));
        }

        return (sql.ToString(), parameters);
    }
}