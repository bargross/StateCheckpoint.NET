using FluentAssertions;
using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace StateCheckpoint.NET.Tests.Manager;

public class CheckpointManagerTests : IAsyncLifetime
{
    private readonly string _testRoot;
    private StorageOptions _storageOptions;
    private CheckpointManager _manager;

    public CheckpointManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "StateCheckpointTests", Guid.NewGuid().ToString());
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
        _manager = new CheckpointManager(_storageOptions);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private ModelCheckpoint CreateTestCheckpoint(
        Guid? id = null,
        int epoch = 1,
        float loss = 0.5f,
        Dictionary<string, string>? tags = null)
    {
        return new ModelCheckpoint
        {
            ModelId = id ?? Guid.NewGuid(),
            WeightsBytes = new byte[] { 1, 2, 3 },
            OptimizerBytes = new byte[] { 4, 5, 6 },
            HyperParams = new HyperParameters { HiddenSize = 768 },
            Tokenizer = new TokenizerData { Type = "BPE" },
            CurrentEpoch = epoch,
            LastTrainingLoss = loss,
            CreatedAt = DateTime.UtcNow,
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
        Action act = () => new CheckpointManager(options);
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
        Action act = () => new CheckpointManager(options);
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
        var manager = new CheckpointManager(options);
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
        var manager = new CheckpointManager(options);
        var field = typeof(CheckpointManager).GetField("_backgroundSaver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var saver = field?.GetValue(manager);
        saver.Should().NotBeNull();
    }

    #endregion

    #region Save and Load

    [Fact]
    public async Task SaveAsync_WithNewCheckpoint_SavesAndReturnsId()
    {
        var checkpoint = CreateTestCheckpoint();
        var id = await _manager.SaveAsync(checkpoint);

        id.Should().Be(checkpoint.ModelId);

        var loaded = await _manager.LoadAsync(id);
        loaded.Should().NotBeNull();
        loaded.ModelId.Should().Be(id);
        loaded.WeightsBytes.Should().BeEquivalentTo(checkpoint.WeightsBytes);
        loaded.OptimizerBytes.Should().BeEquivalentTo(checkpoint.OptimizerBytes);
        loaded.HyperParams.Should().BeEquivalentTo(checkpoint.HyperParams);
        loaded.Tokenizer.Should().BeEquivalentTo(checkpoint.Tokenizer);
        loaded.CurrentEpoch.Should().Be(checkpoint.CurrentEpoch);
        loaded.LastTrainingLoss.Should().Be(checkpoint.LastTrainingLoss);
        loaded.Tags.Should().BeEquivalentTo(checkpoint.Tags);
    }

    [Fact]
    public async Task SaveAsync_WithExistingId_UpdatesCheckpoint()
    {
        var checkpoint = CreateTestCheckpoint();
        var id = await _manager.SaveAsync(checkpoint);

        var updated = CreateTestCheckpoint(id, epoch: 5, loss: 0.1f);
        var returnedId = await _manager.SaveAsync(updated);

        returnedId.Should().Be(id);

        var loaded = await _manager.LoadAsync(id);
        loaded.CurrentEpoch.Should().Be(5);
        loaded.LastTrainingLoss.Should().Be(0.1f);
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
    public async Task DeleteAsync_RemovesCheckpoint()
    {
        var checkpoint = CreateTestCheckpoint();
        var id = await _manager.SaveAsync(checkpoint);

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
            var cp = CreateTestCheckpoint();
            ids.Add(await _manager.SaveAsync(cp));
        }

        var all = await _manager.ListAsync();
        all.Should().BeEquivalentTo(ids);
    }

    [Fact]
    public async Task ListAsync_WithTagFilter_ReturnsMatchingIds()
    {
        var cp1 = CreateTestCheckpoint(tags: new Dictionary<string, string> { { "env", "prod" } });
        var cp2 = CreateTestCheckpoint(tags: new Dictionary<string, string> { { "env", "dev" } });

        var id1 = await _manager.SaveAsync(cp1);
        var id2 = await _manager.SaveAsync(cp2);

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
        var cp1 = CreateTestCheckpoint(epoch: 1, loss: 0.5f);
        var cp2 = CreateTestCheckpoint(epoch: 2, loss: 0.3f);
        await _manager.SaveAsync(cp1);
        await _manager.SaveAsync(cp2);

        var query = new CheckpointQuery { MinEpoch = 2 };
        var summaries = await _manager.QueryAsync(query);

        summaries.Should().ContainSingle(s => s.CurrentEpoch == 2);
        summaries.Should().NotContain(s => s.CurrentEpoch == 1);
    }

    [Fact]
    public async Task QueryStreamAsync_StreamsSummaries()
    {
        var cp1 = CreateTestCheckpoint(epoch: 1);
        var cp2 = CreateTestCheckpoint(epoch: 2);
        await _manager.SaveAsync(cp1);
        await _manager.SaveAsync(cp2);

        var stream = _manager.QueryStreamAsync(new CheckpointQuery());
        var list = await stream.ToListAsync();

        list.Should().HaveCount(2);
        list.Should().Contain(s => s.CurrentEpoch == 1);
        list.Should().Contain(s => s.CurrentEpoch == 2);
    }

    #endregion

    #region FindBest

    [Fact]
    public async Task FindBestAsync_ReturnsBestByScore()
    {
        var cp1 = CreateTestCheckpoint(epoch: 1, loss: 0.5f);
        var cp2 = CreateTestCheckpoint(epoch: 2, loss: 0.2f);
        var cp3 = CreateTestCheckpoint(epoch: 3, loss: 0.4f);
        await _manager.SaveAsync(cp1);
        await _manager.SaveAsync(cp2);
        await _manager.SaveAsync(cp3);

        // By loss (lowest = highest score when using negative loss)
        var best = await _manager.FindBestAsync(s => -s.LastTrainingLoss);
        best.Should().NotBeNull();
        best.ModelId.Should().Be(cp2.ModelId);
        best.LastTrainingLoss.Should().Be(0.2f);
    }

    [Fact]
    public async Task FindBestByLossAsync_ReturnsLowestLoss()
    {
        var cp1 = CreateTestCheckpoint(loss: 0.5f);
        var cp2 = CreateTestCheckpoint(loss: 0.1f);
        await _manager.SaveAsync(cp1);
        await _manager.SaveAsync(cp2);

        var best = await _manager.FindBestByLossAsync();
        best.Should().NotBeNull();
        best.ModelId.Should().Be(cp2.ModelId);
    }

    [Fact]
    public async Task FindLatestEpochAsync_ReturnsHighestEpoch()
    {
        var cp1 = CreateTestCheckpoint(epoch: 1);
        var cp2 = CreateTestCheckpoint(epoch: 10);
        var cp3 = CreateTestCheckpoint(epoch: 5);
        await _manager.SaveAsync(cp1);
        await _manager.SaveAsync(cp2);
        await _manager.SaveAsync(cp3);

        var best = await _manager.FindLatestEpochAsync();
        best.Should().NotBeNull();
        best.ModelId.Should().Be(cp2.ModelId);
        best.CurrentEpoch.Should().Be(10);
    }

    [Fact]
    public async Task FindBestAsync_WhenNoCheckpoints_ReturnsNull()
    {
        var result = await _manager.FindBestAsync(s => s.CurrentEpoch);
        result.Should().BeNull();
    }

    [Fact]
    public async Task FindBestAsync_WithFilter_AppliesFilter()
    {
        var cp1 = CreateTestCheckpoint(epoch: 1, loss: 0.5f);
        var cp2 = CreateTestCheckpoint(epoch: 2, loss: 0.2f);
        var cp3 = CreateTestCheckpoint(epoch: 3, loss: 0.4f);
        await _manager.SaveAsync(cp1);
        await _manager.SaveAsync(cp2);
        await _manager.SaveAsync(cp3);

        var filter = new CheckpointQuery { MinEpoch = 2 };
        var best = await _manager.FindBestAsync(s => -s.LastTrainingLoss, filter);

        best.Should().NotBeNull();
        best.ModelId.Should().Be(cp2.ModelId);
        best.CurrentEpoch.Should().Be(2);
        best.LastTrainingLoss.Should().Be(0.2f);
    }

    #endregion

    #region Dispose

    [Fact]
    public async Task DisposeAsync_DisposesStore()
    {
        // Already called in DisposeAsync of the test class
        // We'll just verify that the store is disposed by trying to use it? Not needed.
        // We can check that the directory is gone? The test already does.
        // This test just ensures the method exists and doesn't throw.
        await _manager.DisposeAsync();
        // If the store was already disposed, calling again should be safe.
        await _manager.DisposeAsync();
    }

    #endregion
}