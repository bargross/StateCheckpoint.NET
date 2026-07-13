namespace StateCheckpoint.NET.Models;

public class CheckpointQuery
{
    public int? MinEpoch { get; set; }
    public int? MaxEpoch { get; set; }
    public float? MinLoss { get; set; }
    public float? MaxLoss { get; set; }
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public Dictionary<string, string>? Tags { get; set; }   // AND‑match on all pairs
    public int? Limit { get; set; }
    public CheckpointSortField OrderBy { get; set; } = CheckpointSortField.CreatedAt;
    public bool Descending { get; set; } = true;
}
