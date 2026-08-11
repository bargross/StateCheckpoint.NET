using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

internal static class StoreModelFactory 
{
    public static IModelStore Create(StorageOptions options)
    {
        return options.StoreType switch
        {
            StoreType.Local => new FileSystemModelStore(options?.FileSystemStoreOptions?.RootPath ?? "./checkpoints"),
            StoreType.SqlDb when options.SqlDbType == SqlDbType.Postgres =>
                new PostgresModelStore(options.DbStoreOptions?.ConnectionString ??
                    throw new InvalidOperationException("ConnectionString required for Postgres.")),
            StoreType.SqlDb when options.SqlDbType == SqlDbType.SqlServer =>
                new SqlServerModelStore(options.DbStoreOptions?.ConnectionString ??
                    throw new InvalidOperationException("ConnectionString required for SQL Server.")),
            _ => throw new NotSupportedException($"StoreType {options.StoreType} with SqlDbType {options.SqlDbType} is not supported.")
        };
    }
}
