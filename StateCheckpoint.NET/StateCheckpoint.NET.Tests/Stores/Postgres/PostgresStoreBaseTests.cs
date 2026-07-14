using FluentAssertions;
using Npgsql;
using StateCheckpoint.NET.Stores;

namespace StateCheckpoint.NET.Tests.Stores.Postgres;

public class PostgresStoreBaseTests
{
    // Testable subclass to expose protected members.
    private class TestablePostgresStore : PostgresStoreBase
    {
        public TestablePostgresStore(string connectionString) : base(connectionString) { }
        public TestablePostgresStore(NpgsqlDataSource dataSource) : base(dataSource) { }

        public new async Task<NpgsqlConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
            => await base.GetConnectionAsync(cancellationToken);
    }

    private const string DummyConnectionString = "Host=localhost;Database=dummy";

    [Fact]
    public void Constructor_WithNullDataSource_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new TestablePostgresStore((NpgsqlDataSource)null!);

        // Assert
        act.Should().ThrowExactly<ArgumentNullException>();
    }

    [Fact]
    public async Task DisposeAsync_WhenOwnsDataSource_DisposesDataSource()
    {
        // Arrange
        var store = new TestablePostgresStore(DummyConnectionString);

        // Act
        await store.DisposeAsync();

        // Assert – GetConnectionAsync throws ObjectDisposedException.
        Func<Task> act = () => store.GetConnectionAsync();
        await act.Should().ThrowExactlyAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_WhenNotOwnsDataSource_DoesNotDisposeDataSource()
    {
        // Arrange – external data source (not owned by the store).
        var externalDataSource = NpgsqlDataSource.Create(DummyConnectionString);
        var store = new TestablePostgresStore(externalDataSource);

        // Act – dispose the store.
        await store.DisposeAsync();

        // Assert – GetConnectionAsync should NOT throw ObjectDisposedException.
        // (It may throw other exceptions, but that's fine – we only care about disposal.)
        Func<Task> act = () => store.GetConnectionAsync();
        await act.Should().NotThrowAsync<ObjectDisposedException>();

        // Clean up external data source (caller's responsibility).
        await externalDataSource.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenNotOwnsDataSource_LeavesDataSourceAlive()
    {
        // Arrange
        var externalDataSource = NpgsqlDataSource.Create(DummyConnectionString);
        var store = new TestablePostgresStore(externalDataSource);

        // Act – dispose the store.
        await store.DisposeAsync();

        // Assert – we can still open a connection (it may fail due to invalid connection string,
        // but that's OK – the point is that the data source is not disposed).
        // We'll try to open a connection and catch any non‑ObjectDisposedException.
        Exception? exception = await Record.ExceptionAsync(() => store.GetConnectionAsync());
        exception.Should().NotBeOfType<ObjectDisposedException>();

        await externalDataSource.DisposeAsync();
    }
}