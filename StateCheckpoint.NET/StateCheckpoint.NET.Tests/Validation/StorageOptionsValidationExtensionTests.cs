using FluentAssertions;
using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;
using System.Text;

namespace StateCheckpoint.NET;

public class StorageOptionsValidationTests
{
    #region StoreType Local

    [Fact]
    public void Validate_WhenStoreTypeLocalAndFileSystemOptionsNull_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = null
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("FileSystemStoreOptions is required when StoreType is Local.");
    }

    public string ToString(bool[,] container)
    {
        if (container == null) return string.Empty;

        var n = container.GetLength(0);
        var sb = new StringBuilder(n * n);
        for (var rw = 0; rw < n; rw++)
        {
            for (var col = 0; col < n; col++)
            {
                sb.Append(container[rw, col].ToString());
            }

            if (rw < n - 1) sb.Append(Environment.NewLine);
        }

        return sb.ToString();
    }

    [Fact]
    public void Validate_WhenStoreTypeLocalAndRootPathNull_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = null }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RootPath is required when StoreType is Local.");
    }

    [Fact]
    public void Validate_WhenStoreTypeLocalAndRootPathEmpty_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "" }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RootPath is required when StoreType is Local.");
    }

    [Fact]
    public void Validate_WhenStoreTypeLocalWithValidOptions_Passes()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" }
        };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    #endregion

    #region StoreType SqlDb

    [Fact]
    public void Validate_WhenStoreTypeSqlDbAndDbOptionsNull_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            DbStoreOptions = null
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("DbStoreOptions is required when StoreType is SqlDb.");
    }

    [Fact]
    public void Validate_WhenStoreTypeSqlDbAndConnectionStringNull_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            DbStoreOptions = new DbStorageOptions { ConnectionString = null }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ConnectionString is required when StoreType is SqlDb.");
    }

    [Fact]
    public void Validate_WhenStoreTypeSqlDbAndConnectionStringEmpty_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            DbStoreOptions = new DbStorageOptions { ConnectionString = "" }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ConnectionString is required when StoreType is SqlDb.");
    }

    [Fact]
    public void Validate_WhenStoreTypeSqlDbWithValidOptions_Passes()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            DbStoreOptions = new DbStorageOptions { ConnectionString = "Host=localhost;Database=test" }
        };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    #endregion

    #region Invalid StoreType

    // StoreType is an enum, so we can force an invalid value via casting.
    [Fact]
    public void Validate_WhenStoreTypeInvalid_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = (StoreType)999, // invalid value
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Unsupported StoreType '999'. Valid values are Local or SqlDb.");
    }

    #endregion

    #region BackgroundSaveOptions

    [Fact]
    public void Validate_WhenBackgroundSaveEnabledAndQueueCapacityZero_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            BackgroundSaveOptions = new BackgroundSaveOptions { Enabled = true, QueueCapacity = 0 }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("BackgroundSaveOptions.QueueCapacity must be greater than 0 when background saves are enabled.");
    }

    [Fact]
    public void Validate_WhenBackgroundSaveEnabledAndQueueCapacityNegative_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            BackgroundSaveOptions = new BackgroundSaveOptions { Enabled = true, QueueCapacity = -5 }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("BackgroundSaveOptions.QueueCapacity must be greater than 0 when background saves are enabled.");
    }

    [Fact]
    public void Validate_WhenBackgroundSaveDisabled_IgnoresQueueCapacity()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            BackgroundSaveOptions = new BackgroundSaveOptions { Enabled = false, QueueCapacity = 0 }
        };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenBackgroundSaveOptionsNull_Passes()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            BackgroundSaveOptions = null
        };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    #endregion

    #region RetentionPolicy

    [Fact]
    public void Validate_WhenMaxCheckpointsZero_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy { MaxCheckpoints = 0 }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RetentionPolicy.MaxCheckpoints must be greater than 0.");
    }

    [Fact]
    public void Validate_WhenMaxCheckpointsNegative_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy { MaxCheckpoints = -1 }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RetentionPolicy.MaxCheckpoints must be greater than 0.");
    }

    [Fact]
    public void Validate_WhenMaxAgeZero_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy { MaxAge = TimeSpan.Zero }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RetentionPolicy.MaxAge must be a positive TimeSpan.");
    }

    [Fact]
    public void Validate_WhenMaxAgeNegative_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy { MaxAge = TimeSpan.FromDays(-1) }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RetentionPolicy.MaxAge must be a positive TimeSpan.");
    }

    [Fact]
    public void Validate_WhenMaxEpochAgeZero_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy { MaxEpochAge = 0 }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RetentionPolicy.MaxEpochAge must be greater than 0.");
    }

    [Fact]
    public void Validate_WhenMaxEpochAgeNegative_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy { MaxEpochAge = -5 }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RetentionPolicy.MaxEpochAge must be greater than 0.");
    }

    [Fact]
    public void Validate_WhenMaxLossThresholdNegative_Throws()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy { MaxLossThreshold = -1.0f }
        };
        Action act = () => options.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("RetentionPolicy.MaxLossThreshold must be a positive number.");
    }

    [Fact]
    public void Validate_WhenRetentionPolicyNull_Passes()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = null
        };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_WhenRetentionPolicyValid_Passes()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.Local,
            FileSystemStoreOptions = new FileSystemStoreOptions { RootPath = "./data" },
            RetentionPolicy = new RetentionPolicy
            {
                MaxCheckpoints = 10,
                MaxAge = TimeSpan.FromDays(30),
                MaxEpochAge = 5,
                MaxLossThreshold = 0.5f,
                AlwaysKeepBest = true,
                PinnedTags = new Dictionary<string, string> { { "status", "production" } }
            }
        };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    #endregion

    #region Combined Complex Validations

    [Fact]
    public void Validate_WithAllOptionsValid_Passes()
    {
        var options = new StorageOptions
        {
            StoreType = StoreType.SqlDb,
            SqlDbType = SqlDbType.Postgres,
            DbStoreOptions = new DbStorageOptions
            {
                ConnectionString = "Host=localhost;Database=test",
                EnsureSchemaOnStartup = true
            },
            BackgroundSaveOptions = new BackgroundSaveOptions
            {
                Enabled = true,
                QueueCapacity = 20,
                OnError = ex => Console.WriteLine(ex.Message)
            },
            RetentionPolicy = new RetentionPolicy
            {
                MaxCheckpoints = 5,
                MaxAge = TimeSpan.FromDays(7),
                MaxEpochAge = 3,
                MaxLossThreshold = 0.8f,
                AlwaysKeepBest = true
            }
        };
        Action act = () => options.Validate();
        act.Should().NotThrow();
    }

    #endregion
}
