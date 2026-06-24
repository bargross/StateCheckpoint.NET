using Checkpoint.NET.Models;
using Checkpoint.NET.Queries;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Checkpoint.NET.Stores.Mysql;

public class SqlServerModelStore : SqlServerStoreBase, IModelStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public SqlServerModelStore(string connectionString) : base(connectionString) { }
    public SqlServerModelStore(SqlConnection connection) : base(connection) { }

    // --- Schema ---
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        await using var cmd = new SqlCommand(SqlServerTrainingQueries.EnsureModelSchema, conn);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- Save ---
    public async Task SaveAsync(ModelCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // 1. Upsert metadata
        await using var cmd = new SqlCommand(SqlServerTrainingQueries.UpsertModelManifest, conn, tx as SqlTransaction);

        cmd.Parameters.AddWithValue("@Id", checkpoint.ModelId);
        cmd.Parameters.AddWithValue("@HyperParams", JsonSerializer.Serialize(checkpoint.HyperParams, _jsonOpts));
        cmd.Parameters.AddWithValue("@Tokenizer", JsonSerializer.Serialize(checkpoint.Tokenizer, _jsonOpts));
        cmd.Parameters.AddWithValue("@Epoch", checkpoint.CurrentEpoch);
        cmd.Parameters.AddWithValue("@Loss", checkpoint.LastTrainingLoss);
        cmd.Parameters.AddWithValue("@CreatedAt", checkpoint.CreatedAt);
        cmd.Parameters.AddWithValue("@Tags", JsonSerializer.Serialize(checkpoint.Tags, _jsonOpts));

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // 2. Upsert blobs
        await using var blobCmd = new SqlCommand(SqlServerTrainingQueries.UpsertModelBlobs, conn, tx as SqlTransaction);

        blobCmd.Parameters.AddWithValue("@Id", checkpoint.ModelId);
        blobCmd.Parameters.AddWithValue("@WeightsData", checkpoint.WeightsBytes);
        blobCmd.Parameters.AddWithValue("@OptimizerData", checkpoint.OptimizerBytes);

        await blobCmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    // --- Load ---
    public async Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        await using var cmd = new SqlCommand(SqlServerTrainingQueries.SelectFullModelManifest, conn);

        cmd.Parameters.AddWithValue("@Id", modelId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

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

    // --- Delete ---
    public async Task DeleteAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        await using var cmd = new SqlCommand(SqlServerTrainingQueries.DeleteModelManifest, conn);

        cmd.Parameters.AddWithValue("@Id", modelId);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- List ---
    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);

        string sql;
        SqlCommand cmd;

        if (string.IsNullOrEmpty(tagKey) || string.IsNullOrEmpty(tagValue))
        {
            sql = SqlServerTrainingQueries.ListAllModelIds;
            cmd = new SqlCommand(sql, conn);
        }
        else
        {
            sql = SqlServerTrainingQueries.ListModelIdsByTag;
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