namespace StateCheckpoint.NET;

internal interface IDbSessionStore: ISessionStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
}
