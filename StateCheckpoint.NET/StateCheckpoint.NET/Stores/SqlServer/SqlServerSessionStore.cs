using Microsoft.Data.SqlClient;
using StateCheckpoint.NET.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StateCheckpoint.NET;

internal class SqlServerSessionStore : SqlServerStoreBase, IDbSessionStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public SqlServerSessionStore(string connectionString) : base(connectionString) { }
    public SqlServerSessionStore(SqlConnection connection) : base(connection) { }

    /// <summary>
    /// Ensures schema is created
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new SqlCommand(SqlServerSessionQueries.EnsureSessionSchema, connection);

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
        using var connection = await GetConnectionAsync(cancellationToken);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var sqlTx = (SqlTransaction)tx;

        await using var command = new SqlCommand(SqlServerSessionQueries.UpsertInferenceSession, connection, sqlTx);

        command.Parameters.AddWithValue("@Id", session.SessionId);
        command.Parameters.AddWithValue("@ModelFingerprint", session.ModelFingerprint);
        command.Parameters.AddWithValue("@TokenHistory", JsonSerializer.Serialize(session.TokenHistory, _jsonOpts));
        command.Parameters.AddWithValue("@SamplingConfig", JsonSerializer.Serialize(session.SamplingConfig, _jsonOpts));
        command.Parameters.AddWithValue("@KvCacheData", session.KvCacheBytes);
        command.Parameters.AddWithValue("@LastUpdated", session.LastUpdated);
        command.Parameters.AddWithValue("@Tags", JsonSerializer.Serialize(session.Tags, _jsonOpts));

        await command.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Loads a specific session checkpoint by session Id
    /// </summary>
    /// <param name="sessionId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SessionCheckpoint?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new SqlCommand(SqlServerSessionQueries.SelectInferenceSession, connection);

        command.Parameters.AddWithValue("@Id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

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

    /// <summary>
    /// Deletes a saved session by session Id
    /// </summary>
    /// <param name="sessionId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new SqlCommand(SqlServerSessionQueries.DeleteInferenceSession, connection);

        command.Parameters.AddWithValue("@Id", sessionId);

        await command.ExecuteNonQueryAsync(cancellationToken);
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
        using var connection = await GetConnectionAsync(cancellationToken);

        string sql;
        SqlCommand command;

        if (string.IsNullOrWhiteSpace(tagKey) || string.IsNullOrWhiteSpace(tagValue))
        {
            sql = SqlServerSessionQueries.ListAllSessionIds;
            command = new SqlCommand(sql, connection);
        }
        else
        {
            sql = SqlServerSessionQueries.ListSessionIdsByTag.Replace("{tagKey}", tagKey);

            command = new SqlCommand(sql, connection);

            command.Parameters.AddWithValue("@TagValue", tagValue);
        }

        await using (command)
        {
            var list = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
                list.Add(reader.GetGuid(0));

            return list;
        }
    }

    public async Task<List<SessionSummary>> QueryAsync(SessionQuery query, CancellationToken ct = default)
    {
        await using var connection = await GetConnectionAsync(ct);
        var (sql, parameters) = BuildQuerySql(query, includeOrderBy: true, includeLimit: true);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<SessionSummary>();
        while (await reader.ReadAsync(ct))
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

    public async IAsyncEnumerable<SessionSummary> QueryStreamAsync(
        SessionQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await GetConnectionAsync(ct);
        var (sql, parameters) = BuildQuerySql(query, includeOrderBy: true, includeLimit: true);

        await using var command = new SqlCommand(sql, connection);
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

    //------------ Private Methods --------------//

    private (string Sql, List<SqlParameter> Parameters) BuildQuerySql(SessionQuery query, bool includeOrderBy = true, bool includeLimit = true)
    {
        var sql = new StringBuilder(@"
        SELECT session_id, model_fingerprint, last_updated, tags
        FROM InferenceSessions
        WHERE 1=1
    ");

        var parameters = new List<SqlParameter>();

        if (!string.IsNullOrEmpty(query.ModelFingerprint))
        {
            sql.Append(" AND model_fingerprint = @ModelFingerprint");
            parameters.Add(new SqlParameter("@ModelFingerprint", query.ModelFingerprint));
        }
        if (query.UpdatedAfter.HasValue)
        {
            sql.Append(" AND last_updated >= @UpdatedAfter");
            parameters.Add(new SqlParameter("@UpdatedAfter", query.UpdatedAfter.Value));
        }
        if (query.UpdatedBefore.HasValue)
        {
            sql.Append(" AND last_updated <= @UpdatedBefore");
            parameters.Add(new SqlParameter("@UpdatedBefore", query.UpdatedBefore.Value));
        }
        if (query.Tags != null && query.Tags.Count > 0)
        {
            foreach (var pair in query.Tags)
            {
                var valParam = $"@tagVal_{pair.Key}";
                sql.Append($" AND JSON_VALUE(tags, '$.{pair.Key}') = {valParam}");
                parameters.Add(new SqlParameter(valParam, pair.Value));
            }
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
            sql.Append(" OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY");
            parameters.Add(new SqlParameter("@Limit", query.Limit.Value));
        }

        return (sql.ToString(), parameters);
    }
}