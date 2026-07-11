using System.Collections.ObjectModel;

namespace CrabRuntimeProbe.Dashboard.Core;

public enum SnapshotValueKind
{
    Null,
    Number,
    String,
    Boolean,
    Json
}

/// <summary>
/// Canonical observation-profile identities used to keep replay and export evidence
/// within the active campaign mode. Historical normal-mode aliases intentionally
/// normalize to the hook-free profile emitted by snapshot-observation-v1.
/// </summary>
public static class ObservationProfileIds
{
    public const string NormalPlayGuide = "normal-play-guide";
    public const string ReadinessCampaign = "crabsync-readiness-campaign";
    public const string ProgressiveBroadObservation = "progressive-broad-observation";

    public static string Normalize(string? profileId)
    {
        var normalized = (profileId ?? string.Empty).Trim().ToLowerInvariant()
            .Replace('_', '-').Replace(' ', '-');
        return normalized switch
        {
            "" or NormalPlayGuide or "crabsync-full-observe" or "crabsync-snapshot-play-guide" =>
                NormalPlayGuide,
            ReadinessCampaign => ReadinessCampaign,
            ProgressiveBroadObservation => ProgressiveBroadObservation,
            _ => normalized
        };
    }

    public static bool IsKnown(string? profileId) => !string.IsNullOrWhiteSpace(profileId)
                                                      && Normalize(profileId) is
                                                          NormalPlayGuide or ReadinessCampaign
                                                          or ProgressiveBroadObservation;

    public static bool IsReadinessReviewedCategory(string? category) =>
        category is "health" or "crystals" or "slots" or "equipment";
}

/// <summary>A normalized value from a passive snapshot. Canonical values make replay deterministic.</summary>
public sealed record SnapshotValue(
    SnapshotValueKind Kind,
    string Canonical,
    decimal? Number = null,
    string? Text = null,
    bool? Boolean = null);

public sealed record SnapshotObservedField(string Status, SnapshotValue? Value)
{
    public bool IsObserved =>
        Value is not null && (Status.Equals("observed", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("unchanged", StringComparison.OrdinalIgnoreCase));
}

public sealed record SnapshotStability(
    bool Stable,
    int SampleCount,
    double DwellSeconds,
    bool WorldStable,
    bool PlayerStateStable,
    string Reason)
{
    public const int MinimumSampleCount = 10;
    public const double MinimumDwellSeconds = 30;

    public bool IsFullyStable => Stable && WorldStable && PlayerStateStable
                                 && SampleCount >= MinimumSampleCount
                                 && DwellSeconds >= MinimumDwellSeconds;
}

public sealed record SnapshotSafety(
    bool WritesDisabled,
    bool RpcCallsDisabled,
    bool HooksDisabled,
    bool MutationDisabled,
    bool RuntimeDiscoveryDisabled,
    bool InventoryStagesDisabled,
    bool RawIdentityDisabled)
{
    public bool IsReadOnlyAndHookFree =>
        WritesDisabled && RpcCallsDisabled && HooksDisabled && MutationDisabled
        && RuntimeDiscoveryDisabled && InventoryStagesDisabled && RawIdentityDisabled;
}

/// <summary>The snapshot-observation-v1 contract emitted by the hook-free in-game sampler.</summary>
public sealed record SnapshotObservation(
    int SchemaVersion,
    string RecordType,
    string CampaignId,
    string SessionId,
    long CampaignGeneration,
    string MachineId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    long LifecycleGeneration,
    string Context,
    CampaignRole SelectedRole,
    string ObservedRole,
    string WorldFingerprint,
    string PlayerStateFingerprint,
    string Category,
    SnapshotStability Stability,
    IReadOnlyDictionary<string, SnapshotObservedField> Fields,
    SnapshotSafety Safety,
    bool DirtyEvidence,
    bool CrashSuspected,
    string ObservationProfile = "normal-play-guide");

public sealed record SnapshotJsonlRejection(int LineNumber, string Code, string Detail);

public sealed record SnapshotJsonlReadResult(
    IReadOnlyList<SnapshotObservation> Observations,
    IReadOnlyList<SnapshotJsonlRejection> Rejections,
    int NonEmptyLineCount)
{
    public bool HasMalformedRows => Rejections.Count > 0;
}

/// <summary>Expected identity for a deterministic replay. Non-empty optional values are enforced.</summary>
public sealed record SnapshotReplayScope(
    string SessionId,
    long CampaignGeneration,
    string CampaignId = "",
    CampaignRole SelectedRole = CampaignRole.Unknown,
    string ObservedRole = "",
    string MachineId = "",
    string ObservationProfile = ObservationProfileIds.NormalPlayGuide)
{
    public string NormalizedObservationProfile => ObservationProfileIds.Normalize(ObservationProfile);

    public static SnapshotReplayScope FromCampaign(LocalCampaignState campaign) => new(
        campaign.SessionId,
        campaign.Generation,
        campaign.CampaignId,
        campaign.Role,
        ObservedRole: string.Empty,
        MachineId: campaign.MachineId,
        ObservationProfile: ObservationProfileIds.Normalize(campaign.ProfileId));

    public static SnapshotReplayScope FromCampaign(
        LocalCampaignState campaign,
        string? observationProfile) => new(
        campaign.SessionId,
        campaign.Generation,
        campaign.CampaignId,
        campaign.Role,
        ObservedRole: string.Empty,
        MachineId: campaign.MachineId,
        ObservationProfile: ObservationProfileIds.Normalize(observationProfile));
}

public enum SnapshotDeltaOperator
{
    Changed,
    Increased,
    Decreased
}

/// <summary>Controls which gameplay scope identifiers must remain identical across a delta.</summary>
public sealed record SnapshotRuleScopePolicy(
    bool SameLifecycleGeneration = true,
    bool SameWorldFingerprint = true,
    bool SamePlayerStateFingerprint = true,
    bool SameContext = true,
    bool SameObservedRole = true,
    bool AllowUnstableBridge = false)
{
    public static SnapshotRuleScopePolicy SamePlayerScope { get; } = new();

    public static SnapshotRuleScopePolicy LifecycleTransition { get; } = new(
        SameLifecycleGeneration: false,
        SameWorldFingerprint: false,
        SamePlayerStateFingerprint: false,
        SameContext: false,
        SameObservedRole: true,
        AllowUnstableBridge: true);
}

/// <summary>
/// Data-only qualification rule. Field paths are aliases: the first observed alias is used.
/// A rule never calls game code; it compares previously persisted snapshots.
/// </summary>
public sealed record SnapshotQualificationRule(
    string Id,
    IReadOnlyList<string> ChecklistIds,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> FieldPaths,
    SnapshotDeltaOperator Operator,
    int StableBeforeSamples,
    int StableAfterSamples,
    SnapshotRuleScopePolicy Scope,
    IReadOnlyList<string> BeforeContexts,
    IReadOnlyList<string> AfterContexts,
    string EvidenceDescription);

public sealed record SnapshotQualification(
    string RuleId,
    IReadOnlyList<string> ChecklistIds,
    string SessionId,
    long CampaignGeneration,
    long BeforeSequence,
    long AfterSequence,
    DateTimeOffset BeforeTimestampUtc,
    DateTimeOffset AfterTimestampUtc,
    string Category,
    string FieldPath,
    SnapshotValue Before,
    SnapshotValue After,
    string ObservedRole,
    string Detail);

public sealed record SnapshotEvidenceRejection(
    long? Sequence,
    string Code,
    string Detail,
    string RuleId = "");

public sealed record SnapshotReplayResult(
    IReadOnlyDictionary<string, ChecklistEvidence> Checklist,
    IReadOnlyList<SnapshotQualification> Qualifications,
    IReadOnlyList<SnapshotEvidenceRejection> Rejections,
    int InputRows,
    int AcceptedRows)
{
    public int RejectedRows => Rejections.Count;

    public static SnapshotReplayResult Empty { get; } = new(
        new ReadOnlyDictionary<string, ChecklistEvidence>(
            new Dictionary<string, ChecklistEvidence>(StringComparer.OrdinalIgnoreCase)),
        Array.Empty<SnapshotQualification>(),
        Array.Empty<SnapshotEvidenceRejection>(),
        0,
        0);
}

public sealed record SnapshotEvidenceLoadResult(
    SnapshotReplayResult Replay,
    IReadOnlyList<string> SourceFiles);
