using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;
using System.IO;

namespace StateCheckpoint.NET.Tests.Integration.FileSystem;

[Collection("NonParallel")]
public class FileSystemTests : IntegrationTestsBase
{
    private readonly string _testRoot;

    public FileSystemTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "StateCheckpointIntegrationTests", Guid.NewGuid().ToString());
    }

    protected override StorageOptions GetCheckpointStorageOptions()
    {
        return new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions
            {
                RootPath = _testRoot,
                EnsureDirectoryExists = true,
                ValidatePermissionsOnStartup = true
            }
        };
    }

    protected override StorageOptions GetSessionStorageOptions()
    {
        return new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions
            {
                RootPath = _testRoot,
                EnsureDirectoryExists = true,
                ValidatePermissionsOnStartup = true
            }
        };
    }

    protected override async Task CleanupAsync()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        await Task.CompletedTask;
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