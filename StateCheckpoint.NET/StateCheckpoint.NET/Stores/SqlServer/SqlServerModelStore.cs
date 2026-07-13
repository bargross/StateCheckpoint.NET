using Microsoft.Data.SqlClient;
using StateCheckpoint.NET.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StateCheckpoint.NET;

internal class SqlServerModelStore : SqlServerStoreBase, IDbModelStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public SqlServerModelStore(string connectionString) : base(connectionString) { }
    public SqlServerModelStore(SqlConnection connection) : base(connection) { }

    /// <summary>
    /// Ensures the schema for the model is created
    /// </summary>
    /// <param name="CancellationToken"></param>
    /// <returns></returns>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new SqlCommand(SqlServerTrainingQueries.EnsureModelSchema, connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Saves a model checkpoint
    /// </summary>
    /// <param name="checkpoint"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>id for model checkpoint</returns>
    public async Task SaveAsync(ModelCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        using var connection = await GetConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new SqlCommand(SqlServerTrainingQueries.UpsertModelManifest, connection, tx as SqlTransaction);

        command.Parameters.AddWithValue("@Id", checkpoint.ModelId);
        command.Parameters.AddWithValue("@HyperParams", JsonSerializer.Serialize(checkpoint.HyperParams, _jsonOpts));
        command.Parameters.AddWithValue("@Tokenizer", JsonSerializer.Serialize(checkpoint.Tokenizer, _jsonOpts));
        command.Parameters.AddWithValue("@Epoch", checkpoint.CurrentEpoch);
        command.Parameters.AddWithValue("@Loss", checkpoint.LastTrainingLoss);
        command.Parameters.AddWithValue("@CreatedAt", checkpoint.CreatedAt);
        command.Parameters.AddWithValue("@Tags", JsonSerializer.Serialize(checkpoint.Tags, _jsonOpts));

        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var blobCmd = new SqlCommand(SqlServerTrainingQueries.UpsertModelBlobs, connection, tx as SqlTransaction);

        blobCmd.Parameters.AddWithValue("@Id", checkpoint.ModelId);
        blobCmd.Parameters.AddWithValue("@WeightsData", checkpoint.WeightsBytes);
        blobCmd.Parameters.AddWithValue("@OptimizerData", checkpoint.OptimizerBytes);

        await blobCmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Loads a training model by model id
    /// </summary>
    /// <param name="modelId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new SqlCommand(SqlServerTrainingQueries.SelectFullModelManifest, connection);

        command.Parameters.AddWithValue("@Id", modelId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken)) return null;

        var hyperParams = JsonSerializer.Deserialize<HyperParameters>(reader.GetString(0))!;
        var tokenizer = JsonSerializer.Deserialize<TokenizerData>(reader.GetString(1))!;
        var epoch = reader.GetInt32(2);
        var loss = (float)reader.GetDouble(3);
        var createdAt = reader.GetDateTime(4);
        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5))!;
        var weights = reader.GetFieldValue<byte[]>(6);
        var optimizer = reader.GetFieldValue<byte[]>(7);

        return new ModelCheckpoint
        {
            ModelId = modelId,
            WeightsBytes = weights,
            OptimizerBytes = optimizer,
            HyperParams = hyperParams,
            Tokenizer = tokenizer,
            CurrentEpoch = epoch,
            LastTrainingLoss = loss,
            CreatedAt = createdAt,
            Tags = tags
        };
    }

    /// <summary>
    /// Deletes a saved training model by training model id (modelId)
    /// </summary>
    /// <param name="modelId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task DeleteAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        using var connection = await GetConnectionAsync(cancellationToken);

        await using var command = new SqlCommand(SqlServerTrainingQueries.DeleteModelManifest, connection);

        command.Parameters.AddWithValue("@Id", modelId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// list all training model ids generated
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

        if (string.IsNullOrEmpty(tagKey) || string.IsNullOrEmpty(tagValue))
        {
            sql = SqlServerTrainingQueries.ListAllModelIds;
            command = new SqlCommand(sql, connection);
        }
        else
        {
            sql = SqlServerTrainingQueries.ListModelIdsByTag.Replace("{tagKey}", tagKey);

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

    public async Task<List<CheckpointSummary>> QueryAsync(CheckpointQuery query, CancellationToken ct = default)
    {
        await using var connection = await GetConnectionAsync(ct);
        var (sql, parameters) = BuildQuerySql(query, includeOrderBy: true, includeLimit: true);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<CheckpointSummary>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CheckpointSummary
            {
                ModelId = reader.GetGuid(0),
                CurrentEpoch = reader.GetInt32(1),
                LastTrainingLoss = (float)reader.GetDouble(2),
                CreatedAt = reader.GetDateTime(3),
                Tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? new()
            });
        }

        return results;
    }

    public async IAsyncEnumerable<CheckpointSummary> QueryStreamAsync(
        CheckpointQuery query,
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
            yield return new CheckpointSummary
            {
                ModelId = reader.GetGuid(0),
                CurrentEpoch = reader.GetInt32(1),
                LastTrainingLoss = (float)reader.GetDouble(2),
                CreatedAt = reader.GetDateTime(3),
                Tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? new()
            };
        }
    }

    public async Task DeleteManyAsync(IEnumerable<Guid> modelIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await GetConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var id in modelIds)
            await DeleteAsync(id, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    //------------- Private Methods -----------------//

    private (string Sql, List<SqlParameter> Parameters) BuildQuerySql(CheckpointQuery query, bool includeOrderBy = true, bool includeLimit = true)
    {
        var sql = new StringBuilder(@"
        SELECT model_id, epoch, loss, created_at, tags
        FROM ModelManifests
        WHERE 1=1
    ");

        var parameters = new List<SqlParameter>();

        if (query.MinEpoch.HasValue) { sql.Append(" AND epoch >= @MinEpoch"); parameters.Add(new SqlParameter("@MinEpoch", query.MinEpoch.Value)); }
        if (query.MaxEpoch.HasValue) { sql.Append(" AND epoch <= @MaxEpoch"); parameters.Add(new SqlParameter("@MaxEpoch", query.MaxEpoch.Value)); }
        if (query.MinLoss.HasValue) { sql.Append(" AND loss >= @MinLoss"); parameters.Add(new SqlParameter("@MinLoss", query.MinLoss.Value)); }
        if (query.MaxLoss.HasValue) { sql.Append(" AND loss <= @MaxLoss"); parameters.Add(new SqlParameter("@MaxLoss", query.MaxLoss.Value)); }
        if (query.CreatedAfter.HasValue) { sql.Append(" AND created_at >= @CreatedAfter"); parameters.Add(new SqlParameter("@CreatedAfter", query.CreatedAfter.Value)); }
        if (query.CreatedBefore.HasValue) { sql.Append(" AND created_at <= @CreatedBefore"); parameters.Add(new SqlParameter("@CreatedBefore", query.CreatedBefore.Value)); }

        if (query.Tags != null && query.Tags.Count > 0)
        {
            foreach (var pair in query.Tags)
            {
                var keyParam = $"@tagKey_{pair.Key}";
                var valParam = $"@tagVal_{pair.Key}";
                sql.Append($" AND JSON_VALUE(tags, '$.{pair.Key}') = {valParam}");
                parameters.Add(new SqlParameter(valParam, pair.Value));
            }
        }

        if (includeOrderBy)
        {
            var orderColumn = query.OrderBy switch
            {
                CheckpointSortField.CreatedAt => "created_at",
                CheckpointSortField.Epoch => "epoch",
                CheckpointSortField.Loss => "loss",
                _ => "created_at"
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