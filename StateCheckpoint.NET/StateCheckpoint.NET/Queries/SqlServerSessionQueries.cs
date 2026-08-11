namespace StateCheckpoint.NET;

/// <summary>
/// SQL Server queries specific to the Inference (Session) domain.
/// Stores KV-cache as VARBINARY(MAX).
/// </summary>
internal static class SqlServerSessionQueries
{
    // --- Schema ---
    public const string EnsureSessionSchema = @"
        IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='InferenceSessions' AND xtype='U')
        BEGIN
            CREATE TABLE inference_sessions (
                session_id UNIQUEIDENTIFIER PRIMARY KEY,
                model_fingerprint NVARCHAR(255) NOT NULL,
                token_history NVARCHAR(MAX) NOT NULL,   -- JSON array
                sampling_config NVARCHAR(MAX) NOT NULL, -- JSON
                kv_cache_data VARBINARY(MAX) NOT NULL,
                last_updated DATETIME2 NOT NULL,
                tags NVARCHAR(MAX) NOT NULL            -- JSON
            );
        END";

    // --- CRUD ---
    public const string UpsertInferenceSession = @"
        MERGE INTO inteference_sessions AS target
        USING (SELECT @Id AS session_id) AS source
        ON target.session_id = source.session_id
        WHEN MATCHED THEN
            UPDATE SET
                model_fingerprint = @ModelFingerprint,
                token_history = @TokenHistory,
                sampling_config = @SamplingConfig,
                kv_cache_data = @KvCacheData,
                last_updated = @LastUpdated,
                tags = @Tags
        WHEN NOT MATCHED THEN
            INSERT (session_id, model_fingerprint, token_history, sampling_config, kv_cache_data, last_updated, tags)
            VALUES (@Id, @ModelFingerprint, @TokenHistory, @SamplingConfig, @KvCacheData, @LastUpdated, @Tags);";

    public const string SelectInferenceSession = @"
        SELECT
            model_fingerprint,
            token_history,
            sampling_config,
            kv_cache_data,
            last_updated,
            tags
        FROM inference_sessions
        WHERE session_id = @Id;";

    public const string DeleteInferenceSession =
        "DELETE FROM inference_sessions WHERE session_id = @Id;";

    // --- Listing ---
    public const string ListAllSessionIds = "SELECT session_id FROM inference_sessions;";
    //public const string ListSessionIdsByTag = "SELECT SessionId FROM InferenceSessions WHERE Tags LIKE @TagPattern;";
    public const string ListSessionIdsByTag = "SELECT session_id FROM inference_sessions WHERE JSON_VALUE(Tags, '$.{tagKey}') = @TagValue";
}