using System.Text.Json;
using Npgsql;
using Checkpoint.NET.Models;
using Checkpoint.NET.Queries;

namespace Checkpoint.NET.Stores.Postgres;

public class PostgresModelStore : PostgresStoreBase, IModelStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    // Option 1: Connection String (Library manages DataSource)
    public PostgresModelStore(string connectionString) : base(connectionString) { }

    // Option 2: Existing DataSource (Caller manages lifetime)
    public PostgresModelStore(NpgsqlDataSource dataSource) : base(dataSource) { }

    // --- Schema ---
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(PostgresTrainingQueries.EnsureModelSchema, conn);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // --- Save ---
    public async Task SaveAsync(ModelCheckpoint checkpoint, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. Upsert metadata (Domain-specific)
        await using var cmd = new NpgsqlCommand(PostgresTrainingQueries.UpsertModelManifest, conn, tx);

        cmd.Parameters.AddWithValue("@id", checkpoint.ModelId);
        cmd.Parameters.AddWithValue("@hp", JsonSerializer.Serialize(checkpoint.HyperParams, _jsonOpts));
        cmd.Parameters.AddWithValue("@tok", JsonSerializer.Serialize(checkpoint.Tokenizer, _jsonOpts));
        cmd.Parameters.AddWithValue("@epoch", checkpoint.CurrentEpoch);
        cmd.Parameters.AddWithValue("@loss", checkpoint.LastTrainingLoss);
        cmd.Parameters.AddWithValue("@now", checkpoint.CreatedAt);
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(checkpoint.Tags, _jsonOpts));

        await cmd.ExecuteNonQueryAsync(ct);

        // 2. Get existing OIDs (Domain-specific)
        uint oldWeightsOid = 0, oldOptimizerOid = 0;
        await using var selectCmd = new NpgsqlCommand(PostgresTrainingQueries.SelectModelBlobOids, conn, tx);

        selectCmd.Parameters.AddWithValue("@id", checkpoint.ModelId);

        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            oldWeightsOid = reader.GetFieldValue<uint>(0);
            oldOptimizerOid = reader.GetFieldValue<uint>(1);
        }
        await reader.CloseAsync();

        // 3. Create new Large Objects (Low-level I/O)
        uint weightsOid = await CreateLargeObjectAsync(conn, tx, ct);
        uint optimizerOid = await CreateLargeObjectAsync(conn, tx, ct);

        // 4. Write binary data (Low-level I/O)
        await WriteLargeObjectAsync(conn, tx, weightsOid, checkpoint.WeightsBytes, ct);
        await WriteLargeObjectAsync(conn, tx, optimizerOid, checkpoint.OptimizerBytes, ct);

        // 5. Delete old OIDs (Low-level I/O)
        if (oldWeightsOid != 0) await UnlinkLargeObjectAsync(conn, tx, oldWeightsOid, ct);
        if (oldOptimizerOid != 0) await UnlinkLargeObjectAsync(conn, tx, oldOptimizerOid, ct);

        // 6. Save OID references (Domain-specific)
        await using var refCmd = new NpgsqlCommand(PostgresTrainingQueries.UpsertModelBlobRefs, conn, tx);

        refCmd.Parameters.AddWithValue("@id", checkpoint.ModelId);
        refCmd.Parameters.AddWithValue("@wOid", weightsOid);
        refCmd.Parameters.AddWithValue("@oOid", optimizerOid);

        await refCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    // --- Large Object Helpers (Use PostgresLargeObjectQueries) ---
    private static async Task<uint> CreateLargeObjectAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(PostgresLargeObjectQueries.CreateLargeObject, conn, tx);

        var result = await cmd.ExecuteScalarAsync(ct);

        return Convert.ToUInt32(result);
    }

    private static async Task WriteLargeObjectAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint oid, byte[] data, CancellationToken ct)
    {
        await using var openCmd = new NpgsqlCommand(PostgresLargeObjectQueries.OpenWrite, conn, tx);

        openCmd.Parameters.AddWithValue("@oid", oid);

        var fd = Convert.ToInt32(await openCmd.ExecuteScalarAsync(ct));

        try
        {
            const int chunkSize = 8192;
            var offset = 0;
            while (offset < data.Length)
            {
                var bytesToWrite = Math.Min(chunkSize, data.Length - offset);
                var chunk = new byte[bytesToWrite];

                Array.Copy(data, offset, chunk, 0, bytesToWrite);

                await using var writeCmd = new NpgsqlCommand(PostgresLargeObjectQueries.WriteChunk, conn, tx);

                writeCmd.Parameters.AddWithValue("@fd", fd);
                writeCmd.Parameters.AddWithValue("@data", chunk);

                await writeCmd.ExecuteScalarAsync(ct);

                offset += bytesToWrite;
            }
        }
        finally
        {
            await using var closeCmd = new NpgsqlCommand(PostgresLargeObjectQueries.CloseLargeObject, conn, tx);

            closeCmd.Parameters.AddWithValue("@fd", fd);

            await closeCmd.ExecuteScalarAsync(ct);
        }
    }

    private static async Task UnlinkLargeObjectAsync(NpgsqlConnection conn, NpgsqlTransaction tx, uint oid, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(PostgresLargeObjectQueries.UnlinkLargeObject, conn, tx);

        cmd.Parameters.AddWithValue("@oid", oid);

        await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<byte[]> ReadLargeObjectAsync(NpgsqlConnection conn, uint oid, CancellationToken ct)
    {
        await using var openCmd = new NpgsqlCommand(PostgresLargeObjectQueries.OpenRead, conn);
        openCmd.Parameters.AddWithValue("@oid", oid);
        var fd = Convert.ToInt32(await openCmd.ExecuteScalarAsync(ct));

        try
        {
            await using var sizeCmd = new NpgsqlCommand(PostgresLargeObjectQueries.GetSize, conn);
            sizeCmd.Parameters.AddWithValue("@fd", fd);
            long size = Convert.ToInt64(await sizeCmd.ExecuteScalarAsync(ct));

            await using var seekCmd = new NpgsqlCommand(PostgresLargeObjectQueries.SeekStart, conn);

            seekCmd.Parameters.AddWithValue("@fd", fd);

            await seekCmd.ExecuteScalarAsync(ct);

            using var ms = new MemoryStream((int)size);
            const int chunkSize = 8192;
            var totalRead = 0;

            while (totalRead < size)
            {
                var bytesToRead = (int)Math.Min(chunkSize, size - totalRead);

                await using var readCmd = new NpgsqlCommand(PostgresLargeObjectQueries.ReadChunk, conn);
                
                readCmd.Parameters.AddWithValue("@fd", fd);
                readCmd.Parameters.AddWithValue("@length", bytesToRead);
                
                var result = await readCmd.ExecuteScalarAsync(ct);

                if (result is byte[] chunk)
                {
                    await ms.WriteAsync(chunk, 0, chunk.Length, ct);
                    totalRead += chunk.Length;
                }
                else break;
            }

            return ms.ToArray();
        }
        finally
        {
            await using var closeCmd = new NpgsqlCommand(PostgresLargeObjectQueries.CloseLargeObject, conn);

            closeCmd.Parameters.AddWithValue("@fd", fd);

            await closeCmd.ExecuteScalarAsync(ct);
        }
    }

    // --- Load ---
    public async Task<ModelCheckpoint?> LoadAsync(Guid modelId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(PostgresTrainingQueries.SelectFullModelManifest, conn);
        cmd.Parameters.AddWithValue("@id", modelId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var hyperParams = JsonSerializer.Deserialize<HyperParameters>(reader.GetString(0))!;
        var tokenizer = JsonSerializer.Deserialize<TokenizerData>(reader.GetString(1))!;
        var epoch = reader.GetInt32(2);
        var loss = (float)reader.GetDouble(3);
        var createdAt = reader.GetDateTime(4);
        var tags = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5))!;
        var weightsOid = reader.GetFieldValue<uint>(6);
        var optimizerOid = reader.GetFieldValue<uint>(7);

        await reader.CloseAsync();

        var weights = await ReadLargeObjectAsync(conn, weightsOid, ct);
        var optimizer = await ReadLargeObjectAsync(conn, optimizerOid, ct);

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
    public async Task DeleteAsync(Guid modelId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var selectCmd = new NpgsqlCommand(PostgresTrainingQueries.SelectModelBlobOids, conn, tx);

        selectCmd.Parameters.AddWithValue("@id", modelId);

        await using var reader = await selectCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            uint wOid = reader.GetFieldValue<uint>(0);
            uint oOid = reader.GetFieldValue<uint>(1);

            await reader.CloseAsync();

            await UnlinkLargeObjectAsync(conn, tx, wOid, ct);
            await UnlinkLargeObjectAsync(conn, tx, oOid, ct);
        }
        else await reader.CloseAsync();

        await using var delCmd = new NpgsqlCommand(PostgresTrainingQueries.DeleteModelManifest, conn, tx);

        delCmd.Parameters.AddWithValue("@id", modelId);

        await delCmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    // --- List ---
    public async Task<List<Guid>> ListAsync(string? tagKey = null, string? tagValue = null, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var sql = string.IsNullOrEmpty(tagKey) || string.IsNullOrEmpty(tagValue)
            ? PostgresTrainingQueries.ListAllModelIds
            : PostgresTrainingQueries.ListModelIdsByTag;

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (!string.IsNullOrEmpty(tagKey) && !string.IsNullOrEmpty(tagValue))
        {
            cmd.Parameters.AddWithValue("@key", tagKey);
            cmd.Parameters.AddWithValue("@value", tagValue);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        
        var list = new List<Guid>();
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetGuid(0));

        return list;
    }
}