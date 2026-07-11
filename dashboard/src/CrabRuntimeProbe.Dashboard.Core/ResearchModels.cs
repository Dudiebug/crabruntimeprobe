using System.Collections.ObjectModel;

namespace CrabRuntimeProbe.Dashboard.Core;

public enum HookValidationDepth
{
    StaticCatalogValidation = 0,
    RegistrationOnly = 1,
    CallbackEntryExit = 2,
    ContextResolution = 3,
    PlayerStateScope = 4,
    ReviewedStateReads = 5,
    DocumentedArguments = 6,
    FullPassiveEvidence = 7
}

public enum HookCandidateState
{
    Untested,
    Armed,
    RegistrationClean,
    RegisteredNotObserved,
    NaturalCallbackClean,
    Provisional,
    Trusted,
    NeedsRevalidation,
    Unsupported,
    Quarantined,
    CrashSuspect
}

public enum ResearchRunType
{
    TrustedPoolOnly,
    CanaryOnly,
    Combined
}

public enum HookRunClassificationKind
{
    CleanShutdown,
    InterruptedRun,
    ExternalTermination,
    StaleWriter,
    RegistrationFailure,
    CallbackBoundaryFailure,
    EvidenceFailure,
    UnattributedPostCallbackCrash
}

public enum HookRunOutcome
{
    RegistrationClean,
    RegisteredNotNaturallyObserved,
    NaturalCallbackClean,
    CrashSuspect,
    NeedsRevalidation,
    Unsupported,
    Unattributed,
    Incomplete
}

public enum AttributionConfidence
{
    None,
    Low,
    Medium,
    High
}

public enum ResearchRecommendation
{
    RepeatSameTest,
    PrepareNextDepth,
    TrustedPoolControl,
    CanaryAlone,
    Combined,
    ControlledSubset,
    ReturnSafePlayGuide,
    ManualReview
}

public sealed record HookArgumentDefinition(
    string Name,
    string PropertyType,
    string ValueTypePath,
    string SafeSummary,
    string Redaction);

public sealed record HookCandidateDefinition(
    string Id,
    string DisplayName,
    string Category,
    string HookPath,
    string HookPathFingerprint,
    string OwnerPath,
    string OwnerKind,
    string CandidateType,
    int Priority,
    string SuggestedAction,
    string RoleApplicability,
    HookValidationDepth MaximumValidationDepth,
    string CallbackPhase,
    IReadOnlyList<string> ScopeProperties,
    IReadOnlyList<string> ReviewedStateFields,
    IReadOnlyList<HookArgumentDefinition> ArgumentSchema,
    IReadOnlyList<string> ChecklistLinks,
    bool KnownCrashContext);

public sealed record HookCandidateCatalog(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string CoverageCatalogHash,
    string HookCatalogIdentity,
    string CallbackImplementationVersion,
    string CallbackSchemaVersion,
    string ValidationBehaviorVersion,
    string PrincipalCandidateId,
    IReadOnlyList<HookCandidateDefinition> Candidates)
{
    public IReadOnlyDictionary<string, HookCandidateDefinition> ById { get; } =
        new ReadOnlyDictionary<string, HookCandidateDefinition>(
            Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal));
}

public sealed record CompatibilityFingerprint(
    string SchemaVersion,
    string GameBuild,
    string Ue4ssVersion,
    string CoverageCatalogHash,
    string HookCatalogIdentity,
    string CallbackImplementationVersion,
    string CallbackSchemaVersion,
    string ValidationBehaviorVersion,
    string Fingerprint,
    DateTimeOffset ComputedAtUtc)
{
    public bool IsComplete =>
        SchemaVersion == ResearchContracts.CompatibilitySchema &&
        ResearchContracts.IsSha256(Fingerprint) &&
        ResearchContracts.IsSha256(CoverageCatalogHash) &&
        ResearchContracts.IsSha256(HookCatalogIdentity) &&
        !ResearchContracts.IsUnknownCompatibilityComponent(GameBuild) &&
        !ResearchContracts.IsUnknownCompatibilityComponent(Ue4ssVersion);
}

public sealed record HookCandidateSelection(
    string CandidateId,
    string HookPathFingerprint,
    HookValidationDepth ValidationDepth);

public sealed record HookRunManifest(
    string SchemaVersion,
    string RunId,
    string SessionId,
    long CampaignGeneration,
    DateTimeOffset CreatedAtUtc,
    ResearchRunType RunType,
    CampaignRole SelectedRole,
    CompatibilityFingerprint Compatibility,
    bool SafeSnapshotBaseline,
    IReadOnlyList<HookCandidateSelection> TrustedCandidates,
    HookCandidateSelection? Canary,
    IReadOnlyList<string> RegistrationOrder,
    bool AutomaticInProcessAdvance);

public sealed record TrustedHookEntry(
    string CandidateId,
    string HookPathFingerprint,
    HookValidationDepth TrustedDepth,
    string CompatibilityFingerprint);

public sealed record TrustedHookManifest(
    string SchemaVersion,
    string CoverageCatalogHash,
    string HookCatalogIdentity,
    string CallbackImplementationVersion,
    string CallbackSchemaVersion,
    string ValidationBehaviorVersion,
    string CompatibilityFingerprint,
    DateTimeOffset GeneratedFromLedgerAtUtc,
    IReadOnlyList<TrustedHookEntry> Candidates);

public sealed record CandidateValidationRecord(
    string CandidateId,
    string HookPathFingerprint,
    HookCandidateState State,
    HookValidationDepth HighestValidatedDepth,
    HookValidationDepth? TrustedDepth,
    int CleanRuns,
    int NaturalCallbacks,
    int HostCleanRuns,
    int JoinedClientCleanRuns,
    int LifecycleTransitionRuns,
    IReadOnlyList<string> EvidenceSessions,
    bool LegacyObservationMigrated,
    bool LegacyObservationTrusted,
    IReadOnlyList<string> CrashSuspectRuns,
    string CompatibilityFingerprint = "",
    bool HasUnmatchedBreadcrumb = false,
    bool HasCorrelatedCrash = false,
    bool HasNewUe4ssCallbackError = false,
    bool ReducerFixtureCovered = false);

public sealed record HookValidationLedger(
    string SchemaVersion,
    string CoverageCatalogHash,
    string HookCatalogIdentity,
    string CallbackImplementationVersion,
    string CallbackSchemaVersion,
    string ValidationBehaviorVersion,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<CandidateValidationRecord> Candidates);

public sealed record HookQuarantineEntry(
    string CandidateId,
    string HookPathFingerprint,
    HookValidationDepth ValidationDepth,
    HookCandidateState State,
    string Reason,
    string RunId,
    DateTimeOffset QuarantinedAtUtc,
    bool ExplicitRetryRequired,
    bool AutomaticRearmAllowed);

public sealed record HookQuarantineState(
    string SchemaVersion,
    string HookCatalogIdentity,
    IReadOnlyList<HookQuarantineEntry> Entries);

public sealed record HookBreadcrumb(
    long Sequence,
    string RunId,
    string CandidateId,
    string HookPathFingerprint,
    HookValidationDepth ValidationDepth,
    string CandidateRole,
    string InvocationId,
    string Phase,
    string Boundary,
    long LifecycleGeneration,
    DateTimeOffset TimestampUtc,
    long MonotonicMicros,
    int SourceLine);

public sealed record BreadcrumbReadIssue(string Code, string Detail, int SourceLine, bool IsFatal);

public sealed record BreadcrumbReadResult(
    IReadOnlyList<HookBreadcrumb> Records,
    IReadOnlyList<BreadcrumbReadIssue> Issues,
    bool TruncatedFinalWrite,
    HookBreadcrumb? LastCompleted,
    HookBreadcrumb? LastUnmatched,
    IReadOnlyDictionary<string, int> CallbackCountByCandidate)
{
    public bool HasFatalIssue => Issues.Any(issue => issue.IsFatal);
}

public sealed record RunObservationSignals(
    bool CleanShutdown,
    bool ProcessExitObserved,
    bool AbnormalProcessExit,
    bool ExternalTermination,
    bool WriterStale,
    bool EvidenceWriteFailed,
    bool StatusFaulted,
    bool CrashArtifactCorrelated,
    int Ue4ssCallbackErrors,
    bool InteractionPlausible = false);

public sealed record HookRunClassification(
    string SchemaVersion,
    string RunId,
    DateTimeOffset ClassifiedAtUtc,
    HookRunClassificationKind Classification,
    HookRunOutcome Outcome,
    AttributionConfidence Confidence,
    string? CandidateId,
    HookValidationDepth? ValidationDepth,
    string? LastCompletedBoundary,
    string? LastUnmatchedBoundary,
    string Reason,
    ResearchRecommendation Recommendation,
    bool AutomaticRearmAllowed,
    IReadOnlyList<string> Evidence);

public sealed record PromotionAssessment(bool Eligible, IReadOnlyList<string> UnmetRequirements);

public sealed record ResearchRunPlan(
    bool IsValid,
    HookRunManifest? Manifest,
    IReadOnlyList<string> Errors,
    HookCandidateDefinition? CanaryCandidate,
    string Summary);

public static class ResearchContracts
{
    public const string CandidateCatalogSchema = "hook-candidate-catalog-v1";
    public const string CompatibilitySchema = "compatibility-fingerprint-v1";
    public const string RunManifestSchema = "hook-run-manifest-v1";
    public const string RunConsumedSchema = "hook-run-consumed-v1";
    public const string BreadcrumbSchema = "hook-breadcrumb-v1";
    public const string ClassificationSchema = "hook-run-classification-v1";
    public const string LedgerSchema = "hook-validation-ledger-v1";
    public const string TrustedManifestSchema = "trusted-hook-manifest-v1";
    public const string QuarantineSchema = "hook-quarantine-v1";

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsUnknownCompatibilityComponent(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("unavailable", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("runtime-detected", StringComparison.OrdinalIgnoreCase);

    public static string DepthDisplay(HookValidationDepth depth) => depth switch
    {
        HookValidationDepth.StaticCatalogValidation => "Depth 0 — static catalog validation",
        HookValidationDepth.RegistrationOnly => "Depth 1 — registration only",
        HookValidationDepth.CallbackEntryExit => "Depth 2 — callback entry and exit",
        HookValidationDepth.ContextResolution => "Depth 3 — context resolution",
        HookValidationDepth.PlayerStateScope => "Depth 4 — PlayerState scope",
        HookValidationDepth.ReviewedStateReads => "Depth 5 — reviewed state reads",
        HookValidationDepth.DocumentedArguments => "Depth 6 — documented arguments",
        HookValidationDepth.FullPassiveEvidence => "Depth 7 — full passive evidence",
        _ => "Unknown validation depth"
    };
}
