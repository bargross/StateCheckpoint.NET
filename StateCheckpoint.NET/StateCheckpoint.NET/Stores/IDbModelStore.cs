namespace StateCheckpoint.NET;

internal interface IDbModelStore: IModelStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
}
