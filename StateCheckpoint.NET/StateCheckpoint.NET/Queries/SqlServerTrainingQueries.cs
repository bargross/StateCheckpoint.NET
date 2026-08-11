namespace StateCheckpoint.NET;

/// <summary>
/// SQL Server queries specific to the Training (Model) domain.
/// Uses VARBINARY(MAX) for weights/optimizer (up to 2 GB).
/// </summary>
internal static class SqlServerTrainingQueries
{
    // --- Schema ---
    public const string EnsureModelSchema = @"
        IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ModelManifests' AND xtype='U')
        BEGIN
            CREATE TABLE ModelManifests (
                model_id UNIQUEIDENTIFIER PRIMARY KEY,
                hyper_params NVARCHAR(MAX) NOT NULL,  -- JSON
                tokenizer NVARCHAR(MAX) NOT NULL,    -- JSON
                epoch INT NOT NULL,
                loss FLOAT NOT NULL,
                created_at DATETIME2 NOT NULL,
                tags NVARCHAR(MAX) NOT NULL          -- JSON
            );
        END

        IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ModelBlobs' AND xtype='U')
        BEGIN
            CREATE TABLE ModelBlobs (
                model_id UNIQUEIDENTIFIER PRIMARY KEY,
                weights_data VARBINARY(MAX) NOT NULL,
                optimizer_data VARBINARY(MAX) NOT NULL,
                CONSTRAINT FK_ModelBlobs_ModelManifests FOREIGN KEY (model_id)
                    REFERENCES ModelManifests(ModelId) ON DELETE CASCADE
            );
        END";

    // --- Metadata Operations ---
    public const string UpsertModelManifest = @"
        MERGE INTO ModelManifests AS target
        USING (SELECT @Id AS ModelId) AS source
        ON target.ModelId = source.ModelId
        WHEN MATCHED THEN
            UPDATE SET
                hyper_params = @HyperParams,
                tokenizer = @Tokenizer,
                epoch = @Epoch,
                loss = @Loss,
                tags = @Tags
        WHEN NOT MATCHED THEN
            INSERT (model_id, hyper_params, tokenizer, epoch, loss, created_at, tags)
            VALUES (@Id, @HyperParams, @Tokenizer, @Epoch, @Loss, @CreatedAt, @Tags);";

    public const string DeleteModelManifest =
        "DELETE FROM ModelManifests WHERE model_id = @Id;";

    // --- Blob Operations ---
    public const string SelectModelBlobs =
        "SELECT weights_data, OptimizerData FROM ModelBlobs WHERE model_id = @Id;";

    public const string UpsertModelBlobs = @"
        MERGE INTO ModelBlobs AS target
        USING (SELECT @Id AS model_id) AS source
        ON target.model_id = source.model_id
        WHEN MATCHED THEN
            UPDATE SET
                weights_data = @WeightsData,
                optimizer_data = @OptimizerData
        WHEN NOT MATCHED THEN
            INSERT (model_id, weights_data, optimizer_data)
            VALUES (@Id, @WeightsData, @OptimizerData);";

    // --- Full Load (Join) ---
    public const string SelectFullModelManifest = @"
        SELECT
            m.hyper_params,
            m.tokenizer,
            m.epoch,
            m.loss,
            m.created_at,
            m.tags,
            b.weights_data,
            b.optimizer_data
        FROM ModelManifests m
        INNER JOIN ModelBlobs b ON m.model_id = b.model_id
        WHERE m.ModelId = @Id;";

    // --- Listing ---
    public const string ListAllModelIds = "SELECT model_id FROM ModelManifests;";
    public const string ListModelIdsByTag = "SELECT model_id FROM ModelManifests WHERE JSON_VALUE(Tags, '$.{tagKey}') = @TagValue";
}