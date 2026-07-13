using Microsoft.Data.SqlClient;
using System.Data;

namespace StateCheckpoint.NET;

/// <summary>
/// Abstract base class for SQL Server stores.
/// Manages connection lifecycle and lazy opening.
/// </summary>
internal abstract class SqlServerStoreBase : IAsyncDisposable
{
    private readonly string _connectionString = string.Empty;
    private SqlConnection? _connection;
    private readonly bool _ownsConnection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// Initializes the store with a connection string.
    /// The store will create and manage its own SqlConnection.
    /// </summary>
    protected SqlServerStoreBase(string connectionString)
    {
        _connectionString = connectionString;
        _ownsConnection = true;
    }

    /// <summary>
    /// Initializes the store with an existing SqlConnection.
    /// The store will NOT dispose the connection (the caller owns it).
    /// </summary>
    protected SqlServerStoreBase(SqlConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _ownsConnection = false;
    }

    /// <summary>
    /// Gets an open SQL Server connection. Opens it lazily if not already open.
    /// </summary>
    protected async Task<SqlConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (!_ownsConnection)
        {
            // External connection – ensure thread‑safe access
            await _connectionLock.WaitAsync(ct);

            try
            {
                if (_connection!.State != ConnectionState.Open)
                    await _connection.OpenAsync(ct);

                return _connection;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        // Owned connection – create a new one per call
        var connection = new SqlConnection(_connectionString!);

        await connection.OpenAsync(ct);

        return connection;
    }

    /// <summary>
    /// Disposes the underlying SqlConnection ONLY if this store created it.
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        if (_ownsConnection && _connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}