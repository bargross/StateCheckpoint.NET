using StateCheckpoint.NET.Models;
using StateCheckpoint.NET.Settings;

namespace StateCheckpoint.NET;

internal static class StoreSessionFactory
{
    public static ISessionStore Create(StorageOptions options)
    {
        return options.StoreType switch
        {
            StoreType.Local => new FileSystemSessionStore(options?.FileSystemStoreOptions?.RootPath ?? "./sessions"),
            StoreType.SqlDb when options.SqlDbType == SqlDbType.Postgres =>
                new PostgresSessionStore(options?.DbStoreOptions?.ConnectionString ??
                    throw new InvalidOperationException("ConnectionString required for Postgres.")),
            StoreType.SqlDb when options.SqlDbType == SqlDbType.SqlServer =>
                new SqlServerSessionStore(options?.DbStoreOptions?.ConnectionString ??
                    throw new InvalidOperationException("ConnectionString required for SQL Server.")),
            _ => throw new NotSupportedException($"StoreType {options.StoreType} with SqlDbType {options.SqlDbType} is not supported.")
        };
    }
}
