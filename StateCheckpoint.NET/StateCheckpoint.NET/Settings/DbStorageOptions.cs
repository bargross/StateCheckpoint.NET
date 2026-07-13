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

    public bool EnsureSchemaOnStartup { get; set; }
}
