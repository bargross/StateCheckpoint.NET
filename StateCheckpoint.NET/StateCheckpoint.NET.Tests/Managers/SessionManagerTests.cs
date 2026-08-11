using FluentAssertions;
using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET.Tests.Manager;

public class SessionManagerTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private StorageOptions _storageOptions;
    private SessionManager _manager;

    public SessionManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "StateCheckpointSessionTests", Guid.NewGuid().ToString());
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_testRoot);
        _storageOptions = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions
            {
                RootPath = _testRoot,
                EnsureDirectoryExists = true,
                ValidatePermissionsOnStartup = true
            }
        };
        _manager = new SessionManager(_storageOptions);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private SessionCheckpoint CreateTestSession(
        Guid? id = null,
        string modelFingerprint = "test-model",
        byte[]? kvCache = null,
        int[]? tokenHistory = null,
        Dictionary<string, string>? tags = null)
    {
        return new SessionCheckpoint
        {
            SessionId = id ?? Guid.NewGuid(),
            KvCacheBytes = kvCache ?? new byte[] { 10, 20, 30 },
            TokenHistory = tokenHistory ?? new int[] { 1, 2, 3 },
            ModelFingerprint = modelFingerprint,
            SamplingConfig = new SamplingData { Temperature = 0.8f },
            LastUpdated = DateTime.UtcNow,
            Tags = tags ?? new Dictionary<string, string>()
        };
    }

    #region Constructor Validation

    [Fact]
    public void Constructor_WhenSqlDbWithoutConnectionString_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            DbStoreOptions = new DbStorageOptions { ConnectionString = null }
        };
        Action act = () => new SessionManager(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ConnectionString is required when StoreType is SqlDb.");
    }

    [Fact]
    public void Constructor_WhenLocalWithoutRootPath_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = null }
        };
        Action act = () => new SessionManager(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RootPath is required when StoreType is Local.");
    }

    [Fact]
    public void Constructor_WithValidLocalOptions_CreatesStore()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = _testRoot }
        };
        var manager = new SessionManager(options);
        manager.Should().NotBeNull();
        Directory.Exists(_testRoot).Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithBackgroundSaveEnabled_CreatesBackgroundSaver()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = _testRoot },
            BackgroundSaveOptions = new BackgroundSaveOptions { Enabled = true, QueueCapacity = 5 }
        };
        var manager = new SessionManager(options);
        var field = typeof(SessionManager).GetField("_backgroundSaver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var saver = field?.GetValue(manager);
        saver.Should().NotBeNull();
    }

    #endregion

    #region Save and Load

    [Fact]
    public async Task SaveAsync_WithNewSession_SavesAndReturnsId()
    {
        var session = CreateTestSession();
        var id = await _manager.SaveAsync(session);

        id.Should().Be(session.SessionId);

        var loaded = await _manager.LoadAsync(id);
        loaded.Should().NotBeNull();
        loaded.SessionId.Should().Be(id);
        loaded.KvCacheBytes.Should().BeEquivalentTo(session.KvCacheBytes);
        loaded.TokenHistory.Should().BeEquivalentTo(session.TokenHistory);
        loaded.ModelFingerprint.Should().Be(session.ModelFingerprint);
        loaded.SamplingConfig.Should().BeEquivalentTo(session.SamplingConfig);
        loaded.Tags.Should().BeEquivalentTo(session.Tags);
    }

    [Fact]
    public async Task SaveAsync_WithExistingId_UpdatesSession()
    {
        var session = CreateTestSession();
        var id = await _manager.SaveAsync(session);

        var updated = CreateTestSession(id, modelFingerprint: "updated-model", kvCache: new byte[] { 99, 88 });
        var returnedId = await _manager.SaveAsync(updated);

        returnedId.Should().Be(id);

        var loaded = await _manager.LoadAsync(id);
        loaded.ModelFingerprint.Should().Be("updated-model");
        loaded.KvCacheBytes.Should().BeEquivalentTo(new byte[] { 99, 88 });
    }

    [Fact]
    public async Task LoadAsync_WhenNotExists_ReturnsNull()
    {
        var result = await _manager.LoadAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    #endregion

    #region Delete

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var session = CreateTestSession();
        var id = await _manager.SaveAsync(session);

        await _manager.DeleteAsync(id);

        var loaded = await _manager.LoadAsync(id);
        loaded.Should().BeNull();
    }

    #endregion

    #region List

    [Fact]
    public async Task ListAsync_WithoutFilters_ReturnsAllIds()
    {
        var ids = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var s = CreateTestSession();
            ids.Add(await _manager.SaveAsync(s));
        }

        var all = await _manager.ListAsync();
        all.Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task ListAsync_WithTagFilter_ReturnsMatchingIds()
    {
        var s1 = CreateTestSession(tags: new Dictionary<string, string> { { "env", "prod" } });
        var s2 = CreateTestSession(tags: new Dictionary<string, string> { { "env", "dev" } });

        var id1 = await _manager.SaveAsync(s1);
        var id2 = await _manager.SaveAsync(s2);

        var result = await _manager.ListAsync("env", "prod");

        result.Count.Should().Be(1);
        result.Should().Contain(id1);
        result.Should().NotContain(id2);
    }

    #endregion

    #region Query

    [Fact]
    public async Task QueryAsync_ReturnsSummaries()
    {
        var s1 = CreateTestSession(modelFingerprint: "model-A");
        var s2 = CreateTestSession(modelFingerprint: "model-B");
        await _manager.SaveAsync(s1);
        await _manager.SaveAsync(s2);

        var query = new SessionQuery { ModelFingerprint = "model-B" };
        var summaries = await _manager.QueryAsync(query);

        summaries.Should().ContainSingle(s => s.ModelFingerprint == "model-B");
        summaries.Should().NotContain(s => s.ModelFingerprint == "model-A");
    }

    [Fact]
    public async Task QueryStreamAsync_StreamsSummaries()
    {
        var s1 = CreateTestSession(modelFingerprint: "model-A");
        var s2 = CreateTestSession(modelFingerprint: "model-B");
        await _manager.SaveAsync(s1);
        await _manager.SaveAsync(s2);

        var stream = _manager.QueryStreamAsync(new SessionQuery());
        var list = await stream.ToListAsync();

        list.Should().HaveCount(2);
        list.Should().Contain(s => s.ModelFingerprint == "model-A");
        list.Should().Contain(s => s.ModelFingerprint == "model-B");
    }

    #endregion

    #region Dispose

    [Fact]
    public async Task DisposeAsync_DisposesStore()
    {
        // Already called in DisposeAsync of the test class.
        // We'll just call it again to ensure no exception.
        await _manager.DisposeAsync();
        await _manager.DisposeAsync(); // should be safe
    }

    #endregion
}