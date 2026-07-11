using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed partial class EvidenceRedactor
{
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var redacted = SensitiveJsonPropertyRegex().Replace(text, match =>
            $"\"{match.Groups["key"].Value}\":\"[redacted]\"");
        redacted = SteamIdRegex().Replace(redacted, "[redacted-platform-id]");
        redacted = UserProfileRegex().Replace(redacted, @"C:\Users\[redacted]");
        redacted = RawIdentityTokenRegex().Replace(redacted, "rawIdentity=[redacted]");
        return redacted;
    }

    public bool ContainsPrivateIdentity(string text) =>
        SteamIdRegex().IsMatch(text)
        || SensitiveJsonPropertyRegex().Matches(text).Any(match =>
            !match.Value.Contains("[redacted]", StringComparison.OrdinalIgnoreCase)
            && !match.Value.Contains("anonymous", StringComparison.OrdinalIgnoreCase))
        || RawIdentityTokenRegex().Matches(text).Any(match =>
            !match.Value.Contains("[redacted]", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("\\\"(?<key>PlayerName|PlayerNamePrivate|UniqueId|SteamId|PlatformId|NetId|rawDisplayName|rawStableId|rawIdentity|displayName)\\\"\\s*:\\s*\\\"(?:\\\\.|[^\\\"])*\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveJsonPropertyRegex();

    [GeneratedRegex("(?<![0-9])[0-9]{17}(?![0-9])")]
    private static partial Regex SteamIdRegex();

    [GeneratedRegex("C:\\\\{1,2}Users\\\\{1,2}[^\\\\/\\s\\\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfileRegex();

    [GeneratedRegex(@"rawIdentity\s*=\s*[^\s,}\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex RawIdentityTokenRegex();
}

public sealed class EvidenceCollector
{
    private const long MaximumCollectedFileBytes = 64L * 1024 * 1024;
    private const int MaximumCrashArtifacts = 32;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly LiveStatusReader _statusReader;
    private readonly ChecklistReducer _fallbackChecklistReducer;
    private readonly CoverageCatalogReader _coverageReader;
    private readonly CapabilityReadinessService _readinessService;
    private readonly EvidenceRedactor _redactor;
    private readonly DashboardResourceLocator _resourceLocator;
    private readonly ChecklistDefinitionLoader _checklistLoader;
    private readonly SnapshotEvidenceService _snapshotEvidenceService;

    public EvidenceCollector(
        LiveStatusReader? statusReader = null,
        ChecklistReducer? checklistReducer = null,
        CoverageCatalogReader? coverageReader = null,
        CapabilityReadinessService? readinessService = null,
        EvidenceRedactor? redactor = null,
        DashboardResourceLocator? resourceLocator = null,
        ChecklistDefinitionLoader? checklistLoader = null,
        SnapshotEvidenceService? snapshotEvidenceService = null)
    {
        _statusReader = statusReader ?? new LiveStatusReader();
        _fallbackChecklistReducer = checklistReducer ?? new ChecklistReducer();
        _coverageReader = coverageReader ?? new CoverageCatalogReader();
        _readinessService = readinessService ?? new CapabilityReadinessService();
        _redactor = redactor ?? new EvidenceRedactor();
        _resourceLocator = resourceLocator ?? new DashboardResourceLocator();
        _checklistLoader = checklistLoader ?? new ChecklistDefinitionLoader();
        _snapshotEvidenceService = snapshotEvidenceService ?? new SnapshotEvidenceService();
    }

    public async Task<CollectionResult> CollectAsync(
        LocalCampaignState state,
        string exportRoot,
        bool abnormalProcessExit = false,
        CancellationToken cancellationToken = default,
        string? resourceStartPath = null)
    {
        var safeSession = SafeName(state.SessionId);
        var role = state.Role.ToContract();
        var bundleDirectory = UniqueDirectory(Path.Combine(
            Path.GetFullPath(exportRoot), $"CrabRuntimeProbe-{safeSession}-{role}"));
        Directory.CreateDirectory(bundleDirectory);

        var canonicalDirectory = Path.Combine(bundleDirectory, "evidence", "canonical");
        var derivedDirectory = Path.Combine(bundleDirectory, "evidence", "derived-redacted");
        var researchDirectory = Path.Combine(bundleDirectory, "evidence", "research-redacted");
        var statusOutputDirectory = Path.Combine(bundleDirectory, "evidence", "live-status-redacted");
        var crashDirectory = Path.Combine(bundleDirectory, "crash-metadata");
        var provenanceDirectory = Path.Combine(bundleDirectory, "provenance");
        var omissionDirectory = Path.Combine(bundleDirectory, "omissions");
        foreach (var directory in new[]
                 {
                     canonicalDirectory, derivedDirectory, researchDirectory, statusOutputDirectory, crashDirectory,
                     provenanceDirectory, omissionDirectory
                 })
            Directory.CreateDirectory(directory);

        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var status = await _statusReader.ReadLatestAsync(
                state.StatusDirectory,
                scope: StatusReadScope.FromCampaign(state),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var copiedEvidence = 0;
        var omitted = new List<string>();
        var dirty = status.Snapshot.DirtyEvidence || status.UsedLastGood || !status.HasSnapshot;
        var activeProfile = ActiveProfile(status);
        var progressiveProfile = activeProfile.Equals(
            "progressive-broad-observation", StringComparison.OrdinalIgnoreCase);

        if (!progressiveProfile)
        {
            try
            {
                var snapshotEvidence = await _snapshotEvidenceService.LoadAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                if (snapshotEvidence.Replay.Rejections.Count > 0)
                {
                    omitted.Add($"snapshot evidence: {snapshotEvidence.Replay.Rejections.Count} row(s) rejected");
                    dirty = true;
                }
                else
                {
                    status = _snapshotEvidenceService.Merge(
                        status,
                        snapshotEvidence.Replay,
                        SnapshotReplayScope.FromCampaign(state));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                omitted.Add($"snapshot evidence: unavailable during collection ({ex.Message})");
                dirty = true;
            }
        }

        DashboardResources? resources = null;
        try
        {
            resources = _resourceLocator.Locate(resourceStartPath);
        }
        catch (DirectoryNotFoundException)
        {
            omitted.Add("campaign provenance: packaged/repository campaign resources were not found");
            dirty = true;
        }

        var activeRuntimeSession = status.HasSnapshot && !string.IsNullOrWhiteSpace(status.Snapshot.SessionId)
            ? status.Snapshot.SessionId
            : state.SessionId;
        var researchSafety = progressiveProfile
            ? await ValidateControlledResearchAsync(state, resources, cancellationToken).ConfigureAwait(false)
            : ResearchSafetyValidation.NotApplicable;
        var bundleSafety = SafetyFrom(status, researchSafety);
        var bundleProfileId = progressiveProfile ? "progressive-broad-observation" : "crabsync-full-observe";
        if (!bundleSafety.IsAcceptableForProfile(bundleProfileId))
        {
            omitted.Add(progressiveProfile
                ? $"safety: controlled progressive research could not be proven ({researchSafety.Reason})"
                : "safety: current status does not prove all normal-mode hooks and mutation paths disabled");
            dirty = true;
        }

        foreach (var source in EnumerateApprovedScriptEvidence(state))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCanonicalEvidence(source))
            {
                var validation = await ValidateCanonicalAsync(
                    source, state.Generation, activeRuntimeSession, progressiveProfile &&
                    bundleSafety.IsAcceptableForProfile(bundleProfileId), cancellationToken).ConfigureAwait(false);
                if (!validation.Accepted)
                {
                    omitted.Add($"{Path.GetFileName(source)}: {validation.Reason}");
                    dirty |= !validation.UnrelatedSession;
                    continue;
                }

                var destination = UniqueFile(Path.Combine(canonicalDirectory, Path.GetFileName(source)));
                await CopyByteForByteAsync(source, destination, cancellationToken).ConfigureAwait(false);
                sourceHashes[Relative(bundleDirectory, destination)] = await Sha256Async(source, cancellationToken)
                    .ConfigureAwait(false);
                copiedEvidence++;
            }
            else
            {
                var name = Path.GetFileName(source);
                var destinationRoot = name.StartsWith("live_status", StringComparison.OrdinalIgnoreCase)
                    ? statusOutputDirectory
                    : IsResearchArtifact(name) ? researchDirectory : derivedDirectory;
                var destination = UniqueFile(Path.Combine(destinationRoot, Path.GetFileName(source)));
                if (await CopyRedactedAsync(source, destination, cancellationToken).ConfigureAwait(false))
                {
                    sourceHashes[Relative(bundleDirectory, destination)] = await Sha256Async(source, cancellationToken)
                        .ConfigureAwait(false);
                    copiedEvidence++;
                }
                else
                {
                    omitted.Add($"{Path.GetFileName(source)}: file exceeded the 64 MiB collection cap");
                    dirty = true;
                }
            }
        }

        var gameBinary = ResolveGameBinaryDirectory(state);
        foreach (var source in EnumerateKnownLogs(gameBinary))
        {
            var destination = UniqueFile(Path.Combine(derivedDirectory, Path.GetFileName(source)));
            if (await CopyRedactedAsync(source, destination, cancellationToken).ConfigureAwait(false))
            {
                sourceHashes[Relative(bundleDirectory, destination)] = await Sha256Async(source, cancellationToken)
                    .ConfigureAwait(false);
                copiedEvidence++;
            }
            else
            {
                omitted.Add($"{Path.GetFileName(source)}: file exceeded the 64 MiB collection cap");
                dirty = true;
            }
        }

        var crashArtifactSeen = false;
        foreach (var source in EnumerateRecentCrashArtifacts(state).Take(MaximumCrashArtifacts))
        {
            crashArtifactSeen = true;
            var metadata = await WriteCrashMetadataAsync(source, crashDirectory, cancellationToken).ConfigureAwait(false);
            sourceHashes[Relative(bundleDirectory, metadata)] = await Sha256Async(source, cancellationToken)
                .ConfigureAwait(false);
        }

        var provenance = resources is null
            ? new ProvenanceResult(string.Empty, string.Empty, "unknown", "unknown", Array.Empty<string>())
            : await CopyProvenanceAsync(
                resources, bundleDirectory, provenanceDirectory, sourceHashes, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(provenance.CatalogPath))
        {
            omitted.Add("coverage catalog: required authoritative denominator is missing");
            dirty = true;
        }

        IReadOnlyList<ChecklistDefinition> definitions = ChecklistCatalog.All;
        if (resources is not null)
        {
            try
            {
                definitions = await _checklistLoader.LoadAuthoritativeOrFallbackAsync(resources, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                omitted.Add($"checklist definition: invalid authoritative data ({ex.Message})");
                dirty = true;
            }
        }
        var checklist = definitions == ChecklistCatalog.All
            ? _fallbackChecklistReducer.Reduce(status.Snapshot)
            : new ChecklistReducer(definitions).Reduce(status.Snapshot);
        var coverage = string.IsNullOrWhiteSpace(provenance.CatalogPath)
            ? Array.Empty<CoverageRow>()
            : await _coverageReader.ReadAsync(provenance.CatalogPath, cancellationToken).ConfigureAwait(false);
        if (coverage.Count == 0)
        {
            omitted.Add("coverage catalog: parsed zero rows; readiness denominator is explicitly unavailable");
            dirty = true;
        }
        var readiness = _readinessService.Calculate(coverage);
        var crashSuspected = abnormalProcessExit || crashArtifactSeen || status.Snapshot.CrashSuspected;
        dirty |= crashSuspected;

        if (omitted.Count > 0)
        {
            var omissionPath = Path.Combine(omissionDirectory, "omitted_or_rejected_sources.txt");
            await AtomicFile.WriteTextAsync(omissionPath, string.Join(Environment.NewLine, omitted), cancellationToken)
                .ConfigureAwait(false);
        }

        await AtomicFile.WriteTextAsync(
            Path.Combine(bundleDirectory, "checklist_report.md"), RenderChecklist(checklist), cancellationToken)
            .ConfigureAwait(false);
        await AtomicFile.WriteTextAsync(
            Path.Combine(bundleDirectory, "capability_readiness.md"), RenderReadiness(readiness), cancellationToken)
            .ConfigureAwait(false);
        await AtomicFile.WriteTextAsync(
            Path.Combine(bundleDirectory, "missing_action_list.md"),
            RenderMissingActions(checklist, coverage, omitted), cancellationToken).ConfigureAwait(false);
        var summaryPath = Path.Combine(bundleDirectory, "diagnostic_summary.txt");
        await AtomicFile.WriteTextAsync(
            summaryPath,
            RenderDiagnosticSummary(state, status, checklist, readiness, crashSuspected, dirty, bundleSafety),
            cancellationToken).ConfigureAwait(false);

        var collectedAt = DateTimeOffset.UtcNow;
        var entries = await BuildFileEntriesAsync(bundleDirectory, sourceHashes, cancellationToken).ConfigureAwait(false);
        var manifest = new BundleManifest(
            1,
            "crabruntimeprobe-evidence-bundle-v1",
            state.CampaignId,
            state.CampaignName,
            bundleProfileId,
            state.Generation,
            state.MachineId,
            activeRuntimeSession,
            role,
            state.PreparedAtUtc,
            collectedAt,
            crashSuspected,
            dirty,
            bundleSafety,
            copiedEvidence,
            provenance.CatalogSchemaVersion,
            provenance.CatalogHash,
            true,
            entries);
        await AtomicFile.WriteTextAsync(
            Path.Combine(bundleDirectory, "bundle_manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);

        var zipPath = bundleDirectory + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(bundleDirectory, zipPath, CompressionLevel.Optimal, false);
        return new CollectionResult(bundleDirectory, zipPath, copiedEvidence, crashSuspected, dirty, summaryPath);
    }

    private async Task<ProvenanceResult> CopyProvenanceAsync(
        DashboardResources resources,
        string bundleRoot,
        string destinationRoot,
        IDictionary<string, string> sourceHashes,
        CancellationToken cancellationToken)
    {
        var candidates = Directory.Exists(resources.CampaignRoot)
            ? Directory.EnumerateFiles(resources.CampaignRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                    Path.GetFileName(path).Equals("crabsync_coverage_catalog.json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Contains("full-observe", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Contains("campaign_plan", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("hook_candidate_catalog.json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("hook_validation_ledger.json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("trusted_hook_manifest.json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("hook_quarantine.json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("progressive_observation.defaults.json", StringComparison.OrdinalIgnoreCase))
                .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();
        string catalogPath = string.Empty;
        string profileId = "unknown";
        string schema = "unknown";
        string catalogHash = "unknown";
        var copied = new List<string>();
        foreach (var source in candidates)
        {
            try
            {
                await using var stream = File.OpenRead(source);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var root = document.RootElement;
                var name = Path.GetFileName(source);
                if (name.Contains("profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ProfileIsReadOnly(root)) continue;
                    profileId = Text(root, "id", Text(root, "profileVersion", "unknown"));
                }
                if (name.Equals("crabsync_coverage_catalog.json", StringComparison.OrdinalIgnoreCase))
                {
                    schema = Text(root, "schemaVersion", "unknown");
                    catalogHash = Text(root, "catalogHash", "unknown");
                }

                var destination = Path.Combine(destinationRoot, name);
                await CopyByteForByteAsync(source, destination, cancellationToken).ConfigureAwait(false);
                sourceHashes[Relative(bundleRoot, destination)] = await Sha256Async(source, cancellationToken)
                    .ConfigureAwait(false);
                copied.Add(destination);
                if (name.Equals("crabsync_coverage_catalog.json", StringComparison.OrdinalIgnoreCase))
                    catalogPath = destination;
            }
            catch (JsonException)
            {
                // Invalid provenance is excluded; the missing-denominator path marks the bundle dirty.
            }
        }
        return new ProvenanceResult(catalogPath, profileId, schema, catalogHash, copied);
    }

    private static bool ProfileIsReadOnly(JsonElement root)
    {
        if (!root.TryGetProperty("safety", out var safety) || safety.ValueKind != JsonValueKind.Object) return false;
        foreach (var name in new[]
                 {
                     "writesEnabled", "rpcInvocationEnabled", "propertyMutationEnabled", "hudHookEnabled",
                     "rawIdentityEnabled", "externalRelayEnabled", "syntheticValuesEnabled",
                     "staleUObjectRetentionEnabled"
                 })
            if (!safety.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.False) return false;

        if (!root.TryGetProperty("normalMode", out var normalMode)
            || normalMode.ValueKind != JsonValueKind.Object
            || !IsBoolean(normalMode, "snapshotSamplerEnabled", true)
            || !IsBoolean(normalMode, "gameplayHooksEnabled", false)
            || !IsBoolean(normalMode, "lifecycleHooksEnabled", false)
            || !IsBoolean(normalMode, "runtimeDiscoveryEnabled", false)
            || !IsBoolean(normalMode, "inventoryEscalationEnabled", false))
            return false;
        foreach (var sectionName in new[] { "passiveHooks", "inventoryEscalation", "runtimeDiscovery" })
        {
            if (!root.TryGetProperty(sectionName, out var section)
                || section.ValueKind != JsonValueKind.Object
                || !IsBoolean(section, "enabled", false))
                return false;
        }
        return true;
    }

    private static bool IsBoolean(JsonElement element, string name, bool expected) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean() == expected;

    private static string ActiveProfile(LiveStatusReadResult status)
    {
        if (!status.HasSnapshot) return string.Empty;
        if (!string.IsNullOrWhiteSpace(status.Snapshot.Runtime.ActiveProfile))
            return status.Snapshot.Runtime.ActiveProfile.Trim();
        return status.Snapshot.CampaignId.Trim();
    }

    private static bool IsResearchArtifact(string name) =>
        name.StartsWith("hook_run_manifest_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("hook_run_consumed_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("hook_breadcrumbs_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("hook_run_classification_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("hook_validation_ledger.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("trusted_hook_manifest.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("hook_quarantine.json", StringComparison.OrdinalIgnoreCase);

    private async Task<ResearchSafetyValidation> ValidateControlledResearchAsync(
        LocalCampaignState state,
        DashboardResources? resources,
        CancellationToken cancellationToken)
    {
        if (resources is null)
            return ResearchSafetyValidation.Failed("candidate catalog resources are unavailable");
        if (!Directory.Exists(state.StatusDirectory))
            return ResearchSafetyValidation.Failed("research results directory is unavailable");

        try
        {
            var artifacts = new ResearchArtifactStore();
            var matching = new List<(string Path, HookRunManifest Manifest)>();
            foreach (var path in Directory.EnumerateFiles(
                         state.StatusDirectory, "hook_run_manifest_*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                HookRunManifest parsedManifest;
                try
                {
                    parsedManifest = await artifacts.ReadRunManifestAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or ResearchSchemaException)
                {
                    continue;
                }
                if (parsedManifest.SessionId.Equals(state.SessionId, StringComparison.Ordinal)
                    && parsedManifest.CampaignGeneration == state.Generation
                    && parsedManifest.SelectedRole == state.Role)
                    matching.Add((path, parsedManifest));
            }

            if (matching.Count != 1)
                return ResearchSafetyValidation.Failed(
                    matching.Count == 0
                        ? "no strict run manifest matches this campaign generation"
                        : "multiple run manifests match this campaign generation");

            var (manifestPath, manifest) = matching[0];
            if (!Path.GetFileName(manifestPath).Equals(
                    $"hook_run_manifest_{manifest.RunId}.json", StringComparison.OrdinalIgnoreCase))
                return ResearchSafetyValidation.Failed("run-manifest filename does not match its immutable run ID");
            if (manifest.CreatedAtUtc < state.PreparedAtUtc.AddMinutes(-5)
                || manifest.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
                return ResearchSafetyValidation.Failed("run-manifest time is outside the prepared campaign interval");
            var consumedMarkerValid = await ValidateConsumptionMarkerAsync(
                state.StatusDirectory, manifest, cancellationToken).ConfigureAwait(false);

            var catalog = await artifacts.ReadCatalogAsync(
                Path.Combine(resources.CampaignRoot, "hook_candidate_catalog.json"), cancellationToken)
                .ConfigureAwait(false);
            var depthEnforced = ValidateResearchRegistrationShape(manifest, catalog, out var shapeReason);
            var componentFingerprint = new CompatibilityFingerprintService().Compute(
                manifest.Compatibility.GameBuild,
                manifest.Compatibility.Ue4ssVersion,
                catalog,
                manifest.Compatibility.ComputedAtUtc);
            var compatibilityValidated = manifest.Compatibility.IsComplete
                                         && manifest.Compatibility.CoverageCatalogHash == catalog.CoverageCatalogHash
                                         && manifest.Compatibility.HookCatalogIdentity == catalog.HookCatalogIdentity
                                         && manifest.Compatibility.CallbackImplementationVersion == catalog.CallbackImplementationVersion
                                         && manifest.Compatibility.CallbackSchemaVersion == catalog.CallbackSchemaVersion
                                         && manifest.Compatibility.ValidationBehaviorVersion == catalog.ValidationBehaviorVersion
                                         && componentFingerprint.Fingerprint == manifest.Compatibility.Fingerprint;
            if (compatibilityValidated)
            {
                var gameBinary = ResolveGameBinaryDirectory(state);
                var current = await new CompatibilityFingerprintService().FromInstallationAsync(
                    state.ExecutablePath,
                    Path.Combine(gameBinary, "UE4SS.dll"),
                    catalog,
                    cancellationToken).ConfigureAwait(false);
                compatibilityValidated = current.IsComplete
                                         && current.Fingerprint == manifest.Compatibility.Fingerprint;
            }

            var canaries = manifest.Canary is null ? 0 : 1;
            var reasonParts = new List<string>();
            if (!consumedMarkerValid) reasonParts.Add("the atomic single-process consumption marker is absent or invalid");
            if (!compatibilityValidated) reasonParts.Add("compatibility fingerprint no longer matches the installed game, UE4SS, or catalog");
            if (!depthEnforced) reasonParts.Add(shapeReason);
            return new ResearchSafetyValidation(
                consumedMarkerValid,
                compatibilityValidated,
                depthEnforced,
                canaries,
                reasonParts.Count == 0 ? "strict run manifest and current compatibility validated" : string.Join("; ", reasonParts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or ResearchSchemaException or InvalidDataException)
        {
            return ResearchSafetyValidation.Failed($"research artifact validation failed: {ex.Message}");
        }
    }

    private static async Task<bool> ValidateConsumptionMarkerAsync(
        string statusDirectory,
        HookRunManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(statusDirectory, $"hook_run_consumed_{manifest.RunId}.json");
        if (!File.Exists(path) || new FileInfo(path).Length is <= 1 or > 4096) return false;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            HookCandidateCatalogReader.RequireProperties(root, "run consumption marker",
                "schemaVersion", "runId", "consumedAtUtc", "automaticRearmAllowed");
            var consumedAt = HookCandidateCatalogReader.RequiredDate(root, "consumedAtUtc");
            return HookCandidateCatalogReader.RequiredString(root, "schemaVersion", 96)
                       == ResearchContracts.RunConsumedSchema
                   && HookCandidateCatalogReader.RequiredString(root, "runId", 128) == manifest.RunId
                   && !HookCandidateCatalogReader.RequiredBool(root, "automaticRearmAllowed")
                   && consumedAt >= manifest.CreatedAtUtc.AddMinutes(-1)
                   && consumedAt <= DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or ResearchSchemaException)
        {
            return false;
        }
    }

    private static bool ValidateResearchRegistrationShape(
        HookRunManifest manifest,
        HookCandidateCatalog catalog,
        out string reason)
    {
        var trustedIds = new HashSet<string>(StringComparer.Ordinal);
        var trusted = new List<HookCandidateDefinition>();
        foreach (var selection in manifest.TrustedCandidates)
        {
            if (!trustedIds.Add(selection.CandidateId)
                || !catalog.ById.TryGetValue(selection.CandidateId, out var candidate)
                || candidate.HookPathFingerprint != selection.HookPathFingerprint
                || selection.ValidationDepth is <= HookValidationDepth.StaticCatalogValidation
                    or > HookValidationDepth.FullPassiveEvidence
                || selection.ValidationDepth > candidate.MaximumValidationDepth)
            {
                reason = $"trusted selection '{selection.CandidateId}' is duplicated, unknown, mismatched, or over-depth";
                return false;
            }
            trusted.Add(candidate);
        }

        if (manifest.RunType == ResearchRunType.CanaryOnly && manifest.TrustedCandidates.Count != 0)
        {
            reason = "canary-only run contains trusted-pool registrations";
            return false;
        }

        if (manifest.Canary is { } canary)
        {
            if (trustedIds.Contains(canary.CandidateId)
                || !catalog.ById.TryGetValue(canary.CandidateId, out var candidate)
                || candidate.HookPathFingerprint != canary.HookPathFingerprint
                || canary.ValidationDepth is <= HookValidationDepth.StaticCatalogValidation
                    or > HookValidationDepth.FullPassiveEvidence
                || canary.ValidationDepth > candidate.MaximumValidationDepth)
            {
                reason = "canary is duplicated, unknown, mismatched, or over-depth";
                return false;
            }
        }

        var orderedTrusted = trusted
            .OrderBy(candidate => candidate.OwnerKind == "blueprint" ? 1 : 0)
            .ThenBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Select(candidate => candidate.Id);
        var expectedOrder = new List<string> { "safe-snapshot-baseline" };
        expectedOrder.AddRange(orderedTrusted);
        if (manifest.Canary is not null) expectedOrder.Add(manifest.Canary.CandidateId);
        if (!manifest.RegistrationOrder.SequenceEqual(expectedOrder, StringComparer.Ordinal))
        {
            reason = "registration order is not baseline, deterministic trusted pool, then canary last";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private async Task<CanonicalValidation> ValidateCanonicalAsync(
        string source,
        long expectedGeneration,
        string activeSession,
        bool allowControlledHooks,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        if (info.Length > MaximumCollectedFileBytes)
            return new CanonicalValidation(false, false, "file exceeded the 64 MiB collection cap");
        var text = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
        if (_redactor.ContainsPrivateIdentity(text))
            return new CanonicalValidation(false, false, "raw identity material detected; canonical bytes were omitted");
        try
        {
            var objects = Path.GetExtension(source).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonDocument.Parse(line)).ToArray()
                : new[] { JsonDocument.Parse(text) };
            try
            {
                foreach (var document in objects)
                {
                    var root = document.RootElement;
                    if (ContainsUnsafeFlag(root, allowControlledHooks))
                        return new CanonicalValidation(
                            false,
                            false,
                            "unsafe write/RPC/hook/discovery/inventory-stage/raw-identity flag detected");
                    var generation = FindInt64(root, "campaignGeneration", "generation");
                    if (generation is not null && expectedGeneration > 0 && generation != expectedGeneration)
                        return new CanonicalValidation(false, true, "belongs to a prior campaign generation");
                    var session = FindText(root, "sessionId", "session");
                    if (!string.IsNullOrWhiteSpace(session) && !string.IsNullOrWhiteSpace(activeSession)
                        && !session.Equals(activeSession, StringComparison.OrdinalIgnoreCase))
                        return new CanonicalValidation(false, true, "belongs to another runtime session");
                }
            }
            finally
            {
                foreach (var document in objects) document.Dispose();
            }
        }
        catch (JsonException ex)
        {
            return new CanonicalValidation(false, false, $"invalid JSON ({ex.Message})");
        }
        return new CanonicalValidation(true, false, string.Empty);
    }

    private static bool ContainsUnsafeFlag(JsonElement element, bool allowControlledHooks)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                if (property.Value.ValueKind == JsonValueKind.True
                    && (normalized is "allowwriteprobes" or "allowrpcprobes" or "allowhudtickhook"
                        or "allowrawidentityevidence" or "writesenabled" or "rpcinvocationenabled"
                        or "propertymutationenabled" or "hudhookenabled" or "rawidentityenabled"
                        or "allowfullobserveruntimediscovery" or "allowfullobserveinventorystages"
                        or "runtimediscoveryenabled" or "inventorystagesenabled" or "inventoryescalationenabled"
                        || !allowControlledHooks && normalized is "allowpassiveobservationhooks" or "hooksenabled"
                            or "gameplayhooksenabled" or "lifecyclehooksenabled"
                            or "progressiveobservationenabled" or "progressivehooksarmed"
                            or "reliccountvalidationenabled"))
                    return true;
                if (property.Value.ValueKind == JsonValueKind.False
                    && (normalized is "writesdisabled" or "rpccallsdisabled" or "rpcsdisabled"
                        or "mutationdisabled" or "hudhookdisabled" or "rawidentitydisabled"
                        or "runtimediscoverydisabled" or "inventorystagesdisabled"
                        || !allowControlledHooks && normalized == "hooksdisabled"))
                    return true;
                if (ContainsUnsafeFlag(property.Value, allowControlledHooks)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (ContainsUnsafeFlag(item, allowControlledHooks)) return true;
        }
        return false;
    }

    private static long? FindInt64(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return null;
    }

    private static string FindText(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static IEnumerable<string> EnumerateApprovedScriptEvidence(LocalCampaignState state)
    {
        var scripts = Path.GetDirectoryName(state.StatusDirectory) ?? state.StatusDirectory;
        var roots = new[] { state.StatusDirectory, scripts }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("access_evidence_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("probe_results_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("session_manifest_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("hook_run_manifest_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("hook_run_consumed_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("hook_breadcrumbs_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("hook_run_classification_", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("hook_validation_ledger.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("trusted_hook_manifest.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("hook_quarantine.json", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("live_status", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("full_observe_progress.txt", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("full_observe_sequence.txt", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("CrabRuntimeProbe.log", StringComparison.OrdinalIgnoreCase))
                    yield return path;
            }
        }
    }

    private static bool IsCanonicalEvidence(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("access_evidence_", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("probe_results_", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("session_manifest_", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CopyRedactedAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (new FileInfo(source).Length > MaximumCollectedFileBytes) return false;
        var text = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
        await AtomicFile.WriteTextAsync(destination, _redactor.Redact(text), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task CopyByteForByteAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> WriteCrashMetadataAsync(
        string source, string crashDirectory, CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        var hash = await Sha256Async(source, cancellationToken).ConfigureAwait(false);
        var text = $"name={SafeName(info.Name)}{Environment.NewLine}bytes={info.Length}{Environment.NewLine}"
                   + $"lastWriteUtc={info.LastWriteTimeUtc:O}{Environment.NewLine}sha256={hash}{Environment.NewLine}"
                   + "The raw crash artifact is intentionally not exported because it may contain private identity data.";
        var output = UniqueFile(Path.Combine(crashDirectory, SafeName(info.Name) + ".metadata.txt"));
        await AtomicFile.WriteTextAsync(output, text, cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static IEnumerable<string> EnumerateRecentCrashArtifacts(LocalCampaignState state)
    {
        var threshold = state.PreparedAtUtc.UtcDateTime.AddMinutes(-5);
        var roots = new[]
        {
            Path.Combine(state.GameDirectory, "CrabChampions", "Saved", "Crashes"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrabChampions", "Saved", "Crashes")
        };
        return roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => new FileInfo(path).LastWriteTimeUtc >= threshold)
            .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc);
    }

    private static IEnumerable<string> EnumerateKnownLogs(string gameBinary)
    {
        var candidates = new[]
        {
            Path.Combine(gameBinary, "UE4SS.log"),
            Path.Combine(gameBinary, "ue4ss", "UE4SS.log"),
            Path.Combine(gameBinary, "Mods", "CrabRuntimeProbe", "CrabRuntimeProbe.log"),
            Path.Combine(gameBinary, "Mods", "CrabRuntimeProbe", "Scripts", "CrabRuntimeProbe.log")
        };
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveGameBinaryDirectory(LocalCampaignState state)
    {
        var nested = Path.Combine(state.GameDirectory, "CrabChampions", "Binaries", "Win64");
        return Directory.Exists(nested) ? nested : Path.GetDirectoryName(state.ExecutablePath) ?? state.GameDirectory;
    }

    private static async Task<IReadOnlyList<BundleFileEntry>> BuildFileEntriesAsync(
        string bundleRoot,
        IReadOnlyDictionary<string, string> sourceHashes,
        CancellationToken cancellationToken)
    {
        var entries = new List<BundleFileEntry>();
        foreach (var path in Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetFileName(path).Equals("bundle_manifest.json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Relative(bundleRoot, path);
            var hash = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
            entries.Add(new BundleFileEntry(
                relative,
                new FileInfo(path).Length,
                hash,
                sourceHashes.TryGetValue(relative, out var sourceHash) ? sourceHash : hash,
                Kind(relative)));
        }
        return entries;
    }

    private static string Kind(string path) => path.StartsWith("evidence/canonical/", StringComparison.OrdinalIgnoreCase)
        ? "canonical-byte-copy"
        : path.StartsWith("provenance/", StringComparison.OrdinalIgnoreCase)
            ? "provenance-byte-copy"
            : path.StartsWith("crash-metadata/", StringComparison.OrdinalIgnoreCase)
                ? "crash-metadata-only"
                : path.Contains("redacted", StringComparison.OrdinalIgnoreCase)
                    ? "redacted-derivative"
                    : "generated-report";

    private static string RenderChecklist(IReadOnlyList<ChecklistViewItem> items)
    {
        var builder = new StringBuilder("# CrabSync Full-Observe Checklist\n\n");
        foreach (var group in items.GroupBy(item => item.Group))
        {
            builder.Append("## ").Append(group.Key).Append("\n\n");
            foreach (var item in group)
                builder.Append("- ").Append(item.IsComplete ? "[x] " : "[ ] ").Append(item.Label)
                    .Append(" - `").Append(item.State).Append("`; observations=").Append(item.ObservationCount)
                    .Append("; next: ").Append(item.Instruction).Append('\n');
            builder.Append('\n');
        }
        return builder.ToString();
    }

    internal static string RenderReadiness(IReadOnlyList<CapabilityReadiness> readiness)
    {
        var builder = new StringBuilder("# CrabSync Capability Readiness\n\n");
        builder.Append("Passive observation never proves write/apply safety. A zero-row capability is incomplete, not zero-of-zero complete.\n\n");
        builder.Append("| Capability | Complete evidence coverage | Closed/total | Summary |\n|---|---|---:|---|\n");
        foreach (var item in readiness)
            builder.Append("| ").Append(item.Category).Append(" | ").Append(item.Complete ? "yes" : "no")
                .Append(" | ").Append(item.ClosedRows).Append('/').Append(item.TotalRows).Append(" | ")
                .Append(item.Summary.Replace('|', '/')).Append(" |\n");
        return builder.ToString();
    }

    private static string RenderMissingActions(
        IReadOnlyList<ChecklistViewItem> checklist,
        IReadOnlyList<CoverageRow> coverage,
        IReadOnlyList<string> omissions)
    {
        var builder = new StringBuilder("# Missing actions and coverage\n\n");
        if (omissions.Count > 0)
        {
            builder.Append("## Collection omissions\n\n");
            foreach (var omission in omissions) builder.Append("- ").Append(omission).Append('\n');
            builder.Append('\n');
        }
        builder.Append("## Checklist actions\n\n");
        foreach (var item in checklist.Where(item => !item.IsComplete))
            builder.Append("- `").Append(item.Id).Append("`: ").Append(item.Instruction).Append('\n');
        builder.Append("\n## Catalog rows needing coverage\n\n");
        if (coverage.Count == 0) builder.Append("- Coverage denominator unavailable; restore the authoritative catalog.\n");
        foreach (var row in coverage.Where(row => row.NeedsCoverage).Take(500))
            builder.Append("- `").Append(row.RowId).Append("` (").Append(row.Category).Append("): ")
                .Append(row.NextRequiredObservation).Append('\n');
        if (coverage.Count(row => row.NeedsCoverage) > 500)
            builder.Append("- Additional rows are available in the bundled authoritative coverage catalog.\n");
        return builder.ToString();
    }

    private static BundleSafety SafetyFrom(
        LiveStatusReadResult status,
        ResearchSafetyValidation research)
    {
        if (!status.HasSnapshot)
            return new BundleSafety(false, false, false, false, false, false, false, false,
                false, false, false, 0);
        var safety = status.Snapshot.Safety;
        return new BundleSafety(
            safety.WritesDisabled,
            safety.RpcsDisabled,
            safety.MutationDisabled,
            safety.RawIdentityDisabled,
            safety.HudHookDisabled,
            safety.HooksDisabled,
            safety.RuntimeDiscoveryDisabled,
            safety.InventoryStagesDisabled,
            research.ControlledResearchHooks,
            research.CompatibilityValidated,
            research.TrustedDepthEnforced,
            research.ActiveCanaries);
    }

    private static string RenderDiagnosticSummary(
        LocalCampaignState state,
        LiveStatusReadResult status,
        IReadOnlyList<ChecklistViewItem> checklist,
        IReadOnlyList<CapabilityReadiness> readiness,
        bool crashSuspected,
        bool dirty,
        BundleSafety safety)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"campaign={state.CampaignName}");
        builder.AppendLine($"sessionId={state.SessionId}");
        builder.AppendLine($"role={state.Role.ToContract()}");
        builder.AppendLine($"statusSequence={status.Snapshot.Sequence}");
        builder.AppendLine($"statusStale={status.IsStale.ToString().ToLowerInvariant()}");
        builder.AppendLine($"crashSuspected={crashSuspected.ToString().ToLowerInvariant()}");
        builder.AppendLine($"dirtyEvidence={dirty.ToString().ToLowerInvariant()}");
        builder.AppendLine($"checklistComplete={checklist.Count(item => item.IsComplete)}");
        builder.AppendLine($"checklistMissing={checklist.Count(item => !item.IsComplete)}");
        builder.AppendLine($"writesDisabled={safety.WritesDisabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"rpcsDisabled={safety.RpcCallsDisabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"hooksDisabled={safety.HooksDisabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"runtimeDiscoveryDisabled={safety.RuntimeDiscoveryDisabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"inventoryStagesDisabled={safety.InventoryStagesDisabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"mutationDisabled={safety.MutationDisabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"controlledResearchHooks={safety.ControlledResearchHooks.ToString().ToLowerInvariant()}");
        builder.AppendLine($"compatibilityValidated={safety.CompatibilityValidated.ToString().ToLowerInvariant()}");
        builder.AppendLine($"trustedDepthEnforced={safety.TrustedDepthEnforced.ToString().ToLowerInvariant()}");
        builder.AppendLine($"activeCanaries={safety.ActiveCanaries}");
        builder.AppendLine("passiveCampaignIsNotWriteApplyProof=true");
        foreach (var item in readiness)
            builder.AppendLine($"capability.{SafeName(item.Category)}={(item.Complete ? "complete" : "incomplete")}");
        if (!string.IsNullOrEmpty(status.Error)) builder.AppendLine($"statusWarning={status.Error}");
        return builder.ToString();
    }

    private static string Text(JsonElement element, string name, string fallback) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static string UniqueDirectory(string desired) => !Directory.Exists(desired)
        ? desired
        : desired + $"-{DateTimeOffset.UtcNow:HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

    private static string UniqueFile(string desired)
    {
        if (!File.Exists(desired)) return desired;
        var directory = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        return Path.Combine(directory, $"{name}-{Guid.NewGuid().ToString("N")[..6]}{extension}");
    }

    internal static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var output = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(output) ? "unknown" : output;
    }

    internal static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    internal static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private sealed record CanonicalValidation(bool Accepted, bool UnrelatedSession, string Reason);
    private sealed record ResearchSafetyValidation(
        bool ControlledResearchHooks,
        bool CompatibilityValidated,
        bool TrustedDepthEnforced,
        int ActiveCanaries,
        string Reason)
    {
        public static ResearchSafetyValidation NotApplicable { get; } = new(
            false, false, false, 0, "normal hook-free profile");

        public static ResearchSafetyValidation Failed(string reason) => new(
            false, false, false, 0, reason);
    }

    private sealed record ProvenanceResult(
        string CatalogPath,
        string ProfileId,
        string CatalogSchemaVersion,
        string CatalogHash,
        IReadOnlyList<string> CopiedPaths);
}

public sealed class BundleCorrelationService
{
    private const long MaximumExpandedBundleBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<CorrelationResult> CombineAsync(
        IReadOnlyList<string> zipPaths,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        if (zipPaths.Count < 2) throw new ArgumentException("Select at least two exported evidence ZIPs.", nameof(zipPaths));
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "CrabRuntimeProbeCombine", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var validated = new List<ValidatedBundle>();
            for (var index = 0; index < zipPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extraction = Path.Combine(temporaryRoot, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(extraction);
                ExtractSafely(zipPaths[index], extraction);
                var manifestPath = Directory.EnumerateFiles(extraction, "bundle_manifest.json", SearchOption.AllDirectories)
                    .SingleOrDefault() ?? throw new InvalidDataException($"Bundle must contain exactly one manifest: {zipPaths[index]}");
                await using var stream = File.OpenRead(manifestPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                ValidateManifestShape(document.RootElement);
                var manifest = document.RootElement.Deserialize<BundleManifest>(JsonOptions)
                               ?? throw new InvalidDataException($"Bundle manifest is invalid: {zipPaths[index]}");
                var bundleRoot = Path.GetDirectoryName(manifestPath)!;
                await ValidateManifestAsync(manifest, bundleRoot, cancellationToken).ConfigureAwait(false);
                validated.Add(new ValidatedBundle(Path.GetFullPath(zipPaths[index]), bundleRoot, manifest));
            }

            var manifests = validated.Select(item => item.Manifest).ToArray();
            var hasHost = manifests.Any(item => item.SelectedRole.Equals("host", StringComparison.OrdinalIgnoreCase));
            var hasJoined = manifests.Any(item => item.SelectedRole.Equals("joined-client", StringComparison.OrdinalIgnoreCase));
            var campaignIdMatches = OneValue(manifests.Select(item => item.CampaignId));
            var campaignNameMatches = OneValue(manifests.Select(item => NormalizeName(item.CampaignName)));
            var schemaMatches = manifests.All(item => item.SchemaVersion == 1
                && item.BundleFormat.Equals("crabruntimeprobe-evidence-bundle-v1", StringComparison.OrdinalIgnoreCase));
            var catalogMatches = OneValue(manifests.Select(item => item.CatalogSchemaVersion))
                                 && OneValue(manifests.Select(item => item.CatalogHash))
                                 && !manifests.Any(item => item.CatalogHash.Equals("unknown", StringComparison.OrdinalIgnoreCase));
            var profileMatches = OneValue(manifests.Select(item => item.ProfileId))
                                 && !manifests.Any(item => item.ProfileId.Equals("unknown", StringComparison.OrdinalIgnoreCase));
            var distinctMachines = manifests.Select(item => item.MachineId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == manifests.Length;
            var distinctSessions = manifests.Select(item => item.SessionId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == manifests.Length;
            var intervalsOverlap = manifests.Max(item => item.PreparedAtUtc) <= manifests.Min(item => item.CollectedAtUtc);
            var clean = manifests.All(item => !item.DirtyEvidence && !item.CrashSuspected);
            var campaignMatches = campaignIdMatches && campaignNameMatches;
            var correlated = hasHost && hasJoined && campaignMatches && schemaMatches && catalogMatches
                             && profileMatches && distinctMachines && distinctSessions && intervalsOverlap && clean;

            var destination = Path.Combine(Path.GetFullPath(outputRoot),
                $"CrabRuntimeProbe-combined-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
            Directory.CreateDirectory(destination);
            var originals = Path.Combine(destination, "source-bundles");
            Directory.CreateDirectory(originals);
            for (var index = 0; index < validated.Count; index++)
            {
                var source = validated[index].ZipPath;
                File.Copy(source, Path.Combine(originals, $"{index + 1}-{Path.GetFileName(source)}"), true);
            }

            var coveragePath = validated.Select(item => Path.Combine(item.BundleRoot, "provenance", "crabsync_coverage_catalog.json"))
                .FirstOrDefault(File.Exists);
            var readiness = coveragePath is null
                ? new CapabilityReadinessService().Calculate(Array.Empty<CoverageRow>())
                : new CapabilityReadinessService().Calculate(
                    await new CoverageCatalogReader().ReadAsync(coveragePath, cancellationToken).ConfigureAwait(false));
            await AtomicFile.WriteTextAsync(
                Path.Combine(destination, "combined_capability_readiness.md"),
                EvidenceCollector.RenderReadiness(readiness), cancellationToken).ConfigureAwait(false);

            var reportPath = Path.Combine(destination, "host_client_correlation.md");
            await AtomicFile.WriteTextAsync(
                reportPath,
                RenderCorrelation(manifests, hasHost, hasJoined, campaignIdMatches, campaignNameMatches,
                    schemaMatches, catalogMatches, profileMatches, distinctMachines, distinctSessions,
                    intervalsOverlap, clean, correlated), cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteTextAsync(
                Path.Combine(destination, "correlation.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    hasHost,
                    hasJoinedClient = hasJoined,
                    campaignIdMatches,
                    campaignNameMatches,
                    schemaMatches,
                    catalogMatches,
                    profileMatches,
                    distinctMachines,
                    distinctSessions,
                    intervalsOverlap,
                    allEvidenceClean = clean,
                    correlationEstablished = correlated,
                    offlinePairingDoesNotProveRemoteVisibility = true,
                    manifests
                }, JsonOptions), cancellationToken).ConfigureAwait(false);

            var outputZip = destination + ".zip";
            ZipFile.CreateFromDirectory(destination, outputZip, CompressionLevel.Optimal, false);
            return new CorrelationResult(hasHost, hasJoined, campaignMatches, correlated,
                reportPath, outputZip, manifests);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true);
        }
    }

    private static async Task ValidateManifestAsync(
        BundleManifest manifest, string bundleRoot, CancellationToken cancellationToken)
    {
        if (manifest.SchemaVersion != 1
            || !manifest.BundleFormat.Equals("crabruntimeprobe-evidence-bundle-v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported evidence bundle schema or format.");
        if (manifest.CampaignGeneration < 1 || string.IsNullOrWhiteSpace(manifest.CampaignId)
            || string.IsNullOrWhiteSpace(manifest.CampaignName) || string.IsNullOrWhiteSpace(manifest.MachineId)
            || string.IsNullOrWhiteSpace(manifest.SessionId))
            throw new InvalidDataException("Manifest campaign/session identity fields are incomplete.");
        if (manifest.SelectedRole is not ("host" or "joined-client"))
            throw new InvalidDataException("Manifest selectedRole is invalid.");
        if (manifest.PreparedAtUtc > manifest.CollectedAtUtc)
            throw new InvalidDataException("Manifest capture interval is inverted.");
        if (manifest.ProfileId is not ("crabsync-full-observe" or "progressive-broad-observation")
            || !IsSha256(manifest.CatalogHash))
            throw new InvalidDataException("Manifest profile/catalog identity is invalid.");
        if (!manifest.ManifestSelfExcluded) throw new InvalidDataException("Manifest must declare its self-exclusion.");
        if (manifest.Safety is null || !manifest.Safety.IsAcceptableForProfile(manifest.ProfileId))
            throw new InvalidDataException("Bundle safety contract is absent or permits an unsafe operation.");
        if (manifest.Files is null) throw new InvalidDataException("Manifest has no file inventory.");
        var expected = manifest.Files.ToDictionary(item => NormalizeRelative(item.Path), StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(bundleRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("bundle_manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => NormalizeRelative(Path.GetRelativePath(bundleRoot, path)), StringComparer.OrdinalIgnoreCase);
        if (!expected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual.Keys))
            throw new InvalidDataException("Manifest file inventory does not match bundle contents.");
        foreach (var pair in expected)
        {
            var entry = pair.Value;
            if (!IsSha256(entry.Hash) || !IsSha256(entry.SourceHash))
                throw new InvalidDataException($"Invalid SHA-256 field: {pair.Key}");
            if (entry.Kind is not ("canonical-byte-copy" or "provenance-byte-copy" or "crash-metadata-only"
                or "redacted-derivative" or "generated-report"))
                throw new InvalidDataException($"Invalid bundle file kind: {pair.Key}");
            var path = actual[pair.Key];
            var info = new FileInfo(path);
            if (entry.SizeBytes != info.Length) throw new InvalidDataException($"Size mismatch: {pair.Key}");
            var hash = await EvidenceCollector.Sha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!hash.Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 mismatch: {pair.Key}");
        }
    }

    private static void ValidateManifestShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("safety", out var safety)
            || safety.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Bundle manifest safety object is missing.");
        foreach (var name in new[]
                 {
                     "writesDisabled", "rpcCallsDisabled", "mutationDisabled", "rawIdentityDisabled",
                     "hudHookDisabled", "hooksDisabled", "runtimeDiscoveryDisabled", "inventoryStagesDisabled",
                     "controlledResearchHooks", "compatibilityValidated", "trustedDepthEnforced"
                 })
            if (!safety.TryGetProperty(name, out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Bundle safety field '{name}' is missing or invalid.");
        if (!safety.TryGetProperty("activeCanaries", out var canaries)
            || canaries.ValueKind != JsonValueKind.Number
            || !canaries.TryGetInt32(out var count)
            || count is < 0 or > 1)
            throw new InvalidDataException("Bundle safety field 'activeCanaries' is missing or invalid.");
    }

    private static string NormalizeRelative(string path)
    {
        if (Path.IsPathRooted(path)) throw new InvalidDataException("Manifest paths must be relative.");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new InvalidDataException($"Unsafe manifest path: {path}");
        return normalized;
    }

    private static bool IsSha256(string value) => value.Length == 64
        && value.All(character => char.IsAsciiHexDigit(character));

    private static void ExtractSafely(string zipPath, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long expanded = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            expanded += entry.Length;
            if (expanded > MaximumExpandedBundleBytes) throw new InvalidDataException("Expanded bundle exceeds 512 MiB cap.");
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe ZIP entry: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static bool OneValue(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string RenderCorrelation(
        IReadOnlyList<BundleManifest> manifests,
        bool hasHost,
        bool hasJoined,
        bool campaignIdMatches,
        bool campaignNameMatches,
        bool schemaMatches,
        bool catalogMatches,
        bool profileMatches,
        bool distinctMachines,
        bool distinctSessions,
        bool intervalsOverlap,
        bool clean,
        bool correlated)
    {
        var builder = new StringBuilder("# Host/joined-client correlation\n\n");
        foreach (var pair in new[]
                 {
                     ("Host bundle present", hasHost), ("Joined-client bundle present", hasJoined),
                     ("Campaign IDs match", campaignIdMatches), ("Normalized campaign names match", campaignNameMatches),
                     ("Bundle schemas compatible", schemaMatches), ("Catalog schema/hash compatible", catalogMatches),
                     ("Profiles compatible", profileMatches), ("Machine IDs unique", distinctMachines),
                     ("Runtime session IDs unique", distinctSessions), ("Capture intervals overlap", intervalsOverlap),
                     ("All bundles clean and crash-free", clean), ("Correlation established", correlated)
                 })
            builder.Append("- ").Append(pair.Item1).Append(": ").Append(pair.Item2 ? "yes" : "no").Append('\n');
        builder.Append("\nOffline pairing proves only that compatible host and joined-client bundles overlap. It does not itself prove remote visibility; qualifying row-level evidence is still required.\n\n");
        builder.Append("| Role | Session | Machine | Generation | Crash | Dirty | Prepared | Collected |\n")
            .Append("|---|---|---|---:|---|---|---|---|\n");
        foreach (var manifest in manifests)
            builder.Append("| ").Append(manifest.SelectedRole).Append(" | `").Append(manifest.SessionId).Append("` | `")
                .Append(manifest.MachineId).Append("` | ").Append(manifest.CampaignGeneration).Append(" | ")
                .Append(manifest.CrashSuspected ? "yes" : "no").Append(" | ")
                .Append(manifest.DirtyEvidence ? "yes" : "no").Append(" | ")
                .Append(manifest.PreparedAtUtc.ToString("O")).Append(" | ")
                .Append(manifest.CollectedAtUtc.ToString("O")).Append(" |\n");
        builder.Append("\nMachine IDs are dashboard-generated random identifiers; no Steam names or IDs are retained.\n");
        return builder.ToString();
    }

    private sealed record ValidatedBundle(string ZipPath, string BundleRoot, BundleManifest Manifest);
}

public static class SupportSummary
{
    public static string Create(LocalCampaignState? campaign, LiveStatusReadResult status)
    {
        var snapshot = status.Snapshot;
        return string.Join(Environment.NewLine, new[]
        {
            $"CrabRuntimeProbe {campaign?.CampaignName ?? snapshot.CampaignName}",
            $"session={campaign?.SessionId ?? snapshot.SessionId} role={(campaign?.Role ?? snapshot.SelectedRole).ToContract()}",
            $"game={snapshot.Runtime.GameProcessState} ue4ss={snapshot.Runtime.Ue4ssState} probe={snapshot.Runtime.RuntimeProbeState}",
            $"lifecycle={snapshot.Lifecycle.State} stage={snapshot.Runtime.CurrentProbeStage} sequence={snapshot.Sequence} stale={status.IsStale}",
            $"evidence={snapshot.EvidenceHealth.State} dirty={snapshot.DirtyEvidence || status.UsedLastGood} crashSuspected={snapshot.CrashSuspected}",
            $"safety=writes:{snapshot.Safety.WritesDisabled},rpcs:{snapshot.Safety.RpcsDisabled},mutation:{snapshot.Safety.MutationDisabled},hooks:{snapshot.Safety.HooksDisabled},hud:{snapshot.Safety.HudHookDisabled},identity:{snapshot.Safety.RawIdentityDisabled}"
        });
    }
}
