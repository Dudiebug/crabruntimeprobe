namespace CrabRuntimeProbe.Dashboard.Core;

public sealed record ResearchWorkspace(
    DashboardResources Resources,
    string LedgerPath,
    string TrustedManifestPath,
    string QuarantinePath,
    HookCandidateCatalog Catalog,
    HookValidationLedger Ledger,
    TrustedHookManifest TrustedManifest,
    HookQuarantineState Quarantine,
    CompatibilityFingerprint Compatibility);

public sealed record ResearchPreparationResult(
    ResearchWorkspace Workspace,
    ResearchRunPlan Plan,
    HookCandidateDefinition? RecommendedCandidate,
    HookValidationDepth? RecommendedDepth);

public sealed class ResearchPreparationService
{
    private readonly DashboardResourceLocator _resourceLocator;
    private readonly ResearchArtifactStore _artifacts = new();
    private readonly CompatibilityFingerprintService _compatibility = new();

    public ResearchPreparationService(DashboardResourceLocator? resourceLocator = null)
    {
        _resourceLocator = resourceLocator ?? new DashboardResourceLocator();
    }

    public async Task<ResearchPreparationResult> PlanAsync(
        LocalCampaignState campaign,
        ResearchRunType runType = ResearchRunType.Combined,
        string? canaryCandidateId = null,
        HookValidationDepth? canaryDepth = null,
        string? resourceStartPath = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await LoadWorkspaceAsync(campaign, resourceStartPath, cancellationToken).ConfigureAwait(false);
        var recommendation = Recommend(workspace.Catalog, workspace.Ledger, workspace.Quarantine);
        var candidateId = runType == ResearchRunType.TrustedPoolOnly
            ? null
            : canaryCandidateId ?? recommendation.Candidate?.Id;
        var depth = runType == ResearchRunType.TrustedPoolOnly
            ? null
            : canaryDepth ?? recommendation.Depth;
        var runId = $"research-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..8]}";
        var plan = new ResearchRunPlanner().CreatePlan(
            workspace.Catalog, workspace.Compatibility, workspace.TrustedManifest, workspace.Ledger,
            workspace.Quarantine, runType, candidateId, depth, campaign.Role, campaign.Generation,
            runId, campaign.SessionId);
        return new ResearchPreparationResult(workspace, plan, recommendation.Candidate, recommendation.Depth);
    }

    public async Task<ResearchWorkspace> LoadWorkspaceAsync(
        LocalCampaignState campaign,
        string? resourceStartPath = null,
        CancellationToken cancellationToken = default)
    {
        var resources = _resourceLocator.Locate(resourceStartPath);
        var catalog = await _artifacts.ReadCatalogAsync(
            Path.Combine(resources.CampaignRoot, "hook_candidate_catalog.json"), cancellationToken).ConfigureAwait(false);
        var ledgerPath = Path.Combine(campaign.StatusDirectory, "hook_validation_ledger.json");
        var trustedPath = Path.Combine(campaign.StatusDirectory, "trusted_hook_manifest.json");
        var quarantinePath = Path.Combine(campaign.StatusDirectory, "hook_quarantine.json");
        await SeedIfMissingAsync(ledgerPath, Path.Combine(resources.CampaignRoot, "hook_validation_ledger.json"), cancellationToken)
            .ConfigureAwait(false);
        await SeedIfMissingAsync(trustedPath, Path.Combine(resources.CampaignRoot, "trusted_hook_manifest.json"), cancellationToken)
            .ConfigureAwait(false);
        await SeedIfMissingAsync(quarantinePath, Path.Combine(resources.CampaignRoot, "hook_quarantine.json"), cancellationToken)
            .ConfigureAwait(false);
        var ledger = await _artifacts.ReadLedgerAsync(ledgerPath, cancellationToken).ConfigureAwait(false);
        var trusted = await _artifacts.ReadTrustedManifestAsync(trustedPath, cancellationToken).ConfigureAwait(false);
        var quarantine = await _artifacts.ReadQuarantineAsync(quarantinePath, cancellationToken).ConfigureAwait(false);
        if (ledger.HookCatalogIdentity != catalog.HookCatalogIdentity || ledger.CoverageCatalogHash != catalog.CoverageCatalogHash)
        {
            var defaults = await _artifacts.ReadLedgerAsync(
                Path.Combine(resources.CampaignRoot, "hook_validation_ledger.json"), cancellationToken).ConfigureAwait(false);
            ledger = MigrateLedger(defaults, ledger, catalog);
        }
        if (quarantine.HookCatalogIdentity != catalog.HookCatalogIdentity)
            quarantine = new HookQuarantineState(ResearchContracts.QuarantineSchema, catalog.HookCatalogIdentity,
                quarantine.Entries.Where(entry => catalog.ById.TryGetValue(entry.CandidateId, out var candidate) &&
                                                   candidate.HookPathFingerprint == entry.HookPathFingerprint).ToArray());
        var gameBinary = Path.GetDirectoryName(campaign.ExecutablePath) ?? campaign.GameDirectory;
        var ue4ssPath = Path.Combine(gameBinary, "UE4SS.dll");
        if (!File.Exists(ue4ssPath))
            ue4ssPath = Path.Combine(SteamGameLocator.ResolveGameBinaryDirectory(
                new GameInstallation(campaign.GameDirectory, campaign.ExecutablePath, "prepared-campaign")), "UE4SS.dll");
        var compatibility = await _compatibility.FromInstallationAsync(
            campaign.ExecutablePath, ue4ssPath, catalog, cancellationToken).ConfigureAwait(false);
        ledger = CompatibilityInvalidator.InvalidateChangedTrust(ledger, compatibility.Fingerprint);
        trusted = TrustedManifestBuilder.Build(catalog, ledger, compatibility);
        await _artifacts.WriteLedgerAsync(ledgerPath, ledger, catalog.GeneratedAtUtc,
            "Legacy observations remain history only and never confer compatibility-aware trust.", cancellationToken)
            .ConfigureAwait(false);
        await _artifacts.WriteTrustedManifestAsync(trustedPath, trusted, catalog.GeneratedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        await _artifacts.WriteQuarantineAsync(quarantinePath, quarantine, catalog, catalog.GeneratedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        return new ResearchWorkspace(resources, ledgerPath, trustedPath, quarantinePath,
            catalog, ledger, trusted, quarantine, compatibility);
    }

    public static (HookCandidateDefinition? Candidate, HookValidationDepth? Depth) Recommend(
        HookCandidateCatalog catalog,
        HookValidationLedger ledger,
        HookQuarantineState quarantine)
    {
        var records = ledger.Candidates.ToDictionary(candidate => candidate.CandidateId, StringComparer.Ordinal);
        var blocked = quarantine.Entries.Select(entry => entry.CandidateId).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in catalog.Candidates.OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            if (blocked.Contains(candidate.Id)) continue;
            if (!records.TryGetValue(candidate.Id, out var record))
                return (candidate, HookValidationDepth.RegistrationOnly);
            if (record.State is HookCandidateState.Quarantined or HookCandidateState.CrashSuspect or
                HookCandidateState.Unsupported or HookCandidateState.NeedsRevalidation)
                continue;
            if (record.State == HookCandidateState.Trusted && record.TrustedDepth is { } trustedDepth)
            {
                if (trustedDepth >= candidate.MaximumValidationDepth) continue;
                return (candidate, (HookValidationDepth)((int)trustedDepth + 1));
            }
            var next = (HookValidationDepth)Math.Min((int)candidate.MaximumValidationDepth,
                Math.Max(1, (int)record.HighestValidatedDepth));
            return (candidate, next);
        }
        return (null, null);
    }

    private static HookValidationLedger MigrateLedger(
        HookValidationLedger defaults,
        HookValidationLedger previous,
        HookCandidateCatalog catalog)
    {
        var prior = previous.Candidates.ToDictionary(candidate => candidate.CandidateId, StringComparer.Ordinal);
        var migrated = defaults.Candidates.Select(candidate =>
        {
            if (!prior.TryGetValue(candidate.CandidateId, out var old) ||
                old.HookPathFingerprint != candidate.HookPathFingerprint) return candidate;
            return candidate with
            {
                State = old.State == HookCandidateState.Untested ? HookCandidateState.Untested : HookCandidateState.NeedsRevalidation,
                HighestValidatedDepth = old.HighestValidatedDepth,
                TrustedDepth = null,
                CleanRuns = old.CleanRuns,
                NaturalCallbacks = old.NaturalCallbacks,
                HostCleanRuns = old.HostCleanRuns,
                JoinedClientCleanRuns = old.JoinedClientCleanRuns,
                LifecycleTransitionRuns = old.LifecycleTransitionRuns,
                EvidenceSessions = old.EvidenceSessions,
                CrashSuspectRuns = old.CrashSuspectRuns,
                CompatibilityFingerprint = old.CompatibilityFingerprint,
                HasUnmatchedBreadcrumb = old.HasUnmatchedBreadcrumb,
                HasCorrelatedCrash = old.HasCorrelatedCrash,
                HasNewUe4ssCallbackError = old.HasNewUe4ssCallbackError,
                ReducerFixtureCovered = old.ReducerFixtureCovered
            };
        }).ToArray();
        return defaults with
        {
            CoverageCatalogHash = catalog.CoverageCatalogHash,
            HookCatalogIdentity = catalog.HookCatalogIdentity,
            CallbackImplementationVersion = catalog.CallbackImplementationVersion,
            CallbackSchemaVersion = catalog.CallbackSchemaVersion,
            ValidationBehaviorVersion = catalog.ValidationBehaviorVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Candidates = migrated
        };
    }

    private static async Task SeedIfMissingAsync(string destination, string source, CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) return;
        if (!File.Exists(source)) throw new FileNotFoundException("Packaged research default is missing.", source);
        var text = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteTextAsync(destination, text, cancellationToken).ConfigureAwait(false);
    }
}
