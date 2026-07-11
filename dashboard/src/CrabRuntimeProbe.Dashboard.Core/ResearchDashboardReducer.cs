namespace CrabRuntimeProbe.Dashboard.Core;

public sealed record ResearchActionState(bool Enabled, string DisabledReason)
{
    public static ResearchActionState Allow() => new(true, string.Empty);
    public static ResearchActionState Block(string reason) => new(false, reason);
}

public sealed record ResearchDashboardStatus(
    int TrustedHookCount,
    string TrustedManifestHash,
    string ActiveCanary,
    string ActiveCanaryId,
    string CanaryValidationDepth,
    string SuggestedAction,
    string RegistrationState,
    int CallbackCount,
    string LastCompletedBreadcrumb,
    string CircuitBreakerState,
    string HeartbeatAndSequence,
    string FinalRunClassification,
    string AttributionConfidence,
    string ClassificationReason,
    ResearchActionState StartResearch,
    ResearchActionState RepeatSameTest,
    ResearchActionState PrepareNextDepth,
    ResearchActionState RunCandidateAlone,
    ResearchActionState QuarantineCandidate,
    ResearchActionState ReturnSafePlayGuide,
    bool IsRunActive)
{
    public static ResearchDashboardStatus Empty { get; } = new(
        0, "none", "OnRep_IslandRewardRarity", "hook-crabps-onrep-islandrewardrarity",
        ResearchContracts.DepthDisplay(HookValidationDepth.RegistrationOnly),
        "Complete an island and allow the next reward rarity to update naturally.",
        "Not armed", 0, "None", "Closed / not active", "No heartbeat yet", "Not classified",
        "none", "Prepare a research run to begin.", ResearchActionState.Allow(),
        ResearchActionState.Block("No completed research run."),
        ResearchActionState.Block("A clean natural callback at the current depth is required."),
        ResearchActionState.Block("No completed research run."),
        ResearchActionState.Block("No active candidate."),
        ResearchActionState.Block("No prepared campaign."), false);
}

public sealed class ResearchDashboardReducer
{
    public ResearchDashboardStatus Reduce(
        ResearchRunPlan? plan,
        HookCandidateDefinition? recommendedCandidate,
        HookValidationDepth? recommendedDepth,
        BreadcrumbReadResult? journal,
        HookRunClassification? classification,
        LiveDashboardStatus live,
        SafetyInfo safety,
        HookQuarantineState? quarantine,
        CandidateValidationRecord? candidateRecord,
        bool gameRunning,
        bool hasPreparedCampaign)
    {
        var manifest = plan?.Manifest;
        var candidate = plan?.CanaryCandidate ?? recommendedCandidate;
        var depth = manifest?.Canary?.ValidationDepth ?? recommendedDepth ?? HookValidationDepth.RegistrationOnly;
        var quarantined = candidate is not null && quarantine?.Entries.Any(entry =>
            entry.CandidateId == candidate.Id && entry.State is HookCandidateState.Quarantined or HookCandidateState.CrashSuspect) == true;
        var registration = Registration(journal, candidate?.Id);
        var callbacks = candidate is null || journal is null ? 0 : journal.CallbackCountByCandidate.GetValueOrDefault(candidate.Id);
        var lastCompleted = journal?.LastCompleted is null
            ? "None"
            : $"#{journal.LastCompleted.Sequence} {journal.LastCompleted.Boundary}";
        var breaker = safety.CircuitBreakers
            .Where(pair => pair.Key.Contains("research", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Contains("canary", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Contains("breadcrumb", StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key}: {pair.Value}")
            .DefaultIfEmpty("Closed")
            .Aggregate((left, right) => left + " | " + right);
        var completed = classification is not null;
        var runActive = manifest is not null && !completed && (gameRunning || live.State is
            LiveCollectionState.Warming or LiveCollectionState.Stable or LiveCollectionState.Ready or LiveCollectionState.Collecting);
        var candidateUnavailable = candidate is null ? "No eligible candidate remains." : string.Empty;
        var processReason = gameRunning ? "The next candidate or depth can only be prepared after Crab Champions closes." : string.Empty;
        var repeat = completed && !gameRunning && !quarantined && candidate is not null
            ? ResearchActionState.Allow()
            : ResearchActionState.Block(processReason.Length > 0 ? processReason : quarantined
                ? "Crash-suspect or quarantined candidates require an explicit controlled recovery choice."
                : candidateUnavailable.Length > 0 ? candidateUnavailable : "A completed run is required.");
        var maximumNextDepth = candidateRecord?.TrustedDepth is { } trustedDepth
            ? (HookValidationDepth)Math.Min(7, (int)trustedDepth + 1)
            : (HookValidationDepth)Math.Max(1, (int)(candidateRecord?.HighestValidatedDepth ?? depth));
        var nextDepth = completed && !gameRunning && !quarantined && candidate is not null &&
                        classification!.Outcome == HookRunOutcome.NaturalCallbackClean &&
                        depth < HookValidationDepth.FullPassiveEvidence && maximumNextDepth > depth
            ? ResearchActionState.Allow()
            : ResearchActionState.Block(processReason.Length > 0 ? processReason : classification?.Outcome != HookRunOutcome.NaturalCallbackClean
                ? "Prepare next depth requires a clean natural callback at the current depth."
                : depth >= HookValidationDepth.FullPassiveEvidence ? "Depth 7 is the deepest validation level."
                : maximumNextDepth <= depth ? "Repeat this depth until every promotion threshold is met."
                : quarantined ? "Quarantined candidates cannot advance." : candidateUnavailable);
        var alone = completed && !gameRunning && !quarantined && candidate is not null
            ? ResearchActionState.Allow()
            : ResearchActionState.Block(processReason.Length > 0 ? processReason : quarantined
                ? "Remove quarantine only through an explicit reviewed recovery." : candidateUnavailable.Length > 0
                    ? candidateUnavailable : "A completed run is required.");
        var hasCurrentCanary = manifest?.Canary is not null;
        var quarantineAction = hasCurrentCanary && candidate is not null && !gameRunning && !quarantined
            ? ResearchActionState.Allow()
            : ResearchActionState.Block(gameRunning ? "A running process cannot be reconfigured." : quarantined
                ? "Candidate is already quarantined." : !hasCurrentCanary
                    ? "Arm a cataloged canary before quarantining it." : candidateUnavailable);
        var safe = hasPreparedCampaign && !gameRunning
            ? ResearchActionState.Allow()
            : ResearchActionState.Block(gameRunning ? "Close Crab Champions before rewriting the next-launch profile."
                : "Prepare a campaign first.");
        var start = !gameRunning && candidate is not null && !quarantined
            ? ResearchActionState.Allow()
            : ResearchActionState.Block(gameRunning ? "A new research generation cannot begin in the same game process."
                : quarantined ? "The recommended candidate is quarantined." : candidateUnavailable);
        var manifestHash = manifest is null
            ? "empty"
            : CompatibilityFingerprintService.Sha256Text(string.Join("|", manifest.TrustedCandidates.Select(item =>
                $"{item.CandidateId}@{(int)item.ValidationDepth}@{item.HookPathFingerprint}")));
        return new ResearchDashboardStatus(
            manifest?.TrustedCandidates.Count ?? 0,
            manifestHash,
            candidate?.DisplayName ?? "No eligible candidate",
            candidate?.Id ?? string.Empty,
            ResearchContracts.DepthDisplay(depth),
            candidate?.SuggestedAction ?? "Return to safe Play Guide; no eligible automatic canary remains.",
            registration,
            callbacks,
            lastCompleted,
            breaker,
            live.State == LiveCollectionState.GameUnavailable ? "No heartbeat yet" : $"{live.HeartbeatAgeText} · {live.SequenceText}",
            classification is null ? runActive ? "Run active" : "Not classified" : ClassificationDisplay(classification),
            classification?.Confidence.ToString().ToLowerInvariant() ?? "none",
            classification?.Reason ?? (plan?.IsValid == false ? string.Join("; ", plan.Errors) : "No completed run classification."),
            start, repeat, nextDepth, alone, quarantineAction, safe, runActive);
    }

    private static string Registration(BreadcrumbReadResult? journal, string? candidateId)
    {
        if (journal is null || string.IsNullOrWhiteSpace(candidateId)) return "Not armed";
        var rows = journal.Records.Where(record => record.CandidateId == candidateId).ToArray();
        if (rows.Any(record => record.Boundary == "registration-failed")) return "Failed";
        if (rows.Any(record => record.Boundary == "registration-complete")) return "Registered";
        if (rows.Any(record => record.Boundary == "registration-begin")) return "Registering / interrupted";
        return journal.HasFatalIssue ? "Journal faulted" : "Waiting for registration";
    }

    private static string ClassificationDisplay(HookRunClassification value) =>
        $"{value.Classification.ToString().Replace("PostCallback", " post-callback ")} · {value.Outcome}";
}
