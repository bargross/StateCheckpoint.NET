using StateCheckpoint.NET.Models;

namespace StateCheckpoint.NET.Settings;

/// <summary>
/// 
/// </summary>
public class StorageOptions
{
    /// <summary>
    /// 
    /// </summary>
    public StoreType StoreType { get; set; } = StoreType.Local;
    
    /// <summary>
    /// 
    /// </summary>
    public SqlDbType SqlDbType { get; set; } = SqlDbType.Postgres;  // ignored if StorageType is not SqlDb

    /// <summary>
    /// 
    /// </summary>
    public FileSystemStoreOptions? FileSystemStoreOptions { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public DbStorageOptions? DbStoreOptions { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public BackgroundSaveOptions? BackgroundSaveOptions { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public RetentionPolicy? RetentionPolicy { get; set; }
}

