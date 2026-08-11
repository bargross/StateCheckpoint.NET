using Microsoft.Data.SqlClient;

namespace StateCheckpoint.NET.Tests.Stores.SqlServer;

public abstract class SqlServerTestBase : IAsyncLifetime
{
    internal SqlServerModelStore ModelStore { get; private set; } = null!;
    internal SqlServerSessionStore SessionStore { get; private set; } = null!;
    private string _connectionString = string.Empty;
    private bool _disposed;

    public async Task InitializeAsync()
    {
        var connectionString = await SqlServerTestHarness.GetConnectionStringAsync();

        _connectionString = connectionString;

        await CleanupAsync();

        ModelStore = new SqlServerModelStore(connectionString);
        await ModelStore.EnsureSchemaAsync();

        SessionStore = new SqlServerSessionStore(connectionString);
        await SessionStore.EnsureSchemaAsync();
    }

    protected virtual async Task CleanupAsync()
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);

            await connection.OpenAsync();

            await using var command = new SqlCommand(
                "TRUNCATE TABLE ModelManifests; TRUNCATE TABLE ModelBlobs; TRUNCATE TABLE InferenceSessions;",
                connection);

            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Ignore if tables don't exist.
        }
    }

    public async Task DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            await CleanupAsync();

            _disposed = true;
        }
        catch
        {
            Console.WriteLine("err");
            // Ignore exceptions during cleanup to prevent test runner crash.
        }
    }
}