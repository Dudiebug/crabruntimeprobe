using System.Collections.ObjectModel;

namespace CrabRuntimeProbe.Dashboard.Core;

public enum CampaignRole
{
    Unknown,
    Host,
    JoinedClient
}

public enum ChecklistDisplayState
{
    NotObserved,
    InProgress,
    Partial,
    Confirmed,
    Unsupported,
    BlockedByPrerequisite,
    CrashSuspect,
    DirtyEvidence,
    NotApplicable
}

public enum PlayGuideDisplayState
{
    ToDo,
    InProgress,
    Done,
    Waiting,
    Retry
}

public enum PlayGuideFilter
{
    ToDo,
    All,
    Completed
}

public enum EvidenceCleanliness
{
    Clean,
    Dirty,
    CrashSuspect,
    Unknown
}

public static class CampaignRoleNames
{
    public static CampaignRole Parse(string? value) => Normalize(value) switch
    {
        "host" or "solo-or-host" => CampaignRole.Host,
        "joined" or "joined-client" or "joinedclient" or "client" => CampaignRole.JoinedClient,
        _ => CampaignRole.Unknown
    };

    public static string ToContract(this CampaignRole role) => role switch
    {
        CampaignRole.Host => "host",
        CampaignRole.JoinedClient => "joined-client",
        _ => "unknown"
    };

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
}

public sealed record LifecycleInfo(
    string State,
    long Generation,
    string World,
    string Context,
    bool Stable,
    DateTimeOffset? ChangedAtUtc);

public sealed record RuntimeInfo(
    bool GameProcessRunning,
    string GameProcessState,
    string Ue4ssState,
    string RuntimeProbeState,
    bool RuntimeProbeLoaded,
    string CurrentProbeStage,
    int? GameProcessId);

public sealed record SafetyInfo(
    bool WritesDisabled,
    bool RpcsDisabled,
    bool MutationDisabled,
    bool HudHookDisabled,
    bool RawIdentityDisabled,
    int InventoryDepth,
    IReadOnlyDictionary<string, string> CircuitBreakers)
{
    public bool AllRequiredSafe =>
        WritesDisabled && RpcsDisabled && MutationDisabled && HudHookDisabled && RawIdentityDisabled;
}

public sealed record ChecklistEvidence(
    string Id,
    string ReportedStatus,
    long ObservationCount,
    DateTimeOffset? FirstObservedAtUtc,
    DateTimeOffset? LastObservedAtUtc,
    IReadOnlyList<string> SourceRoles,
    IReadOnlyList<string> EvidenceSessions,
    IReadOnlyList<string> EvidenceKinds,
    bool HookRegistered,
    bool QualifyingEvidence,
    bool DirtyEvidence,
    bool CrashSuspect,
    string NextInstruction,
    string Detail);

public sealed record EvidenceHealthInfo(
    string State,
    long CanonicalRows,
    long RejectedRows,
    long DirtyRows,
    string Detail);

public sealed record LiveStatusSnapshot(
    int SchemaVersion,
    long Sequence,
    DateTimeOffset WrittenAtUtc,
    DateTimeOffset HeartbeatAtUtc,
    string CampaignId,
    string CampaignName,
    long CampaignGeneration,
    string MachineId,
    string SessionId,
    CampaignRole SelectedRole,
    string ObservedRole,
    string AuthorityStatus,
    LifecycleInfo Lifecycle,
    RuntimeInfo Runtime,
    SafetyInfo Safety,
    IReadOnlyDictionary<string, ChecklistEvidence> Checklist,
    EvidenceHealthInfo EvidenceHealth,
    bool CrashSuspected,
    bool DirtyEvidence,
    string SourceFile)
{
    public static LiveStatusSnapshot Empty { get; } = new(
        1,
        0,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        string.Empty,
        "No campaign",
        0,
        string.Empty,
        string.Empty,
        CampaignRole.Unknown,
        "unknown",
        "unknown",
        new LifecycleInfo("not-started", 0, string.Empty, string.Empty, false, null),
        new RuntimeInfo(false, "not-running", "unknown", "not-loaded", false, "idle", null),
        new SafetyInfo(true, true, true, true, true, 0,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>())),
        new ReadOnlyDictionary<string, ChecklistEvidence>(new Dictionary<string, ChecklistEvidence>()),
        new EvidenceHealthInfo("no-evidence", 0, 0, 0, string.Empty),
        false,
        false,
        string.Empty);
}

public sealed record LiveStatusReadResult(
    LiveStatusSnapshot Snapshot,
    bool HasSnapshot,
    bool IsStale,
    bool UsedLastGood,
    string Error,
    DateTimeOffset ReadAtUtc)
{
    public EvidenceCleanliness Cleanliness => Snapshot.CrashSuspected
        ? EvidenceCleanliness.CrashSuspect
        : Snapshot.DirtyEvidence || UsedLastGood
            ? EvidenceCleanliness.Dirty
            : HasSnapshot
                ? EvidenceCleanliness.Clean
                : EvidenceCleanliness.Unknown;
}

public sealed record ChecklistDefinition(
    string Id,
    string Group,
    string Label,
    string Instruction,
    bool RequiresNaturalEvidence,
    IReadOnlyList<string> Prerequisites);

public sealed record ChecklistViewItem(
    ChecklistDefinition Definition,
    ChecklistDisplayState State,
    long ObservationCount,
    DateTimeOffset? FirstObservedAtUtc,
    DateTimeOffset? LastObservedAtUtc,
    string Sources,
    string EvidenceSessions,
    string Instruction,
    string Detail)
{
    public string Id => Definition.Id;
    public string Group => Definition.Group;
    public string Label => Definition.Label;
    public string FirstObservedDisplay => FormatTimestamp(FirstObservedAtUtc);
    public string LastObservedDisplay => FormatTimestamp(LastObservedAtUtc);
    public string SourcesDisplay => string.IsNullOrWhiteSpace(Sources) ? "—" : Sources;
    public string EvidenceSessionsDisplay => string.IsNullOrWhiteSpace(EvidenceSessions) ? "—" : EvidenceSessions;
    public bool IsComplete => State is ChecklistDisplayState.Confirmed or ChecklistDisplayState.NotApplicable;

    private static string FormatTimestamp(DateTimeOffset? timestamp) => timestamp is { } value
        ? value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)
        : "—";
}

public sealed record PlayGuideSubtask(
    string Label,
    PlayGuideDisplayState State)
{
    public string StateText => PlayGuideStateNames.Text(State);
    public string StateIcon => PlayGuideStateNames.Icon(State);
    public string AutomationName => $"{Label}: {StateText}";
}

public sealed record PlayGuideAction(
    string Id,
    string CategoryId,
    string Title,
    string Instruction,
    PlayGuideDisplayState State,
    IReadOnlyList<string> LinkedChecklistIds,
    IReadOnlyList<PlayGuideSubtask> Subtasks,
    bool IsAutomatic,
    bool HasMappingWarning)
{
    public string StateText => PlayGuideStateNames.Text(State);
    public string StateIcon => PlayGuideStateNames.Icon(State);
    public string AutomationName => $"{Title}: {StateText}";
    public bool IsDone => State == PlayGuideDisplayState.Done;
    public bool HasSubtasks => Subtasks.Count > 0;
}

public sealed record PlayGuideCategory(
    string Id,
    string Name,
    int CompletedCount,
    int TotalCount,
    double Percentage,
    string NextRecommendedAction,
    IReadOnlyList<PlayGuideAction> Actions)
{
    public string CompletionText => $"{CompletedCount} of {TotalCount} done";
    public string PercentageText => $"{Percentage:0}%";
    public string AutomationName => $"{Name}: {CompletionText}, {PercentageText}";
}

public static class PlayGuideStateNames
{
    public static string Text(PlayGuideDisplayState state) => state switch
    {
        PlayGuideDisplayState.ToDo => "TO DO",
        PlayGuideDisplayState.InProgress => "IN PROGRESS",
        PlayGuideDisplayState.Done => "DONE",
        PlayGuideDisplayState.Waiting => "WAITING",
        PlayGuideDisplayState.Retry => "RETRY",
        _ => "TO DO"
    };

    public static string Icon(PlayGuideDisplayState state) => state switch
    {
        PlayGuideDisplayState.ToDo => "○",
        PlayGuideDisplayState.InProgress => "◔",
        PlayGuideDisplayState.Done => "✓",
        PlayGuideDisplayState.Waiting => "…",
        PlayGuideDisplayState.Retry => "↻",
        _ => "○"
    };
}

public sealed record CoverageRow(
    string RowId,
    string Category,
    string SymbolPath,
    string Type,
    string Source,
    string Relevance,
    string ReadStatus,
    string NaturalObservationStatus,
    string ArgumentMetadataStatus,
    string OwnershipAuthorityStatus,
    string VisibilityDirection,
    string LifecycleCoverage,
    string PersistenceUiCoverage,
    string WriteApplyStatus,
    string SafetyClassification,
    string TerminalDisposition,
    string NextRequiredObservation,
    IReadOnlyList<string> ChecklistLinks,
    IReadOnlyList<string> CoverageCapabilities,
    bool DirtyEvidence,
    bool CrashSuspect)
{
    public bool NeedsCoverage => !CoverageTerminalStates.IsTerminal(TerminalDisposition) || DirtyEvidence || CrashSuspect;
}

public static class CoverageTerminalStates
{
    private static readonly HashSet<string> Terminal = new(StringComparer.OrdinalIgnoreCase)
    {
        "confirmed-clean", "confirmed_clean", "confirmed",
        "unsafe-rejected", "unsafe_rejected", "unsafe",
        "unsupported",
        "policy-excluded", "policy_excluded", "intentionally-excluded",
        "excluded-product-policy", "rejected-unsafe"
    };

    public static bool IsTerminal(string? value) => !string.IsNullOrWhiteSpace(value) && Terminal.Contains(value);
}

public sealed record CapabilityReadiness(
    string Category,
    bool Complete,
    int TotalRows,
    int ClosedRows,
    int NeedsCoverageRows,
    string Summary);

public sealed record GameInstallation(string InstallDirectory, string ExecutablePath, string Source)
{
    public bool Exists => Directory.Exists(InstallDirectory) && File.Exists(ExecutablePath);
}

public sealed record InstallResult(int Copied, int Unchanged, IReadOnlyList<string> RelativeFiles);

public sealed record LocalCampaignState(
    int SchemaVersion,
    string CampaignId,
    string CampaignName,
    long Generation,
    string SessionId,
    string MachineId,
    CampaignRole Role,
    string GameDirectory,
    string ExecutablePath,
    string StatusDirectory,
    string Phase,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string LastBundlePath);

public sealed record CollectionResult(
    string BundleDirectory,
    string ZipPath,
    int FileCount,
    bool CrashSuspected,
    bool DirtyEvidence,
    string SummaryPath);

public sealed record BundleFileEntry(
    string Path,
    long SizeBytes,
    string Hash,
    string SourceHash,
    string Kind);

public sealed record BundleSafety(
    bool WritesDisabled,
    bool RpcCallsDisabled,
    bool MutationDisabled,
    bool RawIdentityDisabled,
    bool HudHookDisabled)
{
    public static BundleSafety ReadOnly { get; } = new(true, true, true, true, true);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllDisabled => WritesDisabled && RpcCallsDisabled && MutationDisabled
                               && RawIdentityDisabled && HudHookDisabled;
}

public sealed record BundleManifest(
    int SchemaVersion,
    string BundleFormat,
    string CampaignId,
    string CampaignName,
    string ProfileId,
    long CampaignGeneration,
    string MachineId,
    string SessionId,
    string SelectedRole,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset CollectedAtUtc,
    bool CrashSuspected,
    bool DirtyEvidence,
    BundleSafety Safety,
    int EvidenceFileCount,
    string CatalogSchemaVersion,
    string CatalogHash,
    bool ManifestSelfExcluded,
    IReadOnlyList<BundleFileEntry> Files);

public sealed record CorrelationResult(
    bool HasHost,
    bool HasJoinedClient,
    bool CampaignMatches,
    bool CorrelationEstablished,
    string ReportPath,
    string ZipPath,
    IReadOnlyList<BundleManifest> Manifests);

public sealed record DashboardPreferences(
    int SchemaVersion,
    CampaignRole SelectedRole,
    string GameDirectory,
    string LastCampaignStatePath,
    string LastExportDirectory,
    bool NeedsCoverageOnly,
    string CampaignName = "CrabSync Full Observe");
