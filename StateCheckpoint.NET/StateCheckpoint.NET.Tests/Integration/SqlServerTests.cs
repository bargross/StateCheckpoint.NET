using Microsoft.Data.SqlClient;
using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;
using Testcontainers.MsSql;

namespace StateCheckpoint.NET.Tests.Integration.SqlServer;

[Collection("NonParallel")]
public class SqlServerTests : IntegrationTestsBase
{
    private readonly MsSqlContainer? _container;
    private string _connectionString = string.Empty;
    private readonly bool _useTestcontainers;

    public SqlServerTests()
    {
        _useTestcontainers = Environment.GetEnvironmentVariable("USE_TESTCONTAINERS") == "true";

        if (_useTestcontainers)
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword("Your_password123!")
                .WithCleanUp(true)
                .Build();
        }
        else
        {
            _connectionString = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;";
        }
    }

    protected override StorageOptions GetCheckpointStorageOptions()
    {
        return new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            SqlDbType = SqlDbType.SqlServer,
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
            SqlDbType = SqlDbType.SqlServer,
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
        
        else return _connectionString;
    }

    public override async Task InitializeAsync()
    {
        if (_useTestcontainers)
            await _container!.StartAsync();

        else await EnsureLocalDbDatabaseExistsAsync();

        await base.InitializeAsync();

        await TruncateTablesAsync();
    }

    private async Task EnsureLocalDbDatabaseExistsAsync()
    {
        const string masterConnectionString = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;";
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();

        var createDbQuery = @"
            IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'CheckpointIntegration')
            BEGIN
                CREATE DATABASE [CheckpointIntegration];
            END";
        await using var command = new SqlCommand(createDbQuery, connection);
        await command.ExecuteNonQueryAsync();

        _connectionString = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=CheckpointIntegration;Integrated Security=True;";
    }

    private async Task TruncateTablesAsync()
    {
        try
        {
            var connString = GetConnectionString();

            await using var connection = new SqlConnection(connString);

            await connection.OpenAsync();

            // Check if tables exist
            var checkTables = @"
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_NAME IN ('ModelManifests', 'ModelBlobs', 'InferenceSessions')";

            await using var checkCmd = new SqlCommand(checkTables, connection);

            var tableCount = (int)await checkCmd.ExecuteScalarAsync();

            if (tableCount < 3)
                return;
            
            try
            {
                await using var truncateCmd = new SqlCommand(
                    "TRUNCATE TABLE ModelBlobs; TRUNCATE TABLE ModelManifests; TRUNCATE TABLE InferenceSessions;",
                    connection);

                await truncateCmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (ex.Number == 4712)
            {
                await using var deleteCmd = new SqlCommand(
                    "DELETE FROM ModelBlobs; DELETE FROM ModelManifests; DELETE FROM InferenceSessions;",
                    connection);

                await deleteCmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                throw; // rethrow to make test fail so we can see the error
            }
        }
        catch (Exception ex)
        {
            throw; // rethrow so test fails and we can diagnose
        }
    }

    protected override async Task CleanupAsync()
    {
        // Truncate again after the test (for safety)
        await TruncateTablesAsync();

        if (_useTestcontainers && _container != null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task Checkpoint_RoundTrip_ShouldWork() => await RoundTrip_Checkpoint_ShouldSaveLoadDeleteList();

    [Fact]
    public async Task Session_RoundTrip_ShouldWork() => await RoundTrip_Session_ShouldSaveLoadDeleteList();

    [Fact]
    public async Task Query_ShouldReturnFilteredSummaries() => await base.Query_ShouldReturnFilteredSummaries();

    [Fact]
    public async Task FindBest_ShouldReturnBestCheckpoint() => await base.FindBest_ShouldReturnBestCheckpoint();
}