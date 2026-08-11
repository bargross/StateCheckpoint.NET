using System.Text;

namespace StateCheckpoint.NET.Models;

public class CheckpointDiff
{
    /// <summary>ID of the reference checkpoint (the 'left' side).</summary>
    public Guid BaselineId { get; set; }

    /// <summary>ID of the compared checkpoint (the 'right' side, usually newer).</summary>
    public Guid CandidateId { get; set; }

    /// <summary>Difference in epoch: Candidate.Epoch - Baseline.Epoch (positive means candidate is later).</summary>
    public int EpochDelta { get; set; }

    /// <summary>Difference in loss: Candidate.Loss - Baseline.Loss (negative means candidate has lower loss).</summary>
    public float LossDelta { get; set; }

    /// <summary>Time difference: Candidate.CreatedAt - Baseline.CreatedAt.</summary>
    public TimeSpan AgeDelta { get; set; }

    /// <summary>Hyperparameters that changed, with old (baseline) and new (candidate) values.</summary>
    public Dictionary<string, (object? BaselineValue, object? CandidateValue)> HyperParamChanges { get; set; } = new();

    /// <summary>Tags present in the candidate but not in the baseline.</summary>
    public Dictionary<string, string> TagsAddedInCandidate { get; set; } = new();

    /// <summary>Tags present in the baseline but not in the candidate.</summary>
    public Dictionary<string, string> TagsRemovedInCandidate { get; set; } = new();

    /// <summary>Returns a human-readable summary of the differences.</summary>
    public string GetSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Comparing Baseline '{BaselineId}' vs Candidate '{CandidateId}':");
        sb.AppendLine($"  Epoch delta: {EpochDelta} ({(EpochDelta > 0 ? "Candidate is newer" : "Baseline is newer")})");
        sb.AppendLine($"  Loss delta: {LossDelta:F4} ({(LossDelta < 0 ? "Candidate has lower loss (better)" : "Baseline has lower loss (better)")})");
        sb.AppendLine($"  Age delta: {AgeDelta.TotalHours:F1} hours");

        if (HyperParamChanges.Any())
        {
            sb.AppendLine("  Hyperparameter changes:");
            foreach (var kvp in HyperParamChanges)
                sb.AppendLine($"    {kvp.Key}: {kvp.Value.BaselineValue} → {kvp.Value.CandidateValue}");
        }

        if (TagsAddedInCandidate.Any())
        {
            sb.AppendLine("  Tags added in candidate:");
            foreach (var kvp in TagsAddedInCandidate)
                sb.AppendLine($"    + {kvp.Key}={kvp.Value}");
        }

        if (TagsRemovedInCandidate.Any())
        {
            sb.AppendLine("  Tags removed in candidate:");
            foreach (var kvp in TagsRemovedInCandidate)
                sb.AppendLine($"    - {kvp.Key}={kvp.Value}");
        }

        if (!HyperParamChanges.Any() && !TagsAddedInCandidate.Any() && !TagsRemovedInCandidate.Any())
            sb.AppendLine("  No other differences found.");

        return sb.ToString();
    }
}
