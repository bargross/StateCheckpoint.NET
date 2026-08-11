using Microsoft.Data.SqlClient;

namespace StateCheckpoint.NET;

internal abstract class SqlServerStoreBase : IAsyncDisposable
{
    private readonly string _connectionString = string.Empty;

    /// <summary>
    /// Initializes the store with a connection string.
    /// The store will create and manage its own SqlConnection.
    /// </summary>
    protected SqlServerStoreBase(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Gets an open SQL Server connection. Opens it lazily if not already open.
    /// </summary>
    protected async Task<SqlConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        return connection;
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}