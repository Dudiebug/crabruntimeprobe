using System.Collections.ObjectModel;

namespace CrabRuntimeProbe.Dashboard.Core;

/// <summary>
/// Deterministically replays passive snapshot JSONL into checklist-compatible evidence.
/// It never invokes hooks, RPCs, writes, or game APIs.
/// </summary>
public sealed class SnapshotEvidenceReducer
{
    private readonly SnapshotJsonlReader _reader;
    private readonly IReadOnlyList<SnapshotQualificationRule> _rules;

    public SnapshotEvidenceReducer(
        IReadOnlyList<SnapshotQualificationRule>? rules = null,
        SnapshotJsonlReader? reader = null)
    {
        _rules = rules is { Count: > 0 }
            ? rules
            : SnapshotQualificationRuleCatalog.PlayerFacing;
        _reader = reader ?? new SnapshotJsonlReader();
        ValidateRules(_rules);
    }

    public async Task<SnapshotReplayResult> ReplayFileAsync(
        string path,
        SnapshotReplayScope scope,
        CancellationToken cancellationToken = default) =>
        Replay(await _reader.ReadAsync(path, cancellationToken).ConfigureAwait(false), scope);

    public SnapshotReplayResult ReplayJsonl(string jsonl, SnapshotReplayScope scope) =>
        Replay(_reader.Read(jsonl), scope);

    public SnapshotReplayResult Replay(SnapshotJsonlReadResult input, SnapshotReplayScope scope)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReplayScope(scope);

        var rejections = input.Rejections
            .Select(item => new SnapshotEvidenceRejection(
                null,
                item.Code,
                $"Line {item.LineNumber}: {item.Detail}"))
            .ToList();
        var qualifications = new List<SnapshotQualification>();
        var states = _rules.ToDictionary(
            rule => rule.Id,
            _ => new RuleState(),
            StringComparer.OrdinalIgnoreCase);
        long lastSequence = 0;
        var acceptedRows = 0;

        foreach (var observation in input.Observations)
        {
            var rowError = ValidateObservation(observation, scope, lastSequence);
            if (rowError is not null)
            {
                rejections.Add(rowError);
                if (ShouldBreakStableRuns(rowError.Code, observation, scope))
                {
                    BreakNonLifecycleRuns(states);
                }
                continue;
            }

            lastSequence = observation.Sequence;
            if (!observation.Stability.IsFullyStable)
            {
                rejections.Add(new SnapshotEvidenceRejection(
                    observation.Sequence,
                    "unstable-row",
                    "Unstable snapshots cannot contribute checklist evidence."));
                BreakNonLifecycleRuns(states);
                continue;
            }

            acceptedRows++;
            foreach (var rule in _rules)
            {
                if (!Matches(rule.Categories, observation.Category)) continue;
                if (!TryField(observation, rule.FieldPaths, out var fieldPath, out var value)) continue;
                ProcessRule(
                    rule,
                    states[rule.Id],
                    observation,
                    fieldPath,
                    value,
                    qualifications,
                    rejections);
            }
        }

        // Fail closed for the complete replay scope. Dropping a malformed, unsafe, or
        // mismatched row and joining the snapshots on either side could invent a delta.
        if (rejections.Count > 0)
        {
            return new SnapshotReplayResult(
                SnapshotReplayResult.Empty.Checklist,
                Array.Empty<SnapshotQualification>(),
                rejections.ToArray(),
                input.NonEmptyLineCount,
                acceptedRows);
        }

        return new SnapshotReplayResult(
            BuildChecklistEvidence(_rules, states, qualifications),
            qualifications.ToArray(),
            Array.Empty<SnapshotEvidenceRejection>(),
            input.NonEmptyLineCount,
            acceptedRows);
    }

    private static void ProcessRule(
        SnapshotQualificationRule rule,
        RuleState state,
        SnapshotObservation observation,
        string fieldPath,
        SnapshotValue value,
        ICollection<SnapshotQualification> qualifications,
        ICollection<SnapshotEvidenceRejection> rejections)
    {
        if (state.Current is null)
        {
            if (MatchesContext(rule.BeforeContexts, observation.Context))
            {
                state.Current = StableRun.Start(observation, fieldPath, value);
            }
            return;
        }

        var scopeMismatch = ScopeMismatch(rule.Scope, state.Current.Last, observation);
        if (scopeMismatch is not null)
        {
            // Travel, respawn, and reconnect naturally replace world/PlayerState scope.
            // End the pending run without joining values across that boundary.
            state.Reset();
            if (MatchesContext(rule.BeforeContexts, observation.Context))
            {
                state.Current = StableRun.Start(observation, fieldPath, value);
            }
            return;
        }

        if (ValuesEqual(state.Current.Value, value))
        {
            state.Current.Add(observation, fieldPath);
        }
        else
        {
            if (state.Current.Count >= rule.StableBeforeSamples
                && MatchesContext(rule.BeforeContexts, state.Current.Last.Context))
            {
                state.Before = state.Current;
            }

            state.Current = StableRun.Start(observation, fieldPath, value);
        }

        if (state.Before is null || state.Current.Count < rule.StableAfterSamples) return;

        var pairMismatch = ScopeMismatch(rule.Scope, state.Before.Last, state.Current.Last);
        if (pairMismatch is not null)
        {
            // A scoped rule may never compare across lifecycle identity changes. This is
            // an expected run boundary, not corrupt evidence for unrelated categories.
            state.Before = null;
            return;
        }

        if (!MatchesContext(rule.BeforeContexts, state.Before.Last.Context)
            || !MatchesContext(rule.AfterContexts, state.Current.Last.Context))
        {
            state.Before = null;
            return;
        }

        if (!DeltaMatches(rule.Operator, state.Before.Value, state.Current.Value, out var deltaError))
        {
            if (!string.IsNullOrEmpty(deltaError))
            {
                rejections.Add(new SnapshotEvidenceRejection(
                    observation.Sequence,
                    "incompatible-delta",
                    $"Rule {rule.Id} rejected the delta: {deltaError}",
                    rule.Id));
            }
            state.Before = null;
            return;
        }

        var detail = $"{rule.EvidenceDescription} "
                     + $"{state.Before.Value.Canonical} -> {state.Current.Value.Canonical}.";
        qualifications.Add(new SnapshotQualification(
            rule.Id,
            rule.ChecklistIds,
            observation.SessionId,
            observation.CampaignGeneration,
            state.Before.Last.Sequence,
            state.Current.Last.Sequence,
            state.Before.Last.TimestampUtc,
            state.Current.Last.TimestampUtc,
            observation.Category,
            state.Current.FieldPath,
            state.Before.Value,
            state.Current.Value,
            observation.ObservedRole,
            detail));
        state.Before = null;
    }

    private static IReadOnlyDictionary<string, ChecklistEvidence> BuildChecklistEvidence(
        IReadOnlyList<SnapshotQualificationRule> rules,
        IReadOnlyDictionary<string, RuleState> states,
        IReadOnlyList<SnapshotQualification> qualifications)
    {
        var checklistIds = rules.SelectMany(rule => rule.ChecklistIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        var output = new Dictionary<string, ChecklistEvidence>(StringComparer.OrdinalIgnoreCase);

        foreach (var checklistId in checklistIds)
        {
            var matches = qualifications
                .Where(item => item.ChecklistIds.Contains(checklistId, StringComparer.OrdinalIgnoreCase))
                .OrderBy(item => item.AfterSequence)
                .ToArray();
            if (matches.Length > 0)
            {
                output[checklistId] = new ChecklistEvidence(
                    checklistId,
                    "confirmed",
                    matches.Length,
                    matches.Min(item => item.AfterTimestampUtc),
                    matches.Max(item => item.AfterTimestampUtc),
                    matches.Select(item => item.ObservedRole)
                        .Where(role => !string.IsNullOrWhiteSpace(role))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    matches.Select(item => item.SessionId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray(),
                    ["snapshot-state-delta", "natural-property-change", "hook-free-observation"],
                    HookRegistered: false,
                    QualifyingEvidence: true,
                    DirtyEvidence: false,
                    CrashSuspect: false,
                    NextInstruction: string.Empty,
                    Detail: matches[^1].Detail);
                continue;
            }

            var baselines = rules
                .Where(rule => rule.ChecklistIds.Contains(checklistId, StringComparer.OrdinalIgnoreCase))
                .Select(rule => (Rule: rule, State: states[rule.Id]))
                .Where(item => item.State.HasBaseline(item.Rule.StableBeforeSamples))
                .ToArray();
            if (baselines.Length == 0) continue;
            var first = baselines.Min(item => item.State.BaselineTimestamp);
            var last = baselines.Max(item => item.State.BaselineTimestamp);
            var roles = baselines.Select(item => item.State.BaselineRole)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sessions = baselines.Select(item => item.State.BaselineSession)
                .Where(session => !string.IsNullOrWhiteSpace(session))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(session => session, StringComparer.Ordinal)
                .ToArray();
            output[checklistId] = new ChecklistEvidence(
                checklistId,
                "observing",
                0,
                first,
                last,
                roles,
                sessions,
                ["snapshot-baseline", "hook-free-observation"],
                HookRegistered: false,
                QualifyingEvidence: false,
                DirtyEvidence: false,
                CrashSuspect: false,
                NextInstruction: string.Empty,
                Detail: "Stable snapshot baseline captured; waiting for a qualifying natural change.");
        }

        return new ReadOnlyDictionary<string, ChecklistEvidence>(output);
    }

    private static SnapshotEvidenceRejection? ValidateObservation(
        SnapshotObservation observation,
        SnapshotReplayScope scope,
        long lastSequence)
    {
        if (!observation.SessionId.Equals(scope.SessionId, StringComparison.Ordinal))
            return Reject(observation, "session-mismatch", "Snapshot session does not match the active campaign session.");
        if (observation.CampaignGeneration != scope.CampaignGeneration)
            return Reject(observation, "generation-mismatch", "Snapshot campaign generation does not match.");
        if (!string.IsNullOrWhiteSpace(scope.CampaignId)
            && !observation.CampaignId.Equals(scope.CampaignId, StringComparison.Ordinal))
            return Reject(observation, "campaign-mismatch", "Snapshot campaign id does not match.");
        if (!string.IsNullOrWhiteSpace(scope.MachineId)
            && !observation.MachineId.Equals(scope.MachineId, StringComparison.Ordinal))
            return Reject(observation, "machine-mismatch", "Snapshot machine id does not match.");
        if (scope.SelectedRole != CampaignRole.Unknown && observation.SelectedRole != scope.SelectedRole)
            return Reject(observation, "role-mismatch", "Snapshot selected role does not match.");
        if (!string.IsNullOrWhiteSpace(scope.ObservedRole)
            && !Normalize(observation.ObservedRole).Equals(Normalize(scope.ObservedRole), StringComparison.Ordinal))
            return Reject(observation, "observed-role-mismatch", "Snapshot observed role does not match.");
        if (observation.DirtyEvidence || observation.CrashSuspected)
            return Reject(observation, "dirty-row", "Dirty or crash-suspect snapshots cannot contribute evidence.");
        if (!observation.Safety.IsReadOnlyAndHookFree)
            return Reject(observation, "unsafe-row", "Snapshot does not prove hook-free, read-only safety flags.");
        if (string.IsNullOrWhiteSpace(observation.WorldFingerprint)
            || string.IsNullOrWhiteSpace(observation.PlayerStateFingerprint))
            return Reject(observation, "missing-stable-scope", "Stable snapshots require world and PlayerState fingerprints.");
        if (observation.Sequence <= lastSequence)
            return Reject(observation, "non-monotonic-sequence", "Snapshot sequence is duplicated or out of order.");
        return null;
    }

    private static SnapshotEvidenceRejection Reject(
        SnapshotObservation observation,
        string code,
        string detail) => new(observation.Sequence, code, detail);

    private static bool ShouldBreakStableRuns(
        string code,
        SnapshotObservation observation,
        SnapshotReplayScope scope)
    {
        if (!observation.SessionId.Equals(scope.SessionId, StringComparison.Ordinal)
            || observation.CampaignGeneration != scope.CampaignGeneration)
            return false;
        return code is "dirty-row" or "unsafe-row" or "non-monotonic-sequence";
    }

    private void BreakNonLifecycleRuns(IReadOnlyDictionary<string, RuleState> states)
    {
        foreach (var pair in states)
        {
            var rule = _rules
                .FirstOrDefault(item => item.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (rule is null || !rule.Scope.AllowUnstableBridge) pair.Value.Reset();
        }
    }

    private static bool TryField(
        SnapshotObservation observation,
        IReadOnlyList<string> aliases,
        out string path,
        out SnapshotValue value)
    {
        foreach (var alias in aliases)
        {
            if (observation.Fields.TryGetValue(alias, out var field) && field.IsObserved)
            {
                path = alias;
                value = field.Value!;
                return true;
            }
        }

        path = string.Empty;
        value = new SnapshotValue(SnapshotValueKind.Null, "null");
        return false;
    }

    private static bool DeltaMatches(
        SnapshotDeltaOperator deltaOperator,
        SnapshotValue before,
        SnapshotValue after,
        out string error)
    {
        error = string.Empty;
        if (deltaOperator == SnapshotDeltaOperator.Changed) return !ValuesEqual(before, after);
        if (before.Number is null || after.Number is null)
        {
            error = "increased/decreased rules require numeric values.";
            return false;
        }

        return deltaOperator == SnapshotDeltaOperator.Increased
            ? after.Number.Value > before.Number.Value
            : after.Number.Value < before.Number.Value;
    }

    private static bool ValuesEqual(SnapshotValue left, SnapshotValue right) =>
        left.Kind == right.Kind && left.Canonical.Equals(right.Canonical, StringComparison.Ordinal);

    private static string? ScopeMismatch(
        SnapshotRuleScopePolicy policy,
        SnapshotObservation before,
        SnapshotObservation after)
    {
        if (policy.SameLifecycleGeneration && before.LifecycleGeneration != after.LifecycleGeneration)
            return "lifecycle generation";
        if (policy.SameWorldFingerprint
            && !before.WorldFingerprint.Equals(after.WorldFingerprint, StringComparison.Ordinal))
            return "world scope";
        if (policy.SamePlayerStateFingerprint
            && !before.PlayerStateFingerprint.Equals(after.PlayerStateFingerprint, StringComparison.Ordinal))
            return "PlayerState scope";
        if (policy.SameContext && !Normalize(before.Context).Equals(Normalize(after.Context), StringComparison.Ordinal))
            return "game context";
        if (policy.SameObservedRole
            && !Normalize(before.ObservedRole).Equals(Normalize(after.ObservedRole), StringComparison.Ordinal))
            return "observed role";
        return null;
    }

    private static bool Matches(IReadOnlyList<string> candidates, string actual) =>
        candidates.Any(candidate => Normalize(candidate).Equals(Normalize(actual), StringComparison.Ordinal));

    private static bool MatchesContext(IReadOnlyList<string> candidates, string actual) =>
        candidates.Count == 0 || Matches(candidates, actual);

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

    private static void ValidateReplayScope(SnapshotReplayScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(scope.SessionId))
            throw new ArgumentException("Replay scope requires a session id.", nameof(scope));
        if (scope.CampaignGeneration < 1)
            throw new ArgumentException("Replay scope requires a positive campaign generation.", nameof(scope));
    }

    private static void ValidateRules(IReadOnlyList<SnapshotQualificationRule> rules)
    {
        var duplicate = rules.GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate snapshot rule id: {duplicate.Key}");
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.ChecklistIds.Count == 0
                || rule.Categories.Count == 0 || rule.FieldPaths.Count == 0
                || rule.StableBeforeSamples < 1 || rule.StableAfterSamples < 1)
                throw new ArgumentException($"Snapshot rule {rule.Id} is incomplete.");
        }
    }

    private sealed class RuleState
    {
        public StableRun? Before { get; set; }
        public StableRun? Current { get; set; }

        public DateTimeOffset? BaselineTimestamp => (Before ?? Current)?.Last.TimestampUtc;
        public string BaselineRole => (Before ?? Current)?.Last.ObservedRole ?? string.Empty;
        public string BaselineSession => (Before ?? Current)?.Last.SessionId ?? string.Empty;

        public bool HasBaseline(int requiredSamples) =>
            Before is not null || Current is { Count: var count } && count >= requiredSamples;

        public void Reset()
        {
            Before = null;
            Current = null;
        }
    }

    private sealed class StableRun
    {
        private StableRun(SnapshotObservation observation, string fieldPath, SnapshotValue value)
        {
            First = observation;
            Last = observation;
            FieldPath = fieldPath;
            Value = value;
            Count = 1;
        }

        public SnapshotObservation First { get; }
        public SnapshotObservation Last { get; private set; }
        public string FieldPath { get; private set; }
        public SnapshotValue Value { get; }
        public int Count { get; private set; }

        public static StableRun Start(
            SnapshotObservation observation,
            string fieldPath,
            SnapshotValue value) => new(observation, fieldPath, value);

        public void Add(SnapshotObservation observation, string fieldPath)
        {
            Last = observation;
            FieldPath = fieldPath;
            Count++;
        }
    }
}
