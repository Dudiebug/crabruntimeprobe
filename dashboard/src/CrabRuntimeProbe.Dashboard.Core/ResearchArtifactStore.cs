using System.Text;
using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class ResearchArtifactStore
{
    private const int MaximumArtifactBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task<HookCandidateCatalog> ReadCatalogAsync(string path, CancellationToken cancellationToken = default) =>
        new HookCandidateCatalogReader().ReadAsync(path, cancellationToken);

    public async Task<HookValidationLedger> ReadLedgerAsync(string path, CancellationToken cancellationToken = default)
    {
        using var document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        HookCandidateCatalogReader.RequireProperties(root, "validation ledger",
            "schemaVersion", "generatedAtUtc", "updatedAtUtc", "coverageCatalogHash", "hookCatalogIdentity",
            "callbackImplementationVersion", "callbackSchemaVersion", "validationBehaviorVersion",
            "initialMigrationPolicy", "candidates");
        if (HookCandidateCatalogReader.RequiredString(root, "schemaVersion", 96) != ResearchContracts.LedgerSchema)
            throw new ResearchSchemaException("Unsupported validation-ledger schema.");
        _ = HookCandidateCatalogReader.RequiredDate(root, "generatedAtUtc");
        _ = HookCandidateCatalogReader.RequiredString(root, "initialMigrationPolicy", 1024);
        var candidates = new List<CandidateValidationRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in HookCandidateCatalogReader.RequiredArray(root, "candidates").EnumerateArray())
        {
            if (candidates.Count >= 512) throw new ResearchSchemaException("Validation ledger exceeds its candidate cap.");
            HookCandidateCatalogReader.RequireProperties(element, "validation candidate",
                "candidateId", "hookPathFingerprint", "state", "highestValidatedDepth", "trustedDepth",
                "cleanRuns", "naturalCallbacks", "hostCleanRuns", "joinedClientCleanRuns",
                "lifecycleTransitionRuns", "evidenceSessions", "legacyObservationMigrated",
                "legacyObservationTrusted", "crashSuspectRuns", "compatibilityFingerprint",
                "hasUnmatchedBreadcrumb", "hasCorrelatedCrash", "hasNewUe4ssCallbackError",
                "reducerFixtureCovered");
            var id = SafeCandidateId(element, "candidateId");
            if (!ids.Add(id)) throw new ResearchSchemaException($"Validation ledger duplicates '{id}'.");
            var trustedDepth = NullableDepth(element, "trustedDepth");
            var compatibility = HookCandidateCatalogReader.String(element, "compatibilityFingerprint", 64);
            if (compatibility.Length > 0 && !ResearchContracts.IsSha256(compatibility))
                throw new ResearchSchemaException($"Candidate '{id}' has an invalid compatibility fingerprint.");
            candidates.Add(new CandidateValidationRecord(
                id,
                HookCandidateCatalogReader.RequiredHash(element, "hookPathFingerprint"),
                ParseCandidateState(HookCandidateCatalogReader.RequiredString(element, "state", 64)),
                (HookValidationDepth)HookCandidateCatalogReader.RequiredInt(element, "highestValidatedDepth", 0, 7),
                trustedDepth,
                HookCandidateCatalogReader.RequiredInt(element, "cleanRuns", 0, 100000),
                HookCandidateCatalogReader.RequiredInt(element, "naturalCallbacks", 0, 1000000),
                HookCandidateCatalogReader.RequiredInt(element, "hostCleanRuns", 0, 100000),
                HookCandidateCatalogReader.RequiredInt(element, "joinedClientCleanRuns", 0, 100000),
                HookCandidateCatalogReader.RequiredInt(element, "lifecycleTransitionRuns", 0, 100000),
                StringArray(element, "evidenceSessions", 4096),
                HookCandidateCatalogReader.RequiredBool(element, "legacyObservationMigrated"),
                HookCandidateCatalogReader.RequiredBool(element, "legacyObservationTrusted"),
                StringArray(element, "crashSuspectRuns", 4096),
                compatibility,
                HookCandidateCatalogReader.RequiredBool(element, "hasUnmatchedBreadcrumb"),
                HookCandidateCatalogReader.RequiredBool(element, "hasCorrelatedCrash"),
                HookCandidateCatalogReader.RequiredBool(element, "hasNewUe4ssCallbackError"),
                HookCandidateCatalogReader.RequiredBool(element, "reducerFixtureCovered")));
        }
        if (candidates.Any(candidate => candidate.LegacyObservationTrusted))
            throw new ResearchSchemaException("Legacy observations must never be imported as trust.");
        return new HookValidationLedger(
            ResearchContracts.LedgerSchema,
            HookCandidateCatalogReader.RequiredHash(root, "coverageCatalogHash"),
            HookCandidateCatalogReader.RequiredHash(root, "hookCatalogIdentity"),
            HookCandidateCatalogReader.RequiredToken(root, "callbackImplementationVersion", 96),
            HookCandidateCatalogReader.RequiredToken(root, "callbackSchemaVersion", 96),
            HookCandidateCatalogReader.RequiredToken(root, "validationBehaviorVersion", 96),
            HookCandidateCatalogReader.RequiredDate(root, "updatedAtUtc"), candidates);
    }

    public async Task<TrustedHookManifest> ReadTrustedManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        HookCandidateCatalogReader.RequireProperties(root, "trusted manifest",
            "schemaVersion", "generatedAtUtc", "coverageCatalogHash", "hookCatalogIdentity",
            "callbackImplementationVersion", "callbackSchemaVersion", "validationBehaviorVersion",
            "compatibilityFingerprint", "generatedFromLedgerAtUtc", "candidates");
        if (HookCandidateCatalogReader.RequiredString(root, "schemaVersion", 96) != ResearchContracts.TrustedManifestSchema)
            throw new ResearchSchemaException("Unsupported trusted-manifest schema.");
        _ = HookCandidateCatalogReader.RequiredDate(root, "generatedAtUtc");
        var compatibility = HookCandidateCatalogReader.String(root, "compatibilityFingerprint", 64);
        if (compatibility.Length > 0 && !ResearchContracts.IsSha256(compatibility))
            throw new ResearchSchemaException("Trusted manifest compatibility fingerprint is invalid.");
        var candidates = new List<TrustedHookEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in HookCandidateCatalogReader.RequiredArray(root, "candidates").EnumerateArray())
        {
            if (candidates.Count >= 111) throw new ResearchSchemaException("Trusted manifest exceeds the catalog cap.");
            HookCandidateCatalogReader.RequireProperties(element, "trusted entry",
                "candidateId", "hookPathFingerprint", "trustedDepth", "compatibilityFingerprint");
            var id = SafeCandidateId(element, "candidateId");
            if (!ids.Add(id)) throw new ResearchSchemaException($"Trusted manifest duplicates '{id}'.");
            candidates.Add(new TrustedHookEntry(id,
                HookCandidateCatalogReader.RequiredHash(element, "hookPathFingerprint"),
                (HookValidationDepth)HookCandidateCatalogReader.RequiredInt(element, "trustedDepth", 1, 7),
                HookCandidateCatalogReader.RequiredHash(element, "compatibilityFingerprint")));
        }
        return new TrustedHookManifest(
            ResearchContracts.TrustedManifestSchema,
            HookCandidateCatalogReader.RequiredHash(root, "coverageCatalogHash"),
            HookCandidateCatalogReader.RequiredHash(root, "hookCatalogIdentity"),
            HookCandidateCatalogReader.RequiredToken(root, "callbackImplementationVersion", 96),
            HookCandidateCatalogReader.RequiredToken(root, "callbackSchemaVersion", 96),
            HookCandidateCatalogReader.RequiredToken(root, "validationBehaviorVersion", 96),
            compatibility,
            HookCandidateCatalogReader.RequiredDate(root, "generatedFromLedgerAtUtc"), candidates);
    }

    public async Task<HookQuarantineState> ReadQuarantineAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        HookCandidateCatalogReader.RequireProperties(root, "quarantine state",
            "schemaVersion", "generatedAtUtc", "updatedAtUtc", "coverageCatalogHash", "hookCatalogIdentity",
            "callbackImplementationVersion", "callbackSchemaVersion", "validationBehaviorVersion", "entries");
        if (HookCandidateCatalogReader.RequiredString(root, "schemaVersion", 96) != ResearchContracts.QuarantineSchema)
            throw new ResearchSchemaException("Unsupported quarantine schema.");
        _ = HookCandidateCatalogReader.RequiredDate(root, "generatedAtUtc");
        _ = HookCandidateCatalogReader.RequiredDate(root, "updatedAtUtc");
        _ = HookCandidateCatalogReader.RequiredHash(root, "coverageCatalogHash");
        _ = HookCandidateCatalogReader.RequiredToken(root, "callbackImplementationVersion", 96);
        _ = HookCandidateCatalogReader.RequiredToken(root, "callbackSchemaVersion", 96);
        _ = HookCandidateCatalogReader.RequiredToken(root, "validationBehaviorVersion", 96);
        var entries = new List<HookQuarantineEntry>();
        foreach (var element in HookCandidateCatalogReader.RequiredArray(root, "entries").EnumerateArray())
        {
            if (entries.Count >= 512) throw new ResearchSchemaException("Quarantine state exceeds its cap.");
            HookCandidateCatalogReader.RequireProperties(element, "quarantine entry",
                "candidateId", "hookPathFingerprint", "validationDepth", "state", "reason", "runId",
                "quarantinedAtUtc", "explicitRetryRequired", "automaticRearmAllowed");
            var state = ParseCandidateState(HookCandidateCatalogReader.RequiredString(element, "state", 32));
            if (state is not (HookCandidateState.Quarantined or HookCandidateState.CrashSuspect))
                throw new ResearchSchemaException("Quarantine entry has an unsafe state.");
            var explicitRetry = HookCandidateCatalogReader.RequiredBool(element, "explicitRetryRequired");
            var autoRearm = HookCandidateCatalogReader.RequiredBool(element, "automaticRearmAllowed");
            if (!explicitRetry || autoRearm) throw new ResearchSchemaException("Quarantine entry permits automatic rearming.");
            entries.Add(new HookQuarantineEntry(
                SafeCandidateId(element, "candidateId"), HookCandidateCatalogReader.RequiredHash(element, "hookPathFingerprint"),
                (HookValidationDepth)HookCandidateCatalogReader.RequiredInt(element, "validationDepth", 1, 7),
                state, HookCandidateCatalogReader.RequiredString(element, "reason", 1024), SafeId(element, "runId"),
                HookCandidateCatalogReader.RequiredDate(element, "quarantinedAtUtc"), explicitRetry, autoRearm));
        }
        return new HookQuarantineState(ResearchContracts.QuarantineSchema,
            HookCandidateCatalogReader.RequiredHash(root, "hookCatalogIdentity"), entries);
    }

    public async Task<HookRunManifest> ReadRunManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var document = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        HookCandidateCatalogReader.RequireProperties(root, "run manifest",
            "schemaVersion", "runId", "sessionId", "campaignGeneration", "createdAtUtc", "runType",
            "selectedRole", "compatibility", "safeSnapshotBaseline", "trustedCandidates", "canary",
            "registrationOrder", "automaticInProcessAdvance", "safety");
        if (HookCandidateCatalogReader.RequiredString(root, "schemaVersion", 96) != ResearchContracts.RunManifestSchema)
            throw new ResearchSchemaException("Unsupported run-manifest schema.");
        var compatibility = ParseCompatibility(root.GetProperty("compatibility"));
        var trusted = HookCandidateCatalogReader.RequiredArray(root, "trustedCandidates").EnumerateArray()
            .Select(ParseSelection).ToArray();
        HookCandidateSelection? canary = root.GetProperty("canary").ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Object => ParseSelection(root.GetProperty("canary")),
            _ => throw new ResearchSchemaException("Run-manifest canary must be null or an object.")
        };
        var type = ParseRunType(HookCandidateCatalogReader.RequiredString(root, "runType", 32));
        if (type == ResearchRunType.TrustedPoolOnly && canary is not null ||
            type != ResearchRunType.TrustedPoolOnly && canary is null)
            throw new ResearchSchemaException("Run type and canary count are inconsistent.");
        var automaticAdvance = HookCandidateCatalogReader.RequiredBool(root, "automaticInProcessAdvance");
        if (automaticAdvance) throw new ResearchSchemaException("Run manifest permits in-process advancement.");
        if (!HookCandidateCatalogReader.RequiredBool(root, "safeSnapshotBaseline"))
            throw new ResearchSchemaException("Run manifest disables the safe snapshot baseline.");
        ValidateSafety(root.GetProperty("safety"));
        var order = StringArray(root, "registrationOrder", 114);
        if (canary is not null && (order.Count == 0 || order[^1] != canary.CandidateId))
            throw new ResearchSchemaException("Run-manifest canary is not registered last.");
        return new HookRunManifest(
            ResearchContracts.RunManifestSchema, SafeId(root, "runId"), SafeId(root, "sessionId"),
            HookCandidateCatalogReader.RequiredLong(root, "campaignGeneration", 1, 9007199254740991),
            HookCandidateCatalogReader.RequiredDate(root, "createdAtUtc"), type,
            CampaignRoleNames.Parse(HookCandidateCatalogReader.RequiredString(root, "selectedRole", 32)),
            compatibility, true, trusted, canary, order, false);
    }

    public Task WriteRunManifestAsync(string path, HookRunManifest manifest, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteTextAsync(path, SerializeRunManifest(manifest), cancellationToken);

    public Task WriteClassificationAsync(string path, HookRunClassification classification, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteTextAsync(path, SerializeClassification(classification), cancellationToken);

    public Task WriteLedgerAsync(
        string path,
        HookValidationLedger ledger,
        DateTimeOffset generatedAtUtc,
        string migrationPolicy,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            schemaVersion = ResearchContracts.LedgerSchema,
            generatedAtUtc,
            updatedAtUtc = ledger.UpdatedAtUtc,
            coverageCatalogHash = ledger.CoverageCatalogHash,
            hookCatalogIdentity = ledger.HookCatalogIdentity,
            callbackImplementationVersion = ledger.CallbackImplementationVersion,
            callbackSchemaVersion = ledger.CallbackSchemaVersion,
            validationBehaviorVersion = ledger.ValidationBehaviorVersion,
            initialMigrationPolicy = migrationPolicy,
            candidates = ledger.Candidates.Select(candidate => new
            {
                candidateId = candidate.CandidateId,
                hookPathFingerprint = candidate.HookPathFingerprint,
                state = CandidateStateContract(candidate.State),
                highestValidatedDepth = (int)candidate.HighestValidatedDepth,
                trustedDepth = candidate.TrustedDepth is null ? (int?)null : (int)candidate.TrustedDepth,
                cleanRuns = candidate.CleanRuns,
                naturalCallbacks = candidate.NaturalCallbacks,
                hostCleanRuns = candidate.HostCleanRuns,
                joinedClientCleanRuns = candidate.JoinedClientCleanRuns,
                lifecycleTransitionRuns = candidate.LifecycleTransitionRuns,
                evidenceSessions = candidate.EvidenceSessions,
                legacyObservationMigrated = candidate.LegacyObservationMigrated,
                legacyObservationTrusted = false,
                crashSuspectRuns = candidate.CrashSuspectRuns,
                compatibilityFingerprint = candidate.CompatibilityFingerprint,
                hasUnmatchedBreadcrumb = candidate.HasUnmatchedBreadcrumb,
                hasCorrelatedCrash = candidate.HasCorrelatedCrash,
                hasNewUe4ssCallbackError = candidate.HasNewUe4ssCallbackError,
                reducerFixtureCovered = candidate.ReducerFixtureCovered
            })
        };
        return AtomicFile.WriteTextAsync(path, JsonSerializer.Serialize(payload, WriteOptions) + Environment.NewLine, cancellationToken);
    }

    public Task WriteTrustedManifestAsync(
        string path,
        TrustedHookManifest manifest,
        DateTimeOffset generatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            schemaVersion = ResearchContracts.TrustedManifestSchema,
            generatedAtUtc,
            coverageCatalogHash = manifest.CoverageCatalogHash,
            hookCatalogIdentity = manifest.HookCatalogIdentity,
            callbackImplementationVersion = manifest.CallbackImplementationVersion,
            callbackSchemaVersion = manifest.CallbackSchemaVersion,
            validationBehaviorVersion = manifest.ValidationBehaviorVersion,
            compatibilityFingerprint = manifest.CompatibilityFingerprint,
            generatedFromLedgerAtUtc = manifest.GeneratedFromLedgerAtUtc,
            candidates = manifest.Candidates.Select(candidate => new
            {
                candidateId = candidate.CandidateId,
                hookPathFingerprint = candidate.HookPathFingerprint,
                trustedDepth = (int)candidate.TrustedDepth,
                compatibilityFingerprint = candidate.CompatibilityFingerprint
            })
        };
        return AtomicFile.WriteTextAsync(path, JsonSerializer.Serialize(payload, WriteOptions) + Environment.NewLine, cancellationToken);
    }

    public Task WriteQuarantineAsync(
        string path,
        HookQuarantineState state,
        HookCandidateCatalog catalog,
        DateTimeOffset generatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new
        {
            schemaVersion = ResearchContracts.QuarantineSchema,
            generatedAtUtc,
            updatedAtUtc = now,
            coverageCatalogHash = catalog.CoverageCatalogHash,
            hookCatalogIdentity = catalog.HookCatalogIdentity,
            callbackImplementationVersion = catalog.CallbackImplementationVersion,
            callbackSchemaVersion = catalog.CallbackSchemaVersion,
            validationBehaviorVersion = catalog.ValidationBehaviorVersion,
            entries = state.Entries.Select(entry => new
            {
                candidateId = entry.CandidateId,
                hookPathFingerprint = entry.HookPathFingerprint,
                validationDepth = (int)entry.ValidationDepth,
                state = CandidateStateContract(entry.State),
                reason = entry.Reason,
                runId = entry.RunId,
                quarantinedAtUtc = entry.QuarantinedAtUtc,
                explicitRetryRequired = true,
                automaticRearmAllowed = false
            })
        };
        return AtomicFile.WriteTextAsync(path, JsonSerializer.Serialize(payload, WriteOptions) + Environment.NewLine, cancellationToken);
    }

    private static string SerializeRunManifest(HookRunManifest manifest)
    {
        var payload = new
        {
            schemaVersion = ResearchContracts.RunManifestSchema,
            manifest.RunId,
            manifest.SessionId,
            manifest.CampaignGeneration,
            manifest.CreatedAtUtc,
            runType = RunTypeContract(manifest.RunType),
            selectedRole = manifest.SelectedRole.ToContract(),
            compatibility = CompatibilityPayload(manifest.Compatibility),
            safeSnapshotBaseline = true,
            trustedCandidates = manifest.TrustedCandidates.Select(SelectionPayload),
            canary = manifest.Canary is null ? null : SelectionPayload(manifest.Canary),
            manifest.RegistrationOrder,
            automaticInProcessAdvance = false,
            safety = new
            {
                readOnly = true, invokeFunctions = false, invokeRpcs = false, manualOnRep = false,
                mutation = false, runtimeDiscovery = false, freeFormHookPath = false, maximumCanaries = 1
            }
        };
        return JsonSerializer.Serialize(payload, WriteOptions) + Environment.NewLine;
    }

    private static string SerializeClassification(HookRunClassification value)
    {
        var payload = new
        {
            schemaVersion = ResearchContracts.ClassificationSchema,
            value.RunId,
            value.ClassifiedAtUtc,
            classification = ClassificationContract(value.Classification),
            outcome = OutcomeContract(value.Outcome),
            confidence = value.Confidence.ToString().ToLowerInvariant(),
            value.CandidateId,
            validationDepth = value.ValidationDepth is null ? (int?)null : (int)value.ValidationDepth,
            value.LastCompletedBoundary,
            value.LastUnmatchedBoundary,
            value.Reason,
            recommendation = RecommendationContract(value.Recommendation),
            automaticRearmAllowed = false,
            value.Evidence
        };
        return JsonSerializer.Serialize(payload, WriteOptions) + Environment.NewLine;
    }

    private static async Task<JsonDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 1 or > MaximumArtifactBytes)
            throw new ResearchSchemaException($"Research artifact size {stream.Length} is outside the accepted range.");
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static HookCandidateSelection ParseSelection(JsonElement element)
    {
        HookCandidateCatalogReader.RequireProperties(element, "candidate selection",
            "candidateId", "hookPathFingerprint", "validationDepth");
        return new HookCandidateSelection(SafeCandidateId(element, "candidateId"),
            HookCandidateCatalogReader.RequiredHash(element, "hookPathFingerprint"),
            (HookValidationDepth)HookCandidateCatalogReader.RequiredInt(element, "validationDepth", 1, 7));
    }

    private static CompatibilityFingerprint ParseCompatibility(JsonElement element)
    {
        HookCandidateCatalogReader.RequireProperties(element, "compatibility fingerprint",
            "schemaVersion", "gameBuild", "ue4ssVersion", "coverageCatalogHash", "hookCatalogIdentity",
            "callbackImplementationVersion", "callbackSchemaVersion", "validationBehaviorVersion",
            "fingerprint", "computedAtUtc");
        if (HookCandidateCatalogReader.RequiredString(element, "schemaVersion", 96) != ResearchContracts.CompatibilitySchema)
            throw new ResearchSchemaException("Unsupported compatibility schema.");
        return new CompatibilityFingerprint(
            ResearchContracts.CompatibilitySchema,
            HookCandidateCatalogReader.RequiredString(element, "gameBuild", 128),
            HookCandidateCatalogReader.RequiredString(element, "ue4ssVersion", 128),
            HookCandidateCatalogReader.RequiredHash(element, "coverageCatalogHash"),
            HookCandidateCatalogReader.RequiredHash(element, "hookCatalogIdentity"),
            HookCandidateCatalogReader.RequiredToken(element, "callbackImplementationVersion", 96),
            HookCandidateCatalogReader.RequiredToken(element, "callbackSchemaVersion", 96),
            HookCandidateCatalogReader.RequiredToken(element, "validationBehaviorVersion", 96),
            HookCandidateCatalogReader.RequiredHash(element, "fingerprint"),
            HookCandidateCatalogReader.RequiredDate(element, "computedAtUtc"));
    }

    private static void ValidateSafety(JsonElement element)
    {
        HookCandidateCatalogReader.RequireProperties(element, "research safety",
            "readOnly", "invokeFunctions", "invokeRpcs", "manualOnRep", "mutation", "runtimeDiscovery",
            "freeFormHookPath", "maximumCanaries");
        if (!HookCandidateCatalogReader.RequiredBool(element, "readOnly") ||
            HookCandidateCatalogReader.RequiredBool(element, "invokeFunctions") ||
            HookCandidateCatalogReader.RequiredBool(element, "invokeRpcs") ||
            HookCandidateCatalogReader.RequiredBool(element, "manualOnRep") ||
            HookCandidateCatalogReader.RequiredBool(element, "mutation") ||
            HookCandidateCatalogReader.RequiredBool(element, "runtimeDiscovery") ||
            HookCandidateCatalogReader.RequiredBool(element, "freeFormHookPath") ||
            HookCandidateCatalogReader.RequiredInt(element, "maximumCanaries", 1, 1) != 1)
            throw new ResearchSchemaException("Unsafe research manifest safety contract.");
    }

    private static object CompatibilityPayload(CompatibilityFingerprint value) => new
    {
        schemaVersion = ResearchContracts.CompatibilitySchema,
        value.GameBuild,
        value.Ue4ssVersion,
        value.CoverageCatalogHash,
        value.HookCatalogIdentity,
        value.CallbackImplementationVersion,
        value.CallbackSchemaVersion,
        value.ValidationBehaviorVersion,
        value.Fingerprint,
        value.ComputedAtUtc
    };

    private static object SelectionPayload(HookCandidateSelection value) => new
    {
        value.CandidateId,
        value.HookPathFingerprint,
        validationDepth = (int)value.ValidationDepth
    };

    private static HookValidationDepth? NullableDepth(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : (HookValidationDepth)HookCandidateCatalogReader.RequiredInt(element, name, 1, 7);
    }

    private static IReadOnlyList<string> StringArray(JsonElement element, string name, int maxItems) =>
        HookCandidateCatalogReader.StringArray(element, name, maxItems, 128);

    private static string SafeCandidateId(JsonElement element, string name)
    {
        var value = SafeId(element, name);
        if (!value.StartsWith("hook-", StringComparison.Ordinal))
            throw new ResearchSchemaException($"'{name}' is not a stable candidate ID.");
        return value;
    }

    private static string SafeId(JsonElement element, string name)
    {
        var value = HookCandidateCatalogReader.RequiredString(element, name, 128);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ResearchSchemaException($"'{name}' contains unsafe characters.");
        return value;
    }

    private static HookCandidateState ParseCandidateState(string value) => value switch
    {
        "untested" => HookCandidateState.Untested,
        "armed" => HookCandidateState.Armed,
        "registration-clean" => HookCandidateState.RegistrationClean,
        "registered-not-observed" => HookCandidateState.RegisteredNotObserved,
        "natural-callback-clean" => HookCandidateState.NaturalCallbackClean,
        "provisional" => HookCandidateState.Provisional,
        "trusted" => HookCandidateState.Trusted,
        "needs-revalidation" => HookCandidateState.NeedsRevalidation,
        "unsupported" => HookCandidateState.Unsupported,
        "quarantined" => HookCandidateState.Quarantined,
        "crash-suspect" => HookCandidateState.CrashSuspect,
        _ => throw new ResearchSchemaException($"Unknown candidate state '{value}'.")
    };

    internal static string CandidateStateContract(HookCandidateState value) => value switch
    {
        HookCandidateState.Untested => "untested",
        HookCandidateState.Armed => "armed",
        HookCandidateState.RegistrationClean => "registration-clean",
        HookCandidateState.RegisteredNotObserved => "registered-not-observed",
        HookCandidateState.NaturalCallbackClean => "natural-callback-clean",
        HookCandidateState.Provisional => "provisional",
        HookCandidateState.Trusted => "trusted",
        HookCandidateState.NeedsRevalidation => "needs-revalidation",
        HookCandidateState.Unsupported => "unsupported",
        HookCandidateState.Quarantined => "quarantined",
        HookCandidateState.CrashSuspect => "crash-suspect",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static ResearchRunType ParseRunType(string value) => value switch
    {
        "trusted-pool-only" => ResearchRunType.TrustedPoolOnly,
        "canary-only" => ResearchRunType.CanaryOnly,
        "combined" => ResearchRunType.Combined,
        _ => throw new ResearchSchemaException($"Unknown research run type '{value}'.")
    };

    internal static string RunTypeContract(ResearchRunType value) => value switch
    {
        ResearchRunType.TrustedPoolOnly => "trusted-pool-only",
        ResearchRunType.CanaryOnly => "canary-only",
        ResearchRunType.Combined => "combined",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string ClassificationContract(HookRunClassificationKind value) => value switch
    {
        HookRunClassificationKind.CleanShutdown => "clean-shutdown",
        HookRunClassificationKind.InterruptedRun => "interrupted-run",
        HookRunClassificationKind.ExternalTermination => "external-termination",
        HookRunClassificationKind.StaleWriter => "stale-writer",
        HookRunClassificationKind.RegistrationFailure => "registration-failure",
        HookRunClassificationKind.CallbackBoundaryFailure => "callback-boundary-failure",
        HookRunClassificationKind.EvidenceFailure => "evidence-failure",
        HookRunClassificationKind.UnattributedPostCallbackCrash => "unattributed-post-callback-crash",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string OutcomeContract(HookRunOutcome value) => value switch
    {
        HookRunOutcome.RegistrationClean => "registration-clean",
        HookRunOutcome.RegisteredNotNaturallyObserved => "registered-not-naturally-observed",
        HookRunOutcome.NaturalCallbackClean => "natural-callback-clean",
        HookRunOutcome.CrashSuspect => "crash-suspect",
        HookRunOutcome.NeedsRevalidation => "needs-revalidation",
        HookRunOutcome.Unsupported => "unsupported",
        HookRunOutcome.Unattributed => "unattributed",
        HookRunOutcome.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string RecommendationContract(ResearchRecommendation value) => value switch
    {
        ResearchRecommendation.RepeatSameTest => "repeat-same-test",
        ResearchRecommendation.PrepareNextDepth => "prepare-next-depth",
        ResearchRecommendation.TrustedPoolControl => "trusted-pool-control",
        ResearchRecommendation.CanaryAlone => "canary-alone",
        ResearchRecommendation.Combined => "combined",
        ResearchRecommendation.ControlledSubset => "controlled-subset",
        ResearchRecommendation.ReturnSafePlayGuide => "return-safe-play-guide",
        ResearchRecommendation.ManualReview => "manual-review",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public static class ValidationLedgerReducer
{
    public static HookValidationLedger Apply(
        HookValidationLedger ledger,
        HookRunManifest manifest,
        HookRunClassification classification,
        BreadcrumbReadResult journal,
        bool lifecycleTransitionObserved,
        bool reducerFixtureCovered,
        bool newUe4ssCallbackError)
    {
        if (manifest.Canary is null || classification.CandidateId is null) return ledger;
        var targetId = manifest.Canary.CandidateId;
        var records = ledger.Candidates.ToList();
        var index = records.FindIndex(candidate => candidate.CandidateId == targetId);
        if (index < 0) return ledger;
        var record = records[index];
        var clean = classification.Classification == HookRunClassificationKind.CleanShutdown;
        var callbacks = journal.CallbackCountByCandidate.GetValueOrDefault(targetId);
        var attemptedDepth = manifest.Canary.ValidationDepth;
        var completedNewDepth = clean && callbacks > 0 && attemptedDepth > record.HighestValidatedDepth;
        var sessionIds = (completedNewDepth ? Array.Empty<string>() : record.EvidenceSessions)
            .Concat(new[] { manifest.SessionId }).Distinct(StringComparer.Ordinal).ToArray();
        var crashRuns = record.CrashSuspectRuns;
        var nextState = classification.Outcome switch
        {
            HookRunOutcome.RegistrationClean => HookCandidateState.RegistrationClean,
            HookRunOutcome.RegisteredNotNaturallyObserved => HookCandidateState.RegisteredNotObserved,
            HookRunOutcome.NaturalCallbackClean => HookCandidateState.NaturalCallbackClean,
            HookRunOutcome.CrashSuspect => HookCandidateState.CrashSuspect,
            HookRunOutcome.NeedsRevalidation => HookCandidateState.NeedsRevalidation,
            HookRunOutcome.Unsupported => HookCandidateState.Unsupported,
            _ => record.State
        };
        if (record.TrustedDepth is not null && nextState is HookCandidateState.RegistrationClean
                or HookCandidateState.RegisteredNotObserved or HookCandidateState.NaturalCallbackClean)
            nextState = HookCandidateState.Trusted;
        if (classification.Outcome == HookRunOutcome.CrashSuspect)
            crashRuns = crashRuns.Concat(new[] { manifest.RunId }).Distinct(StringComparer.Ordinal).ToArray();
        var completedDepth = clean && callbacks > 0
            ? (HookValidationDepth)Math.Max((int)record.HighestValidatedDepth, (int)attemptedDepth)
            : record.HighestValidatedDepth;
        var cleanRuns = completedNewDepth ? 0 : record.CleanRuns;
        var naturalCallbacks = completedNewDepth ? 0 : record.NaturalCallbacks;
        var hostCleanRuns = completedNewDepth ? 0 : record.HostCleanRuns;
        var joinedCleanRuns = completedNewDepth ? 0 : record.JoinedClientCleanRuns;
        var lifecycleRuns = completedNewDepth ? 0 : record.LifecycleTransitionRuns;
        records[index] = record with
        {
            State = nextState,
            HighestValidatedDepth = completedDepth,
            CleanRuns = cleanRuns + (clean ? 1 : 0),
            NaturalCallbacks = naturalCallbacks + callbacks,
            HostCleanRuns = hostCleanRuns + (clean && manifest.SelectedRole == CampaignRole.Host ? 1 : 0),
            JoinedClientCleanRuns = joinedCleanRuns + (clean && manifest.SelectedRole == CampaignRole.JoinedClient ? 1 : 0),
            LifecycleTransitionRuns = lifecycleRuns + (clean && lifecycleTransitionObserved ? 1 : 0),
            EvidenceSessions = sessionIds,
            CrashSuspectRuns = crashRuns,
            CompatibilityFingerprint = manifest.Compatibility.Fingerprint,
            HasUnmatchedBreadcrumb = record.HasUnmatchedBreadcrumb || journal.LastUnmatched is not null,
            HasCorrelatedCrash = record.HasCorrelatedCrash || classification.Outcome == HookRunOutcome.CrashSuspect,
            HasNewUe4ssCallbackError = record.HasNewUe4ssCallbackError || newUe4ssCallbackError,
            ReducerFixtureCovered = record.ReducerFixtureCovered || reducerFixtureCovered
        };
        return ledger with { UpdatedAtUtc = DateTimeOffset.UtcNow, Candidates = records };
    }
}
