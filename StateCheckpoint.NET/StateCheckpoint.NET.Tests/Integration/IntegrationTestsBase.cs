using FluentAssertions;
using Microsoft.Data.SqlClient;
using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET.Tests.Integration;

[Collection("NonParallel")]
public abstract class IntegrationTestsBase : IAsyncLifetime
{
    protected CheckpointManager CheckpointManager { get; set; } = null!;
    protected SessionManager SessionManager { get; set; } = null!;

    private bool _disposed;

    // Derived classes provide storage configuration.
    protected abstract StorageOptions GetCheckpointStorageOptions();
    protected abstract StorageOptions GetSessionStorageOptions();

    public virtual async Task InitializeAsync()
    {
        var checkpointOptions = GetCheckpointStorageOptions();
        var sessionOptions = GetSessionStorageOptions();

        CheckpointManager = new CheckpointManager(checkpointOptions);
        SessionManager = new SessionManager(sessionOptions);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await CheckpointManager.DisposeAsync();
            await SessionManager.DisposeAsync();
            await CleanupAsync();
        }
        catch (Exception)
        {
            // Log or ignore – but do not rethrow to avoid test runner crashes.
        }
    }

    protected virtual async Task CleanupAsync()
    {
        try
        {
            var connectionString = DbHelper.BuildConnectionString();

            await using var connection = new SqlConnection(connectionString);

            await connection.OpenAsync();

            await using var command = new SqlCommand(
                "TRUNCATE TABLE ModelManifests; TRUNCATE TABLE ModelBlobs; TRUNCATE TABLE InferenceSessions;",
                connection);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // Ignore if tables don't exist.
        }
    }

    // ----- Shared integration test helpers -----

    protected async Task RoundTrip_Checkpoint_ShouldSaveLoadDeleteList()
    {
        var checkpoint = new ModelCheckpoint
        {
            ModelId = Guid.NewGuid(),
            WeightsBytes = new byte[] { 1, 2, 3, 4 },
            OptimizerBytes = new byte[] { 5, 6, 7 },
            HyperParams = new HyperParameters { HiddenSize = 768 },
            Tokenizer = new TokenizerData { Type = "BPE" },
            CurrentEpoch = 1,
            LastTrainingLoss = 0.5f,
            CreatedAt = DateTime.UtcNow,
            Tags = new Dictionary<string, string> { { "env", "integration" } }
        };

        var modelId = await CheckpointManager.SaveAsync(checkpoint);

        var loaded = await CheckpointManager.LoadAsync(modelId);
        loaded.Should().NotBeNull();
        loaded.ModelId.Should().Be(modelId);
        loaded.WeightsBytes.Should().BeEquivalentTo(checkpoint.WeightsBytes);
        loaded.OptimizerBytes.Should().BeEquivalentTo(checkpoint.OptimizerBytes);
        loaded.HyperParams.Should().BeEquivalentTo(checkpoint.HyperParams);
        loaded.Tokenizer.Should().BeEquivalentTo(checkpoint.Tokenizer);
        loaded.CurrentEpoch.Should().Be(1);
        loaded.LastTrainingLoss.Should().Be(0.5f);
        loaded.Tags.Should().BeEquivalentTo(checkpoint.Tags);

        var ids = await CheckpointManager.ListAsync();
        ids.Should().Contain(modelId);

        await CheckpointManager.DeleteAsync(modelId);

        var deleted = await CheckpointManager.LoadAsync(modelId);
        deleted.Should().BeNull();

        var idsAfterDelete = await CheckpointManager.ListAsync();
        idsAfterDelete.Should().NotContain(modelId);
    }

    protected async Task RoundTrip_Session_ShouldSaveLoadDeleteList()
    {
        var session = new SessionCheckpoint
        {
            SessionId = Guid.NewGuid(),
            KvCacheBytes = new byte[] { 100, 200, 230 },
            TokenHistory = new int[] { 1, 2, 3 },
            ModelFingerprint = "test-model",
            SamplingConfig = new SamplingData { Temperature = 0.8f },
            LastUpdated = DateTime.UtcNow,
            Tags = new Dictionary<string, string> { { "user", "alice" } }
        };

        var returnedId = await SessionManager.SaveAsync(session);
        returnedId.Should().Be(session.SessionId);

        var loaded = await SessionManager.LoadAsync(session.SessionId);
        loaded.Should().NotBeNull();
        loaded.SessionId.Should().Be(session.SessionId);
        loaded.KvCacheBytes.Should().BeEquivalentTo(session.KvCacheBytes);
        loaded.TokenHistory.Should().BeEquivalentTo(session.TokenHistory);
        loaded.ModelFingerprint.Should().Be(session.ModelFingerprint);
        loaded.SamplingConfig.Should().BeEquivalentTo(session.SamplingConfig);
        loaded.Tags.Should().BeEquivalentTo(session.Tags);

        var ids = await SessionManager.ListAsync();
        ids.Should().Contain(session.SessionId);

        await SessionManager.DeleteAsync(session.SessionId);
        var deleted = await SessionManager.LoadAsync(session.SessionId);
        deleted.Should().BeNull();

        var idsAfterDelete = await SessionManager.ListAsync();
        idsAfterDelete.Should().NotContain(session.SessionId);
    }

    protected async Task Query_ShouldReturnFilteredSummaries()
    {
        var cp1 = new ModelCheckpoint
        {
            ModelId = Guid.NewGuid(),
            WeightsBytes = new byte[] { 1 },
            OptimizerBytes = new byte[] { 2 },
            HyperParams = new HyperParameters(),
            Tokenizer = new TokenizerData(),
            CurrentEpoch = 1,
            LastTrainingLoss = 0.5f,
            CreatedAt = DateTime.UtcNow
        };

        var cp2 = new ModelCheckpoint
        {
            ModelId = Guid.NewGuid(),
            WeightsBytes = new byte[] { 3 },
            OptimizerBytes = new byte[] { 4 },
            HyperParams = new HyperParameters(),
            Tokenizer = new TokenizerData(),
            CurrentEpoch = 2,
            LastTrainingLoss = 0.2f,
            CreatedAt = DateTime.UtcNow
        };

        await CheckpointManager.SaveAsync(cp1);
        await CheckpointManager.SaveAsync(cp2);

        var query = new CheckpointQuery { MinEpoch = 2 };

        var summaries = await CheckpointManager.QueryAsync(query);

        summaries.Count.Should().Be(1);
        summaries.Should().Contain(s => s.CurrentEpoch == 2);
    }

    protected async Task FindBest_ShouldReturnBestCheckpoint()
    {
        var cp1 = new ModelCheckpoint
        {
            ModelId = Guid.NewGuid(),
            WeightsBytes = new byte[] { 1 },
            OptimizerBytes = new byte[] { 2 },
            HyperParams = new HyperParameters(),
            Tokenizer = new TokenizerData(),
            CurrentEpoch = 1,
            LastTrainingLoss = 0.5f,
            CreatedAt = DateTime.UtcNow
        };
        var cp2 = new ModelCheckpoint
        {
            ModelId = Guid.NewGuid(),
            WeightsBytes = new byte[] { 3 },
            OptimizerBytes = new byte[] { 4 },
            HyperParams = new HyperParameters(),
            Tokenizer = new TokenizerData(),
            CurrentEpoch = 2,
            LastTrainingLoss = 0.2f,
            CreatedAt = DateTime.UtcNow
        };

        await CheckpointManager.SaveAsync(cp1);
        await CheckpointManager.SaveAsync(cp2);

        var best = await CheckpointManager.FindBestByLossAsync();
        best.Should().NotBeNull();
        best.ModelId.Should().Be(cp2.ModelId);
        best.LastTrainingLoss.Should().Be(0.2f);
    }
}