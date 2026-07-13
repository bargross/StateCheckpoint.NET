namespace StateCheckpoint.NET;

public class RetentionPolicy
{
    /// <summary>Keep only the N most recent checkpoints (by CreatedAt).</summary>
    public int? MaxCheckpoints { get; set; }

    /// <summary>Delete checkpoints older than this age.</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>Delete checkpoints with loss above this threshold.</summary>
    public float? MaxLossThreshold { get; set; }

    /// <summary>Delete checkpoints more than N epochs behind the latest saved epoch.</summary>
    public int? MaxEpochAge { get; set; }

    /// <summary>Never delete the checkpoint with the lowest loss, even if other rules would remove it. Default: true.</summary>
    public bool AlwaysKeepBest { get; set; } = true;

    /// <summary>Tags that exempt a checkpoint from all retention rules (e.g., { "status": "production" }).</summary>
    public Dictionary<string, string>? PinnedTags { get; set; }
}
