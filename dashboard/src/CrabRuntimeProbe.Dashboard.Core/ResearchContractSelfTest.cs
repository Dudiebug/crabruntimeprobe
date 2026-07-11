using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public static class ResearchContractSelfTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<IReadOnlyList<string>> RunAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var resources = new DashboardResourceLocator().Locate();
        var store = new ResearchArtifactStore();
        var catalog = await store.ReadCatalogAsync(Path.Combine(resources.CampaignRoot, "hook_candidate_catalog.json"), cancellationToken)
            .ConfigureAwait(false);
        var ledger = await store.ReadLedgerAsync(Path.Combine(resources.CampaignRoot, "hook_validation_ledger.json"), cancellationToken)
            .ConfigureAwait(false);
        var trusted = await store.ReadTrustedManifestAsync(Path.Combine(resources.CampaignRoot, "trusted_hook_manifest.json"), cancellationToken)
            .ConfigureAwait(false);
        var quarantine = await store.ReadQuarantineAsync(Path.Combine(resources.CampaignRoot, "hook_quarantine.json"), cancellationToken)
            .ConfigureAwait(false);
        Require(catalog.Candidates.Count == 111 && trusted.Candidates.Count == 0,
            "111 cataloged candidates and empty initial trusted pool", messages);
        var principal = catalog.ById[catalog.PrincipalCandidateId];
        Require(principal.DisplayName == "OnRep_IslandRewardRarity" && principal.KnownCrashContext,
            "principal suspect identity is preserved without causal overclaim", messages);
        var compatibility = new CompatibilityFingerprintService().Compute("game:fixture-1", "ue4ss:fixture-1", catalog,
            DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var planner = new ResearchRunPlanner();
        var plan = planner.CreatePlan(catalog, compatibility, trusted, ledger, quarantine,
            ResearchRunType.Combined, catalog.PrincipalCandidateId, HookValidationDepth.RegistrationOnly,
            CampaignRole.Host, 1, "research-fixture-0001", "session-fixture-0001");
        Require(plan.IsValid && plan.Manifest?.TrustedCandidates.Count == 0 &&
                plan.Manifest.Canary?.CandidateId == catalog.PrincipalCandidateId &&
                plan.Manifest.RegistrationOrder[^1] == catalog.PrincipalCandidateId,
            "zero trusted plus exactly one registration-only canary registered last", messages);

        var badCompatibility = compatibility with { Fingerprint = new string('0', 64) };
        Require(!planner.CreatePlan(catalog, badCompatibility, trusted, ledger, quarantine,
                ResearchRunType.CanaryOnly, catalog.PrincipalCandidateId, HookValidationDepth.RegistrationOnly,
                CampaignRole.Host, 1, "research-fixture-0002", "session-fixture-0002").IsValid,
            "invalid compatibility fails closed", messages);
        var blocked = QuarantinePolicy.QuarantineExplicitly(quarantine, principal,
            HookValidationDepth.RegistrationOnly, "prior-run", "fixture quarantine");
        Require(!planner.CreatePlan(catalog, compatibility, trusted, ledger, blocked,
                ResearchRunType.CanaryOnly, catalog.PrincipalCandidateId, HookValidationDepth.RegistrationOnly,
                CampaignRole.Host, 1, "research-fixture-0003", "session-fixture-0003").IsValid,
            "quarantined canary cannot auto-arm", messages);

        var manifest = plan.Manifest!;
        var reader = new BreadcrumbJournalReader();
        var beginBoundaries = new (string Boundary, HookValidationDepth Depth)[]
        {
            ("registration-begin", HookValidationDepth.RegistrationOnly),
            ("callback-enter", HookValidationDepth.CallbackEntryExit),
            ("context-resolve-begin", HookValidationDepth.ContextResolution),
            ("scope-resolve-begin", HookValidationDepth.PlayerStateScope),
            ("prestate-read-begin", HookValidationDepth.ReviewedStateReads),
            ("arguments-read-begin", HookValidationDepth.DocumentedArguments),
            ("poststate-read-begin", HookValidationDepth.FullPassiveEvidence),
            ("evidence-write-begin", HookValidationDepth.FullPassiveEvidence)
        };
        foreach (var fixture in beginBoundaries)
        {
            var fixtureManifest = manifest with
            {
                Canary = manifest.Canary! with { ValidationDepth = fixture.Depth }
            };
            var rows = fixture.Boundary == "registration-begin"
                ? new[] { Row(1, fixtureManifest, principal, fixture.Depth, "registration-begin", "reg-1", "registration") }
                : new[]
                {
                    Row(1, fixtureManifest, principal, fixture.Depth, "registration-begin", "reg-1", "registration"),
                    Row(2, fixtureManifest, principal, fixture.Depth, "registration-complete", "reg-1", "registration"),
                    Row(3, fixtureManifest, principal, fixture.Depth, "callback-enter", "inv-1", "post"),
                    fixture.Boundary == "callback-enter" ? null : Row(4, fixtureManifest, principal, fixture.Depth, fixture.Boundary, "inv-1", "post")
                }.Where(value => value is not null).Cast<string>().ToArray();
            var parsed = reader.Parse(string.Join("\n", rows) + "\n", fixtureManifest.RunId);
            var classification = new HookRunClassifier().Classify(fixtureManifest, parsed,
                new RunObservationSignals(false, true, true, false, false, false, false, true, 0));
            Require(parsed.LastUnmatched?.Boundary == fixture.Boundary &&
                    classification.Outcome == HookRunOutcome.CrashSuspect,
                $"interrupted {fixture.Boundary} fixture", messages, appendSuccess: false);
        }
        messages.Add("PASS simulated crash after every enabled begin boundary");

        var registeredOnly = reader.Parse(string.Join("\n", new[]
        {
            Row(1, manifest, principal, HookValidationDepth.RegistrationOnly, "registration-begin", "reg-1", "registration"),
            Row(2, manifest, principal, HookValidationDepth.RegistrationOnly, "registration-complete", "reg-1", "registration")
        }) + "\n", manifest.RunId);
        var registeredClassification = new HookRunClassifier().Classify(manifest, registeredOnly,
            new RunObservationSignals(true, true, false, false, false, false, false, false, 0));
        Require(registeredClassification.Outcome == HookRunOutcome.RegisteredNotNaturallyObserved,
            "registered but never naturally observed is distinct", messages);

        var completedManifest = manifest with
        {
            Canary = manifest.Canary! with { ValidationDepth = HookValidationDepth.CallbackEntryExit }
        };
        var completed = reader.Parse(string.Join("\n", new[]
        {
            Row(1, completedManifest, principal, HookValidationDepth.CallbackEntryExit, "registration-begin", "reg-1", "registration"),
            Row(2, completedManifest, principal, HookValidationDepth.CallbackEntryExit, "registration-complete", "reg-1", "registration"),
            Row(3, completedManifest, principal, HookValidationDepth.CallbackEntryExit, "callback-enter", "inv-1", "post"),
            Row(4, completedManifest, principal, HookValidationDepth.CallbackEntryExit, "callback-exit", "inv-1", "post")
        }) + "\n", completedManifest.RunId);
        var laterCrash = new HookRunClassifier().Classify(completedManifest, completed,
            new RunObservationSignals(false, true, true, false, false, false, false, true, 0));
        Require(laterCrash.Classification == HookRunClassificationKind.UnattributedPostCallbackCrash &&
                laterCrash.CandidateId is null && laterCrash.Confidence == AttributionConfidence.None,
            "completed callback followed by later crash remains unattributed", messages);

        var truncated = reader.Parse(Row(1, manifest, principal, HookValidationDepth.RegistrationOnly,
            "registration-begin", "reg-1", "registration") + "\n{\"schemaVersion\":", manifest.RunId);
        Require(truncated.TruncatedFinalWrite && truncated.Records.Count == 1,
            "truncated final breadcrumb recovery", messages);
        var duplicate = reader.Parse(string.Join("\n", new[]
        {
            Row(1, manifest, principal, HookValidationDepth.RegistrationOnly, "registration-begin", "reg-1", "registration"),
            Row(1, manifest, principal, HookValidationDepth.RegistrationOnly, "registration-complete", "reg-1", "registration")
        }) + "\n", manifest.RunId);
        Require(duplicate.HasFatalIssue && duplicate.Issues.Any(issue => issue.Code == "duplicate-sequence"),
            "duplicate breadcrumb sequence fails closed", messages);
        var stale = reader.Parse(string.Join("\n", new[]
        {
            Row(1, manifest, principal, HookValidationDepth.RegistrationOnly, "registration-begin", "reg-1", "registration", 2),
            Row(2, manifest, principal, HookValidationDepth.RegistrationOnly, "registration-complete", "reg-1", "registration", 1)
        }) + "\n", manifest.RunId);
        Require(stale.HasFatalIssue && stale.Issues.Any(issue => issue.Code == "stale-lifecycle-generation"),
            "stale lifecycle generation fails closed", messages);
        var unknown = reader.Parse(Row(1, manifest, principal, HookValidationDepth.RegistrationOnly,
            "registration-begin", "reg-1", "unknown-phase") + "\n", manifest.RunId);
        Require(unknown.HasFatalIssue, "unknown breadcrumb phase fails closed", messages);
        var orphanComplete = reader.Parse(Row(1, manifest, principal, HookValidationDepth.RegistrationOnly,
            "registration-complete", "reg-orphan", "registration") + "\n", manifest.RunId);
        Require(orphanComplete.HasFatalIssue
                && orphanComplete.Issues.Any(issue => issue.Code == "orphan-complete-boundary"),
            "orphan completion boundary fails closed", messages);
        var overDepth = reader.Parse(Row(1, manifest, principal, HookValidationDepth.RegistrationOnly,
            "context-resolve-begin", "inv-over-depth", "pre") + "\n", manifest.RunId);
        Require(overDepth.HasFatalIssue
                && overDepth.Issues.Any(issue => issue.Code == "boundary-over-depth"),
            "deeper boundary at a shallow validation depth fails closed", messages);
        var wrongFingerprintRows = string.Join("\n", new[]
        {
            Row(1, manifest, principal, HookValidationDepth.RegistrationOnly,
                "registration-begin", "reg-wrong-fingerprint", "registration"),
            Row(2, manifest, principal, HookValidationDepth.RegistrationOnly,
                "registration-complete", "reg-wrong-fingerprint", "registration")
        }).Replace(principal.HookPathFingerprint, new string('0', 64), StringComparison.Ordinal) + "\n";
        var wrongFingerprint = reader.Parse(wrongFingerprintRows, manifest.RunId);
        var wrongFingerprintClassification = new HookRunClassifier().Classify(
            manifest, wrongFingerprint,
            new RunObservationSignals(true, true, false, false, false, false, false, false, 0));
        Require(wrongFingerprintClassification.Classification == HookRunClassificationKind.EvidenceFailure
                && wrongFingerprintClassification.Outcome == HookRunOutcome.Incomplete,
            "journal identity mismatch cannot attribute a candidate", messages);

        var promotable = ledger.Candidates.First(candidate => candidate.CandidateId == principal.Id) with
        {
            State = HookCandidateState.Provisional,
            HighestValidatedDepth = HookValidationDepth.CallbackEntryExit,
            CleanRuns = 3,
            NaturalCallbacks = 3,
            HostCleanRuns = 1,
            JoinedClientCleanRuns = 1,
            LifecycleTransitionRuns = 1,
            CompatibilityFingerprint = compatibility.Fingerprint,
            ReducerFixtureCovered = true
        };
        Require(PromotionPolicy.Evaluate(promotable, HookValidationDepth.CallbackEntryExit,
                compatibility.Fingerprint, true).Eligible &&
                !PromotionPolicy.Evaluate(promotable with { NaturalCallbacks = 2 },
                    HookValidationDepth.CallbackEntryExit, compatibility.Fingerprint, true).Eligible,
            "promotion requires every configured threshold", messages);
        var underThreshold = promotable with
        {
            State = HookCandidateState.NaturalCallbackClean,
            CleanRuns = 1,
            NaturalCallbacks = 1,
            HostCleanRuns = 1,
            JoinedClientCleanRuns = 0
        };
        var underThresholdLedger = ledger with
        {
            Candidates = ledger.Candidates.Select(candidate => candidate.CandidateId == principal.Id
                ? underThreshold
                : candidate).ToArray()
        };
        Require(!planner.CreatePlan(
                catalog, compatibility, trusted, underThresholdLedger, quarantine,
                ResearchRunType.CanaryOnly, principal.Id, HookValidationDepth.ContextResolution,
                CampaignRole.Host, 2, "research-fixture-premature-depth", "session-fixture-premature-depth").IsValid,
            "next depth remains blocked until current-depth promotion", messages);
        var thresholdLedger = ledger with
        {
            Candidates = ledger.Candidates.Select(candidate => candidate.CandidateId == principal.Id
                ? promotable
                : candidate).ToArray()
        };
        var promotion = TrustedManifestBuilder.Promote(
            catalog, thresholdLedger, principal.Id, HookValidationDepth.CallbackEntryExit, compatibility);
        Require(promotion.Assessment.Eligible
                && promotion.Manifest.Candidates.Count == 1
                && promotion.Manifest.Candidates[0].TrustedDepth == HookValidationDepth.CallbackEntryExit,
            "eligible candidate enters the compatibility-bound trusted manifest", messages);

        var nextCandidate = catalog.Candidates.First(candidate => candidate.Id != principal.Id);
        var trustedPlusCanary = planner.CreatePlan(
            catalog, compatibility, promotion.Manifest, promotion.Ledger, quarantine,
            ResearchRunType.Combined, nextCandidate.Id, HookValidationDepth.RegistrationOnly,
            CampaignRole.Host, 2, "research-fixture-trusted-canary", "session-fixture-trusted-canary");
        Require(trustedPlusCanary.IsValid
                && trustedPlusCanary.Manifest?.TrustedCandidates.Count == 1
                && trustedPlusCanary.Manifest.Canary?.CandidateId == nextCandidate.Id
                && trustedPlusCanary.Manifest.RegistrationOrder[^1] == nextCandidate.Id,
            "trusted pool plus exactly one distinct canary is deterministic and canary-last", messages);

        var deeperSameCandidate = planner.CreatePlan(
            catalog, compatibility, promotion.Manifest, promotion.Ledger, quarantine,
            ResearchRunType.Combined, principal.Id, HookValidationDepth.ContextResolution,
            CampaignRole.Host, 3, "research-fixture-deeper-canary", "session-fixture-deeper-canary");
        Require(deeperSameCandidate.IsValid
                && deeperSameCandidate.Manifest?.TrustedCandidates.All(candidate => candidate.CandidateId != principal.Id) == true
                && deeperSameCandidate.Manifest.Canary?.CandidateId == principal.Id,
            "a trusted candidate registers once as the next-depth canary", messages);

        var deeperManifest = deeperSameCandidate.Manifest!;
        var deeperJournal = reader.Parse(string.Join("\n", new[]
        {
            Row(1, deeperManifest, principal, HookValidationDepth.ContextResolution, "registration-begin", "reg-deeper", "registration"),
            Row(2, deeperManifest, principal, HookValidationDepth.ContextResolution, "registration-complete", "reg-deeper", "registration"),
            Row(3, deeperManifest, principal, HookValidationDepth.ContextResolution, "callback-enter", "inv-deeper", "post"),
            Row(4, deeperManifest, principal, HookValidationDepth.ContextResolution, "context-resolve-begin", "inv-deeper", "post"),
            Row(5, deeperManifest, principal, HookValidationDepth.ContextResolution, "context-resolve-complete", "inv-deeper", "post"),
            Row(6, deeperManifest, principal, HookValidationDepth.ContextResolution, "callback-exit", "inv-deeper", "post")
        }) + "\n", deeperManifest.RunId);
        var deeperClassification = new HookRunClassifier().Classify(
            deeperManifest, deeperJournal,
            new RunObservationSignals(true, true, false, false, false, false, false, false, 0));
        var deeperLedger = ValidationLedgerReducer.Apply(
            promotion.Ledger, deeperManifest, deeperClassification, deeperJournal,
            lifecycleTransitionObserved: true, reducerFixtureCovered: true, newUe4ssCallbackError: false);
        var deeperRecord = deeperLedger.Candidates.Single(candidate => candidate.CandidateId == principal.Id);
        var prematurePromotion = TrustedManifestBuilder.Promote(
            catalog, deeperLedger, principal.Id, HookValidationDepth.ContextResolution, compatibility);
        var retainedTrustPlan = planner.CreatePlan(
            catalog, compatibility, prematurePromotion.Manifest, deeperLedger, quarantine,
            ResearchRunType.Combined, nextCandidate.Id, HookValidationDepth.RegistrationOnly,
            CampaignRole.JoinedClient, 4, "research-fixture-retained-trust", "session-fixture-retained-trust");
        Require(deeperRecord.State == HookCandidateState.Trusted
                && deeperRecord.TrustedDepth == HookValidationDepth.CallbackEntryExit
                && deeperRecord.HighestValidatedDepth == HookValidationDepth.ContextResolution
                && deeperRecord.CleanRuns == 1 && deeperRecord.NaturalCallbacks == 1
                && !prematurePromotion.Assessment.Eligible
                && prematurePromotion.Manifest.Candidates.Single().TrustedDepth == HookValidationDepth.CallbackEntryExit
                && retainedTrustPlan.IsValid
                && retainedTrustPlan.Manifest?.TrustedCandidates.Single().ValidationDepth
                    == HookValidationDepth.CallbackEntryExit,
            "deeper counters reset per depth while shallower trust remains until re-promotion", messages);
        var invalidated = CompatibilityInvalidator.InvalidateChangedTrust(
            ledger with { Candidates = new[] { promotable with { State = HookCandidateState.Trusted, TrustedDepth = HookValidationDepth.CallbackEntryExit } } },
            new string('f', 64));
        Require(invalidated.Candidates[0].State == HookCandidateState.NeedsRevalidation &&
                invalidated.Candidates[0].TrustedDepth is null,
            "compatibility change invalidates trust", messages);
        return messages;
    }

    private static string Row(
        long sequence,
        HookRunManifest manifest,
        HookCandidateDefinition candidate,
        HookValidationDepth depth,
        string boundary,
        string invocationId,
        string phase,
        long lifecycleGeneration = 1) => JsonSerializer.Serialize(new
    {
        schemaVersion = ResearchContracts.BreadcrumbSchema,
        sequence,
        runId = manifest.RunId,
        candidateId = candidate.Id,
        hookPathFingerprint = candidate.HookPathFingerprint,
        validationDepth = (int)depth,
        candidateRole = "canary",
        invocationId,
        phase,
        boundary,
        lifecycleGeneration,
        timestampUtc = "2026-07-11T00:00:00Z",
        monotonicMicros = sequence * 1000
    }, JsonOptions);

    private static void Require(
        bool condition,
        string name,
        ICollection<string> messages,
        bool appendSuccess = true)
    {
        if (!condition) throw new InvalidOperationException($"Research self-test failed: {name}");
        if (appendSuccess) messages.Add($"PASS {name}");
    }
}
