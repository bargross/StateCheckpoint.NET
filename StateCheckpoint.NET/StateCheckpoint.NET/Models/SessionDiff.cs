using System.Text;

namespace StateCheckpoint.NET.Models;

public class SessionDiff
{
    /// <summary>ID of the reference (baseline) session.</summary>
    public Guid BaselineId { get; set; }

    /// <summary>ID of the compared (candidate) session.</summary>
    public Guid CandidateId { get; set; }

    /// <summary>Time difference: Candidate.LastUpdated - Baseline.LastUpdated.</summary>
    public TimeSpan LastUpdatedDelta { get; set; }

    /// <summary>Model fingerprint difference (null if same).</summary>
    public string? ModelFingerprintChange { get; set; }

    /// <summary>Difference in token history length: Candidate.TokenHistory.Length - Baseline.TokenHistory.Length.</summary>
    public int TokenHistoryLengthDelta { get; set; }

    /// <summary>Sampling configuration changes.</summary>
    public Dictionary<string, (object? BaselineValue, object? CandidateValue)> SamplingConfigChanges { get; set; } = new();

    /// <summary>Tags present in the candidate but not in the baseline.</summary>
    public Dictionary<string, string> TagsAddedInCandidate { get; set; } = new();

    /// <summary>Tags present in the baseline but not in the candidate.</summary>
    public Dictionary<string, string> TagsRemovedInCandidate { get; set; } = new();

    /// <summary>Returns a human-readable summary.</summary>
    public string GetSummary()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Comparing Baseline '{BaselineId}' vs Candidate '{CandidateId}':");
        sb.AppendLine($"  Last updated delta: {LastUpdatedDelta.TotalMinutes:F1} minutes");

        if (!string.IsNullOrEmpty(ModelFingerprintChange))
            sb.AppendLine($"  Model fingerprint changed: {ModelFingerprintChange}");

        sb.AppendLine($"  Token history length delta: {TokenHistoryLengthDelta} ({(TokenHistoryLengthDelta > 0 ? "Candidate has more tokens" : "Candidate has fewer tokens")})");


        if (SamplingConfigChanges.Any())
        {
            sb.AppendLine("  Sampling config changes:");
            foreach (var kvp in SamplingConfigChanges)
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

        if (!SamplingConfigChanges.Any() && !TagsAddedInCandidate.Any() && !TagsRemovedInCandidate.Any())
            sb.AppendLine("  No other differences found.");

        return sb.ToString();
    }
}
