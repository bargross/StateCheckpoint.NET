namespace StateCheckpoint.NET.Settings;

/// <summary>
/// 
/// </summary>
public class DbStorageOptions
{
    /// <summary>
    /// 
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public bool EnsureSchemaOnStartup { get; set; }
}
