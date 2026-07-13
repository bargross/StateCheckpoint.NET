using Npgsql;
using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;
using Testcontainers.PostgreSql;

namespace StateCheckpoint.NET.Tests.Integration.Postgres;

[Collection("NonParallel")]
public class PostgresTests : IntegrationTestsBase
{
    private readonly PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;
    private readonly bool _useTestcontainers;

    public PostgresTests()
    {
        _useTestcontainers = Environment.GetEnvironmentVariable("USE_TESTCONTAINERS") == "true";

        if (_useTestcontainers)
        {
            _container = new PostgreSqlBuilder("postgres:18-alpine")
                .WithDatabase("testdb")
                .WithUsername("testuser")
                .WithPassword("testpassword")
                .WithCleanUp(true)
                .Build();
        }
        else
        {
            _connectionString = DbHelper.BuildConnectionString();
        }
    }

    protected override StorageOptions GetCheckpointStorageOptions()
    {
        return new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            SqlDbType = SqlDbType.Postgres,
            DbStoreOptions = new DbStorageOptions
            {
                ConnectionString = GetConnectionString(),
                EnsureSchemaOnStartup = true
            }
        };
    }

    protected override StorageOptions GetSessionStorageOptions()
    {
        return new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            SqlDbType = SqlDbType.Postgres,
            DbStoreOptions = new DbStorageOptions
            {
                ConnectionString = GetConnectionString(),
                EnsureSchemaOnStartup = true
            }
        };
    }

    private string GetConnectionString()
    {
        if (_useTestcontainers)
            return _container!.GetConnectionString();
        else
            return _connectionString;
    }

    public override async Task InitializeAsync()
    {
        if (_useTestcontainers)
            await _container!.StartAsync();
        else
            await EnsureLocalDatabaseExistsAsync();

        await base.InitializeAsync(); // ensures schema
        await TruncateTablesAsync();  // now tables exist
    }

    private async Task EnsureLocalDatabaseExistsAsync()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(_connectionString);
            var testDbName = "checkpoint_integration";
            builder.Database = "postgres";

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand($"CREATE DATABASE {testDbName};", connection);

            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "42P04")
            {
                // Database already exists
            }

            builder.Database = testDbName;
            _connectionString = builder.ConnectionString;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not connect to local PostgreSQL. Ensure it is running.", ex);
        }
    }

    private async Task TruncateTablesAsync()
    {
        try
        {
            var connString = GetConnectionString();

            await using var connection = new NpgsqlConnection(connString);
            
            await connection.OpenAsync();

            // Check if tables exist
            var checkTables = @"
                SELECT COUNT(*) FROM information_schema.tables 
                WHERE table_name IN ('model_manifests', 'model_blobs', 'inference_sessions')";

            await using var checkCmd = new NpgsqlCommand(checkTables, connection);
            
            var tableCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());
            if (tableCount < 3)
                return;
            

            try
            {
                await using var truncateCmd = new NpgsqlCommand(
                    "TRUNCATE TABLE model_blobs, model_manifests, inference_sessions;",
                    connection);
                await truncateCmd.ExecuteNonQueryAsync();
            }
            catch (PostgresException ex) when (ex.SqlState == "23503")
            {
                await using var deleteCmd = new NpgsqlCommand(
                    "DELETE FROM model_blobs; DELETE FROM model_manifests; DELETE FROM inference_sessions;",
                    connection);

                await deleteCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw;
            }
        }
        catch (Exception ex)
        {
            throw;
        }
    }
    protected override async Task CleanupAsync()
    {
        if (_useTestcontainers && _container != null)
            await _container.DisposeAsync();
    }

    // ----- Tests -----

    [Fact]
    public async Task Checkpoint_RoundTrip_ShouldWork() => await RoundTrip_Checkpoint_ShouldSaveLoadDeleteList();

    [Fact]
    public async Task Session_RoundTrip_ShouldWork() => await RoundTrip_Session_ShouldSaveLoadDeleteList();

    [Fact]
    public async Task Query_ShouldReturnFilteredSummaries() => await base.Query_ShouldReturnFilteredSummaries();

    [Fact]
    public async Task FindBest_ShouldReturnBestCheckpoint() => await base.FindBest_ShouldReturnBestCheckpoint();
}