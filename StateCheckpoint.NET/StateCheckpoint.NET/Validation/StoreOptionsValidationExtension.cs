using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

internal static class StoreOptionsValidationExtension
{
    public static void Validate(this StorageOptions value)
    {
        // Store type specific validation
        if (value?.StoreType == StoreType.Local)
        {
            if (value?.FileSystemStoreOptions == null)
                throw new InvalidOperationException("FileSystemStoreOptions is required when StoreType is Local.");

            if (string.IsNullOrWhiteSpace(value?.FileSystemStoreOptions?.RootPath))
                throw new InvalidOperationException("RootPath is required when StoreType is Local.");
        }
        else if (value?.StoreType == StoreType.SqlDb)
        {
            if (value?.DbStoreOptions == null)
                throw new InvalidOperationException("DbStoreOptions is required when StoreType is SqlDb.");

            if (string.IsNullOrWhiteSpace(value?.DbStoreOptions?.ConnectionString))
                throw new InvalidOperationException("ConnectionString is required when StoreType is SqlDb.");

            // SqlDbType enum validation (already enforced by type, but we can check)
            if (!Enum.IsDefined(typeof(SqlDbType), value.SqlDbType))
                throw new InvalidOperationException($"Invalid SqlDbType '{value?.SqlDbType.ToString()}'. Valid values are Postgres or SqlServer.");
        }
        else
        {
            throw new InvalidOperationException($"Unsupported StoreType '{value?.StoreType.ToString()}'. Valid values are Local or SqlDb.");
        }

        // Validate BackgroundSaveOptions if present
        if (value?.BackgroundSaveOptions != null && value?.BackgroundSaveOptions?.Enabled == true)
        {
            if (value?.BackgroundSaveOptions?.QueueCapacity <= 0)
                throw new InvalidOperationException("BackgroundSaveOptions.QueueCapacity must be greater than 0 when background saves are enabled.");
        }

        // Validate RetentionPolicy if present
        if (value?.RetentionPolicy != null)
        {
            if (value?.RetentionPolicy?.MaxCheckpoints != null && value?.RetentionPolicy?.MaxCheckpoints.Value <= 0)
                throw new InvalidOperationException("RetentionPolicy.MaxCheckpoints must be greater than 0.");

            if (value?.RetentionPolicy?.MaxAge != null && value?.RetentionPolicy?.MaxAge <= TimeSpan.Zero)
                throw new InvalidOperationException("RetentionPolicy.MaxAge must be a positive TimeSpan.");

            if (value?.RetentionPolicy?.MaxEpochAge != null && value?.RetentionPolicy?.MaxEpochAge <= 0)
                throw new InvalidOperationException("RetentionPolicy.MaxEpochAge must be greater than 0.");

            if (value?.RetentionPolicy?.MaxLossThreshold != null && value?.RetentionPolicy?.MaxLossThreshold < 0)
                throw new InvalidOperationException("RetentionPolicy.MaxLossThreshold must be a positive number.");

            // If PinnedTags is provided, it's a dictionary; no further validation needed.
        }
    }
}
