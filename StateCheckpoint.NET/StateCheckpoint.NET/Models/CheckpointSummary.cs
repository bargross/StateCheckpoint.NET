namespace StateCheckpoint.NET.Models;

public class CheckpointSummary
{
    public Guid ModelId { get; set; }
    public int CurrentEpoch { get; set; }
    public float LastTrainingLoss { get; set; }
    public DateTime CreatedAt { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}
