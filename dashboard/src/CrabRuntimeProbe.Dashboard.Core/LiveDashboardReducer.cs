using System.Globalization;

namespace CrabRuntimeProbe.Dashboard.Core;

public static class NormalObservationCapabilities
{
    private static readonly IReadOnlySet<string> HookFreeSnapshotChecklistIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "health-damage",
            "health-healing",
            "health-current-change",
            "health-current-max-change",
            "resource-crystal-gain",
            "resource-crystal-spend",
            "transaction-equipment-change",
            "slot-weapon-increment",
            "slot-ability-increment",
            "slot-melee-increment",
            "slot-perk-increment"
        };

    private static readonly IReadOnlySet<string> None =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static ObservationCapabilityProfile ForProfile(string? profileId)
    {
        var normalized = (profileId ?? string.Empty).Trim();
        if (normalized.Equals("crabsync-full-observe", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("crabsync-snapshot-play-guide", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("normal-play-guide", StringComparison.OrdinalIgnoreCase))
        {
            return new ObservationCapabilityProfile(
                normalized.Length == 0 ? "crabsync-full-observe" : normalized,
                HookFreeSnapshotChecklistIds,
                "This hook-free profile detects only reviewed health, crystal, slot, and equipment state changes. "
                + "It cannot prove exact callbacks, pickup identity, inventory counts, persistence, or RPC activity.");
        }

        return new ObservationCapabilityProfile(
            normalized.Length == 0 ? "unknown" : normalized,
            None,
            "The active profile did not publish a reviewed observation-capability contract, so no action is presented as detectable.");
    }
}

public sealed class LiveDashboardReducer
{
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromSeconds(5);

    public LiveDashboardStatus Reduce(
        LiveStatusReadResult status,
        bool localGameRunning = false,
        bool monitoringExpected = false,
        bool collectionStopped = false)
    {
        ArgumentNullException.ThrowIfNull(status);

        var snapshot = status.Snapshot;
        var profile = FirstNonEmpty(snapshot.Runtime.ActiveProfile, snapshot.CampaignId, "unknown");
        var capabilities = NormalObservationCapabilities.ForProfile(profile);
        if (!status.HasSnapshot)
        {
            var waiting = localGameRunning;
            return LiveDashboardStatus.Empty with
            {
                State = waiting ? LiveCollectionState.Warming : LiveCollectionState.GameUnavailable,
                StateText = waiting ? "WARMING" : "GAME UNAVAILABLE",
                Detail = waiting
                    ? "The game is available, but no completed RuntimeProbe status snapshot has arrived yet."
                    : monitoringExpected
                        ? "The campaign is prepared, but Crab Champions and its status writer are not available yet."
                        : "Start Crab Champions and wait for a completed RuntimeProbe status snapshot.",
                ReadinessText = waiting ? "Waiting for writer" : "Not ready",
                ActiveProfile = profile,
                Capabilities = capabilities
            };
        }

        var rawAge = status.ReadAtUtc - snapshot.HeartbeatAtUtc;
        var clockSkew = rawAge < -MaximumFutureClockSkew;
        var heartbeatAge = rawAge < TimeSpan.Zero ? TimeSpan.Zero : rawAge;
        var category = FirstNonEmpty(
            snapshot.Runtime.CurrentSamplingCategory,
            CategoryFromStage(snapshot.Runtime.CurrentProbeStage));
        var stopped = collectionStopped
                      || snapshot.Runtime.StopRequested
                      || IsOneOf(snapshot.Runtime.RuntimeProbeState, "stopped", "stop-requested", "complete", "completed");
        var safetyProven = snapshot.Safety.IsSafeForProfile(profile);
        var faulted = snapshot.CrashSuspected
                      || snapshot.DirtyEvidence
                      || !safetyProven
                      || ContainsFault(snapshot.Runtime.RuntimeProbeState)
                      || ContainsFault(snapshot.EvidenceHealth.State)
                      || clockSkew;
        var stale = status.IsStale || status.UsedLastGood;
        var gameRunning = localGameRunning || snapshot.Runtime.GameProcessRunning;

        LiveCollectionState state;
        if (faulted) state = LiveCollectionState.Faulted;
        else if (stopped) state = LiveCollectionState.Stopped;
        else if (stale) state = LiveCollectionState.Stale;
        else if (!gameRunning) state = LiveCollectionState.GameUnavailable;
        else if (!snapshot.Lifecycle.Stable) state = LiveCollectionState.Warming;
        else if (!string.IsNullOrWhiteSpace(category)) state = LiveCollectionState.Collecting;
        else if (snapshot.Runtime.CollectionReady == false) state = LiveCollectionState.Stable;
        else state = LiveCollectionState.Ready;

        var ready = state is LiveCollectionState.Ready or LiveCollectionState.Collecting;
        var fresh = !stale && !clockSkew;
        return new LiveDashboardStatus(
            state,
            StateText(state),
            Detail(state, status, snapshot, category, clockSkew),
            heartbeatAge,
            $"{FormatAge(heartbeatAge)} ago",
            snapshot.Sequence,
            FormatSequence(snapshot.Sequence, status.PreviousSequence),
            status.PreviousSequence is { } previous && snapshot.Sequence > previous,
            profile,
            category,
            string.IsNullOrWhiteSpace(category) ? "Not sampling" : FriendlyCategory(category),
            ReadinessText(state, snapshot),
            ready,
            fresh,
            safetyProven,
            clockSkew,
            capabilities);
    }

    private static string StateText(LiveCollectionState state) => state switch
    {
        LiveCollectionState.GameUnavailable => "GAME UNAVAILABLE",
        LiveCollectionState.Warming => "WARMING",
        LiveCollectionState.Stable => "STABLE",
        LiveCollectionState.Ready => "READY",
        LiveCollectionState.Collecting => "COLLECTING",
        LiveCollectionState.Stale => "STALE",
        LiveCollectionState.Stopped => "STOPPED",
        LiveCollectionState.Faulted => "FAULTED",
        _ => "UNKNOWN"
    };

    private static string Detail(
        LiveCollectionState state,
        LiveStatusReadResult status,
        LiveStatusSnapshot snapshot,
        string category,
        bool clockSkew) => state switch
    {
        LiveCollectionState.GameUnavailable =>
            "No running game is visible. The last completed status is retained for diagnostics only.",
        LiveCollectionState.Warming => StabilityProgress(snapshot.Lifecycle),
        LiveCollectionState.Stable =>
            "The lifecycle scope is stable; RuntimeProbe is completing its collection-readiness checks.",
        LiveCollectionState.Ready =>
            "The heartbeat is fresh, the hook-free safety contract is proven, and collection is ready.",
        LiveCollectionState.Collecting =>
            $"Collection is active in the {FriendlyCategory(category)} sampling category.",
        LiveCollectionState.Stale =>
            "The status writer has stopped advancing. Retained values are not treated as healthy collection.",
        LiveCollectionState.Stopped =>
            "RuntimeProbe reported a clean stop; no new heartbeat is expected for this run.",
        LiveCollectionState.Faulted when clockSkew =>
            "The heartbeat timestamp is too far in the future, so freshness cannot be proven.",
        LiveCollectionState.Faulted when !snapshot.Safety.AllRequiredSafe =>
            "The live snapshot does not prove the complete hook-free normal-mode safety contract.",
        LiveCollectionState.Faulted when snapshot.CrashSuspected =>
            "RuntimeProbe marked this run crash-suspect; collection is not considered healthy.",
        LiveCollectionState.Faulted => string.IsNullOrWhiteSpace(status.Error)
            ? "RuntimeProbe reported faulted or dirty evidence. Review diagnostics before collecting."
            : status.Error,
        _ => "Runtime status is unavailable."
    };

    private static string ReadinessText(LiveCollectionState state, LiveStatusSnapshot snapshot) => state switch
    {
        LiveCollectionState.GameUnavailable => "Not ready",
        LiveCollectionState.Warming => snapshot.Lifecycle.StableSamplesRequired > 0
            ? $"{snapshot.Lifecycle.StableSamples}/{snapshot.Lifecycle.StableSamplesRequired} stable samples"
            : "Stability barrier pending",
        LiveCollectionState.Stable => "Stable; checks pending",
        LiveCollectionState.Ready => "Ready for collection",
        LiveCollectionState.Collecting => "Collection active",
        LiveCollectionState.Stale => "Writer stale",
        LiveCollectionState.Stopped => "Collection stopped",
        LiveCollectionState.Faulted => "Collection blocked",
        _ => "Not ready"
    };

    private static string StabilityProgress(LifecycleInfo lifecycle)
    {
        if (lifecycle.StableSamplesRequired > 0 || lifecycle.StableDwellSecondsRequired > 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Warming: {0}/{1} stable samples and {2:0}/{3:0} seconds dwell.",
                lifecycle.StableSamples,
                lifecycle.StableSamplesRequired,
                lifecycle.StableDwellSeconds,
                lifecycle.StableDwellSecondsRequired);
        }

        return "RuntimeProbe is warming up and waiting for a stable world and local PlayerState.";
    }

    private static string FormatSequence(long sequence, long? previous) => previous switch
    {
        null => $"{sequence} (first seen)",
        var value when sequence > value => $"{sequence} (+{sequence - value})",
        var value when sequence == value => $"{sequence} (unchanged)",
        _ => $"{sequence} (reset)"
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(1)) return "<1s";
        if (age < TimeSpan.FromMinutes(1)) return $"{Math.Floor(age.TotalSeconds):0}s";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m {age.Seconds:00}s";
        return $"{(int)age.TotalHours}h {age.Minutes:00}m";
    }

    private static string CategoryFromStage(string? stage)
    {
        var parts = (stage ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !parts[0].Equals("snapshot", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return parts[1].Equals("waiting-for-stable-game", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : parts[1];
    }

    private static string FriendlyCategory(string category) => category.Trim().ToLowerInvariant() switch
    {
        "health" => "Health",
        "crystals" => "Crystals",
        "slots" => "Inventory slots",
        "equipment" => "Equipment",
        "inventory" or "inventory-counts" => "Inventory counts",
        "lifecycle" => "Lifecycle",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(category.Replace('-', ' '))
    };

    private static bool ContainsFault(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return new[] { "fault", "error", "failed", "unsafe", "rejected", "crash", "role-mismatch" }
            .Any(text.Contains);
    }

    private static bool IsOneOf(string? value, params string[] expected) =>
        expected.Any(item => item.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
