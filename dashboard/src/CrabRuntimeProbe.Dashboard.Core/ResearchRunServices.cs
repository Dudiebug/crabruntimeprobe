namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class ResearchRunPlanner
{
    public ResearchRunPlan CreatePlan(
        HookCandidateCatalog catalog,
        CompatibilityFingerprint compatibility,
        TrustedHookManifest trustedManifest,
        HookValidationLedger ledger,
        HookQuarantineState quarantine,
        ResearchRunType runType,
        string? canaryCandidateId,
        HookValidationDepth? canaryDepth,
        CampaignRole selectedRole,
        long campaignGeneration,
        string runId,
        string sessionId)
    {
        var errors = new List<string>();
        ValidateIdentity(runId, nameof(runId), errors);
        ValidateIdentity(sessionId, nameof(sessionId), errors);
        if (campaignGeneration < 1) errors.Add("Campaign generation must be positive.");
        if (selectedRole is not (CampaignRole.Host or CampaignRole.JoinedClient))
            errors.Add("Research runs require an explicit host or joined-client role.");
        ValidateCompatibility(catalog, compatibility, errors);
        if (ledger.SchemaVersion != ResearchContracts.LedgerSchema ||
            ledger.HookCatalogIdentity != catalog.HookCatalogIdentity ||
            ledger.CoverageCatalogHash != catalog.CoverageCatalogHash)
            errors.Add("Validation ledger identity is incompatible with the candidate catalog.");
        if (quarantine.SchemaVersion != ResearchContracts.QuarantineSchema ||
            quarantine.HookCatalogIdentity != catalog.HookCatalogIdentity)
            errors.Add("Quarantine state is missing or incompatible.");

        var ledgerById = UniqueById(ledger.Candidates, item => item.CandidateId, "ledger", errors);
        var quarantineIds = quarantine.Entries
            .Where(entry => entry.State is HookCandidateState.Quarantined or HookCandidateState.CrashSuspect)
            .Select(entry => entry.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        var requiresCanary = runType is ResearchRunType.CanaryOnly or ResearchRunType.Combined;

        var trustedSelections = new List<(HookCandidateDefinition Candidate, HookCandidateSelection Selection)>();
        if (runType is ResearchRunType.TrustedPoolOnly or ResearchRunType.Combined)
        {
            ValidateTrustedManifest(catalog, compatibility, trustedManifest, errors);
            var seenTrusted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in trustedManifest.Candidates)
            {
                if (!seenTrusted.Add(entry.CandidateId))
                {
                    errors.Add($"Trusted manifest duplicates candidate '{entry.CandidateId}'.");
                    continue;
                }
                if (!catalog.ById.TryGetValue(entry.CandidateId, out var candidate))
                {
                    errors.Add($"Trusted manifest references unknown candidate '{entry.CandidateId}'.");
                    continue;
                }
                if (!string.Equals(candidate.HookPathFingerprint, entry.HookPathFingerprint, StringComparison.Ordinal) ||
                    !string.Equals(entry.CompatibilityFingerprint, compatibility.Fingerprint, StringComparison.Ordinal))
                {
                    errors.Add($"Trusted candidate '{entry.CandidateId}' needs revalidation for this compatibility fingerprint.");
                    continue;
                }
                if (entry.TrustedDepth is <= HookValidationDepth.StaticCatalogValidation or > HookValidationDepth.FullPassiveEvidence ||
                    entry.TrustedDepth > candidate.MaximumValidationDepth)
                {
                    errors.Add($"Trusted candidate '{entry.CandidateId}' has an invalid trusted depth.");
                    continue;
                }
                if (!ledgerById.TryGetValue(entry.CandidateId, out var record) ||
                    record.State != HookCandidateState.Trusted || record.TrustedDepth is null ||
                    record.TrustedDepth < entry.TrustedDepth ||
                    record.HighestValidatedDepth < entry.TrustedDepth ||
                    record.CompatibilityFingerprint != compatibility.Fingerprint ||
                    record.HasUnmatchedBreadcrumb || record.HasCorrelatedCrash ||
                    record.CrashSuspectRuns.Count > 0 || record.HasNewUe4ssCallbackError ||
                    !record.ReducerFixtureCovered)
                {
                    errors.Add($"Trusted candidate '{entry.CandidateId}' is not trusted in the validation ledger at that depth.");
                    continue;
                }
                // Counters describe the current highest depth. Once a clean deeper canary has
                // reset them, TrustedDepth remains the durable proof that its shallower threshold
                // already passed; only compatibility and safety invalidators can remove it.
                if (record.HighestValidatedDepth == entry.TrustedDepth)
                {
                    var promotion = PromotionPolicy.Evaluate(record, entry.TrustedDepth, compatibility.Fingerprint, true);
                    if (!promotion.Eligible)
                    {
                        errors.Add($"Trusted candidate '{entry.CandidateId}' no longer meets promotion policy: {string.Join("; ", promotion.UnmetRequirements)}");
                        continue;
                    }
                }
                if (quarantineIds.Contains(entry.CandidateId))
                {
                    errors.Add($"Trusted candidate '{entry.CandidateId}' is quarantined or crash-suspect.");
                    continue;
                }
                // A trusted candidate being validated one depth deeper is registered once as the
                // canary. That single deeper callback includes its already-trusted shallower behavior.
                if (requiresCanary && entry.CandidateId == canaryCandidateId) continue;
                trustedSelections.Add((candidate,
                    new HookCandidateSelection(candidate.Id, candidate.HookPathFingerprint, entry.TrustedDepth)));
            }
        }

        HookCandidateDefinition? canaryCandidate = null;
        HookCandidateSelection? canary = null;
        if (requiresCanary)
        {
            if (string.IsNullOrWhiteSpace(canaryCandidateId) || canaryDepth is null)
            {
                errors.Add("This run type requires exactly one cataloged canary and one validation depth.");
            }
            else if (!catalog.ById.TryGetValue(canaryCandidateId, out canaryCandidate))
            {
                errors.Add($"Unknown canary candidate '{canaryCandidateId}'.");
            }
            else
            {
                var depth = canaryDepth.Value;
                if (depth is <= HookValidationDepth.StaticCatalogValidation or > HookValidationDepth.FullPassiveEvidence ||
                    depth > canaryCandidate.MaximumValidationDepth)
                    errors.Add("Canary depth is outside the candidate's validated depth ladder.");
                if (quarantineIds.Contains(canaryCandidate.Id))
                    errors.Add("Quarantined or crash-suspect candidates cannot auto-arm.");
                if (!ledgerById.TryGetValue(canaryCandidate.Id, out var record))
                {
                    if (depth != HookValidationDepth.RegistrationOnly)
                        errors.Add("An unrecorded candidate must begin at registration-only depth.");
                }
                else
                {
                    if (record.State is HookCandidateState.Quarantined or HookCandidateState.CrashSuspect or
                        HookCandidateState.Unsupported or HookCandidateState.NeedsRevalidation)
                        errors.Add($"Candidate state '{record.State}' cannot be armed automatically.");
                    var maximumNextDepth = (HookValidationDepth)Math.Min(7,
                        record.TrustedDepth is null
                            ? Math.Max(1, (int)record.HighestValidatedDepth)
                            : (int)record.TrustedDepth.Value + 1);
                    if (depth > maximumNextDepth)
                        errors.Add($"Canary cannot skip its next untrusted depth; maximum is Depth {(int)maximumNextDepth}.");
                    if (record.TrustedDepth is not null && record.TrustedDepth >= depth)
                        errors.Add("The selected depth is already trusted and is not an unvalidated canary depth.");
                }
                canary = new HookCandidateSelection(canaryCandidate.Id, canaryCandidate.HookPathFingerprint, depth);
            }
        }
        else if (!string.IsNullOrWhiteSpace(canaryCandidateId) || canaryDepth is not null)
        {
            errors.Add("Trusted-pool-only runs must not contain a canary.");
        }

        var orderedTrusted = trustedSelections
            .OrderBy(item => item.Candidate.OwnerKind == "blueprint" ? 1 : 0)
            .ThenBy(item => item.Candidate.Priority)
            .ThenBy(item => item.Candidate.Id, StringComparer.Ordinal)
            .ToArray();
        var registrationOrder = new List<string> { "safe-snapshot-baseline" };
        registrationOrder.AddRange(orderedTrusted.Select(item => item.Candidate.Id));
        if (canary is not null) registrationOrder.Add(canary.CandidateId);
        if (canary is not null && registrationOrder[^1] != canary.CandidateId)
            errors.Add("Canary registration must be last.");
        if (errors.Count > 0)
            return new ResearchRunPlan(false, null, errors, canaryCandidate, "Research run rejected; configuration failed closed.");

        var manifest = new HookRunManifest(
            ResearchContracts.RunManifestSchema, runId, sessionId, campaignGeneration, DateTimeOffset.UtcNow,
            runType, selectedRole, compatibility, true, orderedTrusted.Select(item => item.Selection).ToArray(),
            canary, registrationOrder, false);
        return new ResearchRunPlan(true, manifest, Array.Empty<string>(), canaryCandidate,
            $"Safe snapshot baseline + {manifest.TrustedCandidates.Count} trusted hook(s)" +
            (canary is null ? " + no canary" : $" + canary {canaryCandidate!.DisplayName} at Depth {(int)canary.ValidationDepth}"));
    }

    private static void ValidateCompatibility(
        HookCandidateCatalog catalog,
        CompatibilityFingerprint compatibility,
        ICollection<string> errors)
    {
        if (!compatibility.IsComplete) errors.Add("Game/UE4SS compatibility inputs are incomplete.");
        if (compatibility.CoverageCatalogHash != catalog.CoverageCatalogHash ||
            compatibility.HookCatalogIdentity != catalog.HookCatalogIdentity ||
            compatibility.CallbackImplementationVersion != catalog.CallbackImplementationVersion ||
            compatibility.CallbackSchemaVersion != catalog.CallbackSchemaVersion ||
            compatibility.ValidationBehaviorVersion != catalog.ValidationBehaviorVersion)
            errors.Add("Compatibility fingerprint components do not match the current hook catalog and callback behavior.");
        var expected = new CompatibilityFingerprintService().Compute(
            compatibility.GameBuild, compatibility.Ue4ssVersion, catalog, compatibility.ComputedAtUtc).Fingerprint;
        if (!string.Equals(expected, compatibility.Fingerprint, StringComparison.Ordinal))
            errors.Add("Compatibility fingerprint hash is invalid.");
    }

    private static void ValidateTrustedManifest(
        HookCandidateCatalog catalog,
        CompatibilityFingerprint compatibility,
        TrustedHookManifest manifest,
        ICollection<string> errors)
    {
        if (manifest.SchemaVersion != ResearchContracts.TrustedManifestSchema ||
            manifest.CoverageCatalogHash != catalog.CoverageCatalogHash ||
            manifest.HookCatalogIdentity != catalog.HookCatalogIdentity ||
            manifest.CallbackImplementationVersion != catalog.CallbackImplementationVersion ||
            manifest.CallbackSchemaVersion != catalog.CallbackSchemaVersion ||
            manifest.ValidationBehaviorVersion != catalog.ValidationBehaviorVersion)
            errors.Add("Trusted manifest identity is incompatible with the current runtime.");
        if (manifest.Candidates.Count > 0 && manifest.CompatibilityFingerprint != compatibility.Fingerprint)
            errors.Add("Trusted manifest compatibility changed; affected entries need revalidation.");
    }

    private static Dictionary<string, T> UniqueById<T>(
        IEnumerable<T> values,
        Func<T, string> id,
        string label,
        ICollection<string> errors)
    {
        var output = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var key = id(value);
            if (!output.TryAdd(key, value)) errors.Add($"The {label} duplicates candidate '{key}'.");
        }
        return output;
    }

    private static void ValidateIdentity(string value, string label, ICollection<string> errors)
    {
        if (value.Length is < 8 or > 128 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            errors.Add($"{label} must be an opaque bounded identifier.");
    }
}

public sealed class HookRunClassifier
{
    public HookRunClassification Classify(
        HookRunManifest manifest,
        BreadcrumbReadResult journal,
        RunObservationSignals signals,
        DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var evidence = journal.Issues.Select(issue => issue.Code).Distinct(StringComparer.Ordinal).ToList();
        var zeroHookControl = manifest.RunType == ResearchRunType.TrustedPoolOnly
                              && manifest.TrustedCandidates.Count == 0 && manifest.Canary is null
                              && journal.Records.Count == 0;
        var manifestMismatch = ValidateJournalIdentity(manifest, journal);
        if (manifestMismatch is not null)
        {
            evidence.Add("journal-manifest-mismatch");
            return Result(HookRunClassificationKind.EvidenceFailure, HookRunOutcome.Incomplete,
                AttributionConfidence.None, null, null, manifestMismatch,
                ResearchRecommendation.ReturnSafePlayGuide, journal, manifest, now, evidence);
        }
        if (journal.HasFatalIssue && !zeroHookControl)
            return Result(HookRunClassificationKind.EvidenceFailure, HookRunOutcome.Incomplete,
                AttributionConfidence.None, null, null,
                "The breadcrumb journal contains a fatal malformed, duplicate, stale-generation, or unknown record; candidate attribution is unsafe.",
                ResearchRecommendation.ReturnSafePlayGuide, journal, manifest, now, evidence);

        var registrationFailure = journal.Records.LastOrDefault(record => record.Boundary == "registration-failed");
        if (registrationFailure is not null)
            return Result(HookRunClassificationKind.RegistrationFailure, HookRunOutcome.CrashSuspect,
                signals.CrashArtifactCorrelated || signals.AbnormalProcessExit ? AttributionConfidence.High : AttributionConfidence.Medium,
                registrationFailure.CandidateId, registrationFailure.ValidationDepth,
                "Hook registration reported failure at an exact, journaled candidate boundary.",
                ResearchRecommendation.ReturnSafePlayGuide, journal, manifest, now, evidence);

        if (journal.LastUnmatched is { } unmatched)
        {
            var registration = unmatched.Boundary == "registration-begin";
            var confidence = signals.CrashArtifactCorrelated || signals.AbnormalProcessExit
                ? AttributionConfidence.High : AttributionConfidence.Medium;
            evidence.Add($"unmatched:{unmatched.Boundary}");
            return Result(
                registration ? HookRunClassificationKind.RegistrationFailure : HookRunClassificationKind.CallbackBoundaryFailure,
                HookRunOutcome.CrashSuspect, confidence, unmatched.CandidateId, unmatched.ValidationDepth,
                $"The last justified unmatched boundary is '{unmatched.Boundary}' for one exact candidate invocation. This is boundary attribution, not proof of sole causation.",
                unmatched.CandidateRole == "canary" ? ResearchRecommendation.CanaryAlone : ResearchRecommendation.TrustedPoolControl,
                journal, manifest, now, evidence);
        }

        if (signals.EvidenceWriteFailed || signals.StatusFaulted)
            return Result(HookRunClassificationKind.EvidenceFailure, HookRunOutcome.Incomplete,
                AttributionConfidence.None, null, null, "Evidence processing or a required runtime circuit failed after all recoverable boundaries.",
                ResearchRecommendation.ReturnSafePlayGuide, journal, manifest, now, evidence);
        if (signals.WriterStale)
            return Result(HookRunClassificationKind.StaleWriter, HookRunOutcome.Incomplete,
                AttributionConfidence.None, null, null, "The runtime writer heartbeat became stale; healthy collection cannot be inferred.",
                ResearchRecommendation.ReturnSafePlayGuide, journal, manifest, now, evidence);

        if (signals.AbnormalProcessExit || signals.CrashArtifactCorrelated || signals.Ue4ssCallbackErrors > 0)
        {
            evidence.Add("all-enabled-boundaries-completed");
            return Result(HookRunClassificationKind.UnattributedPostCallbackCrash, HookRunOutcome.Unattributed,
                AttributionConfidence.None, null, null,
                "Every enabled callback boundary completed before the later failure. The final hook is not blamed; causation remains unattributed.",
                ControlRecommendation(manifest.RunType, signals.InteractionPlausible), journal, manifest, now, evidence);
        }
        if (signals.ExternalTermination)
            return Result(HookRunClassificationKind.ExternalTermination, HookRunOutcome.Incomplete,
                AttributionConfidence.None, null, null, "The process was externally terminated after all recorded boundaries completed.",
                ResearchRecommendation.RepeatSameTest, journal, manifest, now, evidence);
        if (!signals.CleanShutdown)
            return Result(HookRunClassificationKind.InterruptedRun, HookRunOutcome.Incomplete,
                AttributionConfidence.Low, null, null, "The run ended without a clean shutdown signal or an attributable unmatched boundary.",
                ResearchRecommendation.RepeatSameTest, journal, manifest, now, evidence);

        var canary = manifest.Canary;
        if (canary is null)
            return Result(HookRunClassificationKind.CleanShutdown, HookRunOutcome.RegistrationClean,
                AttributionConfidence.High, null, null, "Trusted-pool control completed cleanly.",
                ResearchRecommendation.Combined, journal, manifest, now, evidence);
        var registered = journal.Records.Any(record => record.CandidateId == canary.CandidateId && record.Boundary == "registration-complete");
        var callbackCount = journal.CallbackCountByCandidate.GetValueOrDefault(canary.CandidateId);
        if (!registered)
            return Result(HookRunClassificationKind.InterruptedRun, HookRunOutcome.Incomplete,
                AttributionConfidence.Low, canary.CandidateId, canary.ValidationDepth,
                "The clean process signal did not include a completed canary registration boundary.",
                ResearchRecommendation.RepeatSameTest, journal, manifest, now, evidence);
        if (callbackCount == 0)
            return Result(HookRunClassificationKind.CleanShutdown, HookRunOutcome.RegisteredNotNaturallyObserved,
                AttributionConfidence.High, canary.CandidateId, canary.ValidationDepth,
                "The canary registered cleanly but no natural callback occurred. Registration alone does not promote trust.",
                ResearchRecommendation.RepeatSameTest, journal, manifest, now, evidence);
        return Result(HookRunClassificationKind.CleanShutdown, HookRunOutcome.NaturalCallbackClean,
            AttributionConfidence.High, canary.CandidateId, canary.ValidationDepth,
            $"The canary completed {callbackCount} matched natural callback invocation(s) at its configured depth.",
            ResearchRecommendation.RepeatSameTest, journal, manifest, now, evidence);
    }

    private static string? ValidateJournalIdentity(
        HookRunManifest manifest,
        BreadcrumbReadResult journal)
    {
        var expected = new Dictionary<string, (HookCandidateSelection Selection, string Role)>(StringComparer.Ordinal);
        foreach (var selection in manifest.TrustedCandidates)
            if (!expected.TryAdd(selection.CandidateId, (selection, "trusted")))
                return "The run manifest duplicates a trusted candidate; journal attribution is unsafe.";
        if (manifest.Canary is { } canary && !expected.TryAdd(canary.CandidateId, (canary, "canary")))
            return "The run manifest assigns one candidate to both trusted and canary roles.";

        foreach (var record in journal.Records)
        {
            if (!expected.TryGetValue(record.CandidateId, out var contract)
                || contract.Selection.HookPathFingerprint != record.HookPathFingerprint
                || contract.Selection.ValidationDepth != record.ValidationDepth
                || contract.Role != record.CandidateRole)
                return "A breadcrumb candidate, path fingerprint, depth, or role does not match the immutable run manifest.";
        }

        var expectedOrder = manifest.RegistrationOrder
            .Where(candidateId => candidateId != "safe-snapshot-baseline")
            .ToArray();
        var observedOrder = journal.Records
            .Where(record => record.Boundary == "registration-begin")
            .Select(record => record.CandidateId)
            .ToArray();
        if (observedOrder.Distinct(StringComparer.Ordinal).Count() != observedOrder.Length
            || observedOrder.Length > expectedOrder.Length
            || !observedOrder.SequenceEqual(expectedOrder.Take(observedOrder.Length), StringComparer.Ordinal))
            return "Journaled registration begins do not follow the manifest's deterministic trusted-then-canary order.";
        return null;
    }

    private static ResearchRecommendation ControlRecommendation(ResearchRunType type, bool interactionPlausible) =>
        interactionPlausible ? ResearchRecommendation.ControlledSubset : type switch
        {
            ResearchRunType.Combined => ResearchRecommendation.CanaryAlone,
            ResearchRunType.CanaryOnly => ResearchRecommendation.TrustedPoolControl,
            ResearchRunType.TrustedPoolOnly => ResearchRecommendation.CanaryAlone,
            _ => ResearchRecommendation.ManualReview
        };

    private static HookRunClassification Result(
        HookRunClassificationKind kind,
        HookRunOutcome outcome,
        AttributionConfidence confidence,
        string? candidateId,
        HookValidationDepth? depth,
        string reason,
        ResearchRecommendation recommendation,
        BreadcrumbReadResult journal,
        HookRunManifest manifest,
        DateTimeOffset now,
        IReadOnlyList<string> evidence) =>
        new(ResearchContracts.ClassificationSchema, manifest.RunId, now, kind, outcome, confidence,
            candidateId, depth, journal.LastCompleted?.Boundary, journal.LastUnmatched?.Boundary,
            reason, recommendation, false, evidence);
}

public static class PromotionPolicy
{
    public static PromotionAssessment Evaluate(
        CandidateValidationRecord candidate,
        HookValidationDepth depth,
        string compatibilityFingerprint,
        bool requireBothRoles,
        bool naturalCallbackPractical = true)
    {
        var unmet = new List<string>();
        if (candidate.HighestValidatedDepth < depth) unmet.Add("requested depth has not completed validation");
        if (candidate.CleanRuns < 3) unmet.Add("three clean runs are required");
        if (naturalCallbackPractical && candidate.NaturalCallbacks < 3) unmet.Add("three matched natural callbacks are required");
        if (requireBothRoles && (candidate.HostCleanRuns < 1 || candidate.JoinedClientCleanRuns < 1))
            unmet.Add("host and joined-client clean evidence are required");
        if (candidate.LifecycleTransitionRuns < 1) unmet.Add("one clean island/lifecycle transition is required");
        if (candidate.HasUnmatchedBreadcrumb) unmet.Add("an unmatched breadcrumb remains");
        if (candidate.HasCorrelatedCrash || candidate.CrashSuspectRuns.Count > 0) unmet.Add("a correlated crash-suspect run remains");
        if (candidate.HasNewUe4ssCallbackError) unmet.Add("a new UE4SS callback error remains");
        if (!candidate.ReducerFixtureCovered) unmet.Add("deterministic reducer fixture coverage is required");
        if (!ResearchContracts.IsSha256(compatibilityFingerprint) ||
            !string.Equals(candidate.CompatibilityFingerprint, compatibilityFingerprint, StringComparison.Ordinal))
            unmet.Add("compatibility fingerprint does not match");
        if (candidate.State is HookCandidateState.Quarantined or HookCandidateState.CrashSuspect or
            HookCandidateState.Unsupported or HookCandidateState.NeedsRevalidation)
            unmet.Add($"candidate state {candidate.State} is not promotable");
        return new PromotionAssessment(unmet.Count == 0, unmet);
    }
}

public static class CompatibilityInvalidator
{
    public static HookValidationLedger InvalidateChangedTrust(
        HookValidationLedger ledger,
        string currentFingerprint,
        DateTimeOffset? nowUtc = null)
    {
        var candidates = ledger.Candidates.Select(candidate =>
            candidate.State == HookCandidateState.Trusted &&
            !string.Equals(candidate.CompatibilityFingerprint, currentFingerprint, StringComparison.Ordinal)
                ? candidate with { State = HookCandidateState.NeedsRevalidation, TrustedDepth = null }
                : candidate).ToArray();
        return ledger with { UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow, Candidates = candidates };
    }
}

public static class TrustedManifestBuilder
{
    public static (HookValidationLedger Ledger, TrustedHookManifest Manifest, PromotionAssessment Assessment) Promote(
        HookCandidateCatalog catalog,
        HookValidationLedger ledger,
        string candidateId,
        HookValidationDepth depth,
        CompatibilityFingerprint compatibility,
        bool requireBothRoles = true)
    {
        var records = ledger.Candidates.ToList();
        var index = records.FindIndex(candidate => candidate.CandidateId == candidateId);
        if (index < 0)
            return (ledger, Empty(catalog, compatibility, ledger.UpdatedAtUtc),
                new PromotionAssessment(false, new[] { "candidate is absent from the validation ledger" }));
        var assessment = PromotionPolicy.Evaluate(records[index], depth, compatibility.Fingerprint, requireBothRoles);
        if (!assessment.Eligible)
            return (ledger, Build(catalog, ledger, compatibility), assessment);
        records[index] = records[index] with { State = HookCandidateState.Trusted, TrustedDepth = depth };
        var updated = ledger with { UpdatedAtUtc = DateTimeOffset.UtcNow, Candidates = records };
        return (updated, Build(catalog, updated, compatibility), assessment);
    }

    public static TrustedHookManifest Build(
        HookCandidateCatalog catalog,
        HookValidationLedger ledger,
        CompatibilityFingerprint compatibility)
    {
        var entries = new List<TrustedHookEntry>();
        foreach (var record in ledger.Candidates.Where(candidate => candidate.State == HookCandidateState.Trusted))
        {
            if (record.TrustedDepth is null || !catalog.ById.TryGetValue(record.CandidateId, out var candidate)) continue;
            // TrustedDepth is written only by Promote after the full threshold succeeds. Later
            // deeper-canary counters describe the new depth and must not erase that durable
            // shallower trust unless compatibility or a safety signal invalidates it.
            if (record.HighestValidatedDepth < record.TrustedDepth.Value
                || record.CompatibilityFingerprint != compatibility.Fingerprint
                || record.HasUnmatchedBreadcrumb || record.HasCorrelatedCrash
                || record.CrashSuspectRuns.Count > 0 || record.HasNewUe4ssCallbackError
                || !record.ReducerFixtureCovered)
                continue;
            entries.Add(new TrustedHookEntry(candidate.Id, candidate.HookPathFingerprint,
                record.TrustedDepth.Value, compatibility.Fingerprint));
        }
        return new TrustedHookManifest(
            ResearchContracts.TrustedManifestSchema, catalog.CoverageCatalogHash, catalog.HookCatalogIdentity,
            catalog.CallbackImplementationVersion, catalog.CallbackSchemaVersion, catalog.ValidationBehaviorVersion,
            entries.Count == 0 ? string.Empty : compatibility.Fingerprint, ledger.UpdatedAtUtc,
            entries.OrderBy(entry => catalog.ById[entry.CandidateId].Priority)
                .ThenBy(entry => entry.CandidateId, StringComparer.Ordinal).ToArray());
    }

    private static TrustedHookManifest Empty(
        HookCandidateCatalog catalog,
        CompatibilityFingerprint compatibility,
        DateTimeOffset updatedAt) => new(
        ResearchContracts.TrustedManifestSchema, catalog.CoverageCatalogHash, catalog.HookCatalogIdentity,
        catalog.CallbackImplementationVersion, catalog.CallbackSchemaVersion, catalog.ValidationBehaviorVersion,
        string.Empty, updatedAt, Array.Empty<TrustedHookEntry>());
}

public static class QuarantinePolicy
{
    public static HookQuarantineState AddCrashSuspect(
        HookQuarantineState state,
        HookCandidateCatalog catalog,
        HookRunClassification classification,
        string runId,
        DateTimeOffset? nowUtc = null)
    {
        if (classification.Outcome != HookRunOutcome.CrashSuspect || classification.CandidateId is null ||
            classification.ValidationDepth is null || !catalog.ById.TryGetValue(classification.CandidateId, out var candidate))
            return state;
        var entries = state.Entries.Where(entry =>
            entry.CandidateId != candidate.Id || entry.ValidationDepth != classification.ValidationDepth).ToList();
        entries.Add(new HookQuarantineEntry(
            candidate.Id, candidate.HookPathFingerprint, classification.ValidationDepth.Value,
            HookCandidateState.CrashSuspect, classification.Reason, runId, nowUtc ?? DateTimeOffset.UtcNow,
            true, false));
        return state with { Entries = entries.OrderBy(entry => entry.CandidateId, StringComparer.Ordinal).ToArray() };
    }

    public static HookQuarantineState QuarantineExplicitly(
        HookQuarantineState state,
        HookCandidateDefinition candidate,
        HookValidationDepth depth,
        string runId,
        string reason,
        DateTimeOffset? nowUtc = null)
    {
        var entries = state.Entries.Where(entry =>
            entry.CandidateId != candidate.Id || entry.ValidationDepth != depth).ToList();
        entries.Add(new HookQuarantineEntry(candidate.Id, candidate.HookPathFingerprint, depth,
            HookCandidateState.Quarantined, reason, runId, nowUtc ?? DateTimeOffset.UtcNow, true, false));
        return state with { Entries = entries.OrderBy(entry => entry.CandidateId, StringComparer.Ordinal).ToArray() };
    }
}
