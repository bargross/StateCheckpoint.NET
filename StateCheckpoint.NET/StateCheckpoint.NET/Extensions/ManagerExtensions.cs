using StateCheckpoint.NET.Models;
using System.Reflection;

namespace StateCheckpoint.NET;

internal static class ManagerExtensions
{
    public static async Task<int> InternalApplyRetentionPolicyAsync(this CheckpointManager service, CancellationToken cancellationToken = default)
    {
        if (service.RetentionPolicy == null) return 0;

        // Get all summaries (metadata only)
        var summaries = await service.Store.QueryAsync(new CheckpointQuery(), cancellationToken);
        if (summaries.Count == 0) return 0;

        // Identify protected checkpoints (pinned, best)
        var protectedIds = new HashSet<Guid>();
        if (service.RetentionPolicy.PinnedTags != null)
        {
            var pinned = summaries.Where(s => IsPinned(s.Tags, service.RetentionPolicy.PinnedTags));
            foreach (var s in pinned) protectedIds.Add(s.ModelId);
        }
        if (service.RetentionPolicy.AlwaysKeepBest)
        {
            var best = summaries.MinBy(s => s.LastTrainingLoss);
            if (best != null) protectedIds.Add(best.ModelId);
        }

        // Build candidate list
        var candidates = summaries.Where(s => !protectedIds.Contains(s.ModelId)).ToList();
        var toDelete = new HashSet<Guid>();

        // Apply rules
        if (service.RetentionPolicy.MaxCheckpoints.HasValue && service.RetentionPolicy.MaxCheckpoints.Value > 0)
        {
            var sorted = candidates.OrderByDescending(s => s.CreatedAt).ToList();
            var keep = sorted.Take(service.RetentionPolicy.MaxCheckpoints.Value).Select(s => s.ModelId).ToHashSet();
            foreach (var s in candidates.Where(s => !keep.Contains(s.ModelId)))
                toDelete.Add(s.ModelId);
        }
        if (service.RetentionPolicy.MaxAge.HasValue)
        {
            var cutoff = DateTime.UtcNow - service.RetentionPolicy.MaxAge.Value;
            foreach (var s in candidates.Where(s => s.CreatedAt < cutoff))
                toDelete.Add(s.ModelId);
        }
        if (service.RetentionPolicy.MaxLossThreshold.HasValue)
        {
            foreach (var s in candidates.Where(s => s.LastTrainingLoss > service.RetentionPolicy.MaxLossThreshold.Value))
                toDelete.Add(s.ModelId);
        }
        if (service.RetentionPolicy.MaxEpochAge.HasValue && service.RetentionPolicy.MaxEpochAge.Value > 0)
        {
            var latestEpoch = summaries.Max(s => s.CurrentEpoch);
            var minEpoch = latestEpoch - service.RetentionPolicy.MaxEpochAge.Value;
            foreach (var s in candidates.Where(s => s.CurrentEpoch < minEpoch))
                toDelete.Add(s.ModelId);
        }

        if (toDelete.Count == 0) return 0;

        await service.Store.DeleteManyAsync(toDelete, cancellationToken);

        return toDelete.Count;
    }

    public static async Task<int> InternalApplyRetentionPolicyAsync(this SessionManager service, CancellationToken cancellationToken = default)
    {
        if (service.RetentionPolicy == null) return 0;

        // Get all session summaries (metadata only)
        var summaries = await service.Store.QueryAsync(new SessionQuery(), cancellationToken);
        if (summaries.Count == 0) return 0;

        var protectedIds = new HashSet<Guid>();

        // Protect pinned sessions
        if (service.RetentionPolicy.PinnedTags != null)
        {
            var pinned = summaries.Where(s => IsPinned(s.Tags, service.RetentionPolicy.PinnedTags));
            foreach (var s in pinned) protectedIds.Add(s.SessionId);
        }

        // No "best" concept for sessions – skip AlwaysKeepBest

        // Build candidate list (unprotected)
        var candidates = summaries.Where(s => !protectedIds.Contains(s.SessionId)).ToList();
        var toDelete = new HashSet<Guid>();

        // MaxCheckpoints – keep only N most recent sessions
        if (service.RetentionPolicy.MaxCheckpoints.HasValue && service.RetentionPolicy.MaxCheckpoints.Value > 0)
        {
            var sorted = candidates.OrderByDescending(s => s.LastUpdated).ToList();
            var keep = sorted.Take(service.RetentionPolicy.MaxCheckpoints.Value).Select(s => s.SessionId).ToHashSet();
            foreach (var s in candidates.Where(s => !keep.Contains(s.SessionId)))
                toDelete.Add(s.SessionId);
        }

        // MaxAge – delete sessions older than the cutoff
        if (service.RetentionPolicy.MaxAge.HasValue)
        {
            var cutoff = DateTime.UtcNow - service.RetentionPolicy.MaxAge.Value;
            foreach (var s in candidates.Where(s => s.LastUpdated < cutoff))
                toDelete.Add(s.SessionId);
        }

        // (Loss/Epoch rules do not apply to sessions)
        if (toDelete.Count == 0) return 0;

        await service.Store.DeleteManyAsync(toDelete, cancellationToken);

        return toDelete.Count;
    }

    public static async Task<CheckpointDiff> InternalCompareAsync(this CheckpointManager service, Guid baseLineId, Guid CandidateId, CancellationToken cancellationToken = default)
    {
        // Load both summaries in parallel
        var summaryA = await service.Store.GetCheckpointSummaryAsync(baseLineId, cancellationToken);
        var summaryB = await service.Store.GetCheckpointSummaryAsync(CandidateId, cancellationToken);

        if (summaryA == null)
            throw new ArgumentException($"Checkpoint with ID '{baseLineId}' not found.", nameof(baseLineId));

        if (summaryB == null)
            throw new ArgumentException($"Checkpoint with ID '{CandidateId}' not found.", nameof(CandidateId));

        var diff = new CheckpointDiff
        {
            BaselineId = baseLineId,
            CandidateId = CandidateId,
            EpochDelta = summaryB.CurrentEpoch - summaryA.CurrentEpoch,
            LossDelta = summaryB.LastTrainingLoss - summaryA.LastTrainingLoss,
            AgeDelta = summaryB.CreatedAt - summaryA.CreatedAt
        };

        // Compare hyperparameters (using reflection on HyperParameters)
        diff.HyperParamChanges = CompareHyperParams(summaryA.HyperParams, summaryB.HyperParams);

        // Compare tags
        diff.TagsAddedInCandidate = summaryB.Tags
            .Where(kvp => !summaryA.Tags.ContainsKey(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        diff.TagsRemovedInCandidate = summaryA.Tags
            .Where(kvp => !summaryB.Tags.ContainsKey(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return diff;
    }

    public static async Task<SessionDiff> InternalCompareAsync(this SessionManager service, Guid baselineId, Guid candidateId, CancellationToken cancellationToken = default)
    {
        var baseline = await service.Store.GetSessionSummaryAsync(baselineId, cancellationToken);
        var candidate = await service.Store.GetSessionSummaryAsync(candidateId, cancellationToken);

        if (baseline == null)
            throw new ArgumentException($"Session with ID '{baselineId}' not found.", nameof(baselineId));
        if (candidate == null)
            throw new ArgumentException($"Session with ID '{candidateId}' not found.", nameof(candidateId));

        var diff = new SessionDiff
        {
            BaselineId = baselineId,
            CandidateId = candidateId,
            LastUpdatedDelta = candidate.LastUpdated - baseline.LastUpdated,
            TokenHistoryLengthDelta = candidate.TokenHistoryLength - baseline.TokenHistoryLength,
            ModelFingerprintChange = baseline.ModelFingerprint != candidate.ModelFingerprint
                ? $"{baseline.ModelFingerprint} → {candidate.ModelFingerprint}"
                : null,
            SamplingConfigChanges = CompareSamplingConfigs(baseline.SamplingConfig, candidate.SamplingConfig)
        };

        // Tags diff
        diff.TagsAddedInCandidate = candidate.Tags
            .Where(kvp => !baseline.Tags.ContainsKey(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        diff.TagsRemovedInCandidate = baseline.Tags
            .Where(kvp => !candidate.Tags.ContainsKey(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return diff;
    }

    private static Dictionary<string, (object? BaselineValue, object? CandidateValue)> CompareSamplingConfigs(
        SamplingData? baseline,
        SamplingData? candidate)
    {
        var changes = new Dictionary<string, (object?, object?)>();
        if (baseline == null && candidate == null) return changes;
        if (baseline == null) throw new ArgumentException("Baseline sampling config is null.");
        if (candidate == null) throw new ArgumentException("Candidate sampling config is null.");

        var props = typeof(SamplingData).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in props)
        {
            var oldVal = prop.GetValue(baseline);
            var newVal = prop.GetValue(candidate);
            if (!Equals(oldVal, newVal))
                changes[prop.Name] = (oldVal, newVal);
        }
        return changes;
    }

    private static Dictionary<string, (object? OldValue, object? NewValue)> CompareHyperParams(HyperParameters? oldHp, HyperParameters? newHp)
    {
        var changes = new Dictionary<string, (object?, object?)>();
        if (oldHp == null && newHp == null) return changes;
        if (oldHp == null) throw new ArgumentException("Old hyperparameters are null.");
        if (newHp == null) throw new ArgumentException("New hyperparameters are null.");

        // Get all public instance properties
        var props = typeof(HyperParameters).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in props)
        {
            var oldVal = prop.GetValue(oldHp);
            var newVal = prop.GetValue(newHp);
            if (!Equals(oldVal, newVal))
            {
                changes[prop.Name] = (oldVal, newVal);
            }
        }
        return changes;
    }

    private static bool IsPinned(Dictionary<string, string>? tags, Dictionary<string, string>? pinnedTags)
    {
        if (pinnedTags == null || tags == null) 
            return false;

        foreach (var pair in pinnedTags)
            if (!tags.TryGetValue(pair.Key, out var val) || val != pair.Value)
                return false;

        return true;
    }
}
