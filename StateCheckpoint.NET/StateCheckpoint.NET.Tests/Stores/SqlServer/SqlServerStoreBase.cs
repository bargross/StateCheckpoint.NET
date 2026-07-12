using System.Data;
using StateCheckpoint.NET.Stores;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace StateCheckpoint.NET.Tests.Stores.SqlServer;

[Collection("NonParallel")]
public class SqlServerStoreBaseTests
{
    // Use LocalDB (installed with Visual Studio).
    private const string TestConnectionString = "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=master;Integrated Security=True;";

    private static bool IsLocalDbAvailable()
    {
        try
        {
            using var connection = new SqlConnection(TestConnectionString);
            connection.Open();
            connection.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Concrete test subclass that exposes protected methods.
    private class TestableSqlServerStore : SqlServerStoreBase
    {
        public TestableSqlServerStore(string connectionString) : base(connectionString) { }
        public TestableSqlServerStore(SqlConnection connection) : base(connection) { }

        public new async Task<SqlConnection> GetConnectionAsync(CancellationToken ct = default)
            => await base.GetConnectionAsync(ct);
    }

    [SkippableFact]
    public async Task GetConnectionAsync_OpensConnectionLazily()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        // Arrange
        var store = new TestableSqlServerStore(TestConnectionString);

        // Act
        var connection = await store.GetConnectionAsync();

        // Assert
        connection.State.Should().Be(ConnectionState.Open);

        // Clean up
        await store.DisposeAsync();
    }

    [SkippableFact]
    public async Task GetConnectionAsync_WithOwnedConnection_ReturnsNewConnectionEachTime()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        var store = new TestableSqlServerStore(TestConnectionString);

        // Act
        var connection1 = await store.GetConnectionAsync();
        var connection2 = await store.GetConnectionAsync();

        // Assert – each call yields a new connection from the pool
        connection1.Should().NotBeSameAs(connection2);
        connection1.State.Should().Be(ConnectionState.Open);
        connection2.State.Should().Be(ConnectionState.Open);

        // Clean up
        await connection1.DisposeAsync();
        await connection2.DisposeAsync();
        await store.DisposeAsync();
    }

    [SkippableFact]
    public async Task Constructor_WithExistingConnection_UsesProvidedConnection()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        // Arrange
        using var existingConn = new SqlConnection(TestConnectionString);
        await existingConn.OpenAsync();

        // Act
        var store = new TestableSqlServerStore(existingConn);
        var retrievedConn = await store.GetConnectionAsync();

        // Assert
        retrievedConn.Should().BeSameAs(existingConn);

        // Clean up
        await store.DisposeAsync(); // Should NOT dispose existingConn
        existingConn.State.Should().Be(ConnectionState.Open);
        await existingConn.DisposeAsync();
    }

    [SkippableFact]
    public async Task DisposeAsync_WhenOwnsConnection_DoesNotCloseConnections()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        var store = new TestableSqlServerStore(TestConnectionString);
        var connection = await store.GetConnectionAsync();
        connection.State.Should().Be(ConnectionState.Open);

        // Act – dispose the store
        await store.DisposeAsync();

        // Assert – the connection we obtained is still open (we must close it ourselves)
        connection.State.Should().Be(ConnectionState.Open);

        await connection.DisposeAsync();
        connection.State.Should().Be(ConnectionState.Closed);
    }

    [SkippableFact]
    public async Task DisposeAsync_WhenNotOwnsConnection_DoesNotCloseConnection()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        // Arrange
        using var existingConn = new SqlConnection(TestConnectionString);
        await existingConn.OpenAsync();

        var store = new TestableSqlServerStore(existingConn);

        // Act
        await store.DisposeAsync();

        // Assert
        // The store does not own the connection, so it should remain open.
        existingConn.State.Should().Be(ConnectionState.Open);

        // Clean up manually
        await existingConn.DisposeAsync();
    }

    [SkippableFact]
    public async Task DisposeAsync_MultipleCalls_DoesNotThrow()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        // Arrange
        var store = new TestableSqlServerStore(TestConnectionString);

        // Act & Assert
        await store.DisposeAsync();
        await store.DisposeAsync(); // Second call should be harmless
    }

    [SkippableFact]
    public async Task GetConnectionAsync_RespectsCancellationToken()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var store = new TestableSqlServerStore(TestConnectionString);

        // Act
        var act = () => store.GetConnectionAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [SkippableFact]
    public async Task Constructor_WithConnectionString_DoesNotOpenConnectionImmediately()
    {
        Skip.IfNot(IsLocalDbAvailable(), "LocalDB is not available. Skipping test.");

        // Arrange
        var store = new TestableSqlServerStore(TestConnectionString);

        // Assert - we can inspect the private _connection field via reflection
        // to verify it's null initially.
        var field = typeof(SqlServerStoreBase).GetField("_connection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var connectionField = field?.GetValue(store) as SqlConnection;
        connectionField.Should().BeNull("the connection should not be created until GetConnectionAsync is called.");

        // Clean up
        await store.DisposeAsync();
    }
}