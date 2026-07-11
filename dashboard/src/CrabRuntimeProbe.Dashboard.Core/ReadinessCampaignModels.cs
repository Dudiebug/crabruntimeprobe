using System.Security.Cryptography;
using System.Text;

namespace CrabRuntimeProbe.Dashboard.Core;

/// <summary>
/// Contracts for the opt-in, read-only paired readiness campaign. These values are deliberately
/// separate from the Normal Play Guide so the hook-free profile remains the default.
/// </summary>
public static class ReadinessCampaignContracts
{
    public const string ProfileId = "crabsync-readiness-campaign";
    public const string CampaignId = "crabsync-readiness-campaign";
    public const string ManifestSchema = "readiness-campaign-manifest-v1";
    public const string DefaultCampaignName = "CrabSync Readiness Campaign";
    public const string DeferredInventoryStage = "disabled";
    public const int CorrelationCodeLength = 8;
    public const int MaxPeers = 4;
    public const int MaxDeferredInventoryItems = 32;
    public const int MaxDeferredEnhancements = 16;
    // Health is sampled by the same reviewed scalar pass as the other local fields.
    // Keep the manifest cadence truthful; there is no separate 250ms health loop.
    public const double HealthIntervalSeconds = 1;
    public const double ScalarIntervalSeconds = 1;
    // Required by the manifest contract as a declared dormant cadence only. It never authorizes
    // inventory collection and is not written to the runtime configuration.
    public const double DisabledInventoryIntervalSeconds = 2;
    public const double UnchangedHeartbeatSeconds = 30;

    // The code never leaves dashboard-local state. Game config, runtime status, manifests,
    // evidence, and logs receive only the derived opaque pair ID.
    private const string CorrelationAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly string[] RequiredChannels =
    {
        "health", "crystals", "slots", "equipment", "peer-snapshots"
    };

    public static bool IsReadinessProfile(string? profileId) =>
        string.Equals(profileId?.Trim(), ProfileId, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeCorrelationCode(string value)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) && character != '-')
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (normalized.Length != CorrelationCodeLength || normalized.Any(character => !CorrelationAlphabet.Contains(character)))
        {
            throw new ArgumentException(
                "The readiness correlation code must contain exactly eight characters from A-Z and 2-9 (excluding I, O, 0, and 1).",
                nameof(value));
        }

        return normalized;
    }

    public static string GenerateCorrelationCode()
    {
        Span<byte> bytes = stackalloc byte[CorrelationCodeLength];
        RandomNumberGenerator.Fill(bytes);
        var output = new char[CorrelationCodeLength];
        for (var index = 0; index < output.Length; index++)
            output[index] = CorrelationAlphabet[bytes[index] % CorrelationAlphabet.Length];
        return new string(output);
    }

    public static string DerivePairId(string correlationCode)
    {
        var normalized = NormalizeCorrelationCode(correlationCode);
        var material = Encoding.UTF8.GetBytes($"CrabRuntimeProbe/readiness-pair-v1:{normalized}");
        var hash = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        return $"readiness-pair-{hash[..24]}";
    }

    public static bool IsOpaqueIdentifier(string? value) =>
        value is { Length: >= 8 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    // Do not accept an arbitrary opaque token here. In particular, the eight-character
    // human pairing code itself is syntactically opaque, so accepting it would make it
    // possible to persist the code into runtime evidence by mistake.
    public static bool IsOpaquePairId(string? value) =>
        value is { Length: 39 }
        && value.StartsWith("readiness-pair-", StringComparison.Ordinal)
        && value[15..].All(character => char.IsAsciiDigit(character)
                                       || character is >= 'a' and <= 'f');

    public static bool IsDeferredInventoryStage(string? stage) =>
        string.Equals(stage?.Trim(), DeferredInventoryStage, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> NormalizeChannels(IEnumerable<string>? requestedChannels = null)
    {
        var allowed = new HashSet<string>(RequiredChannels, StringComparer.OrdinalIgnoreCase);
        var selected = (requestedChannels ?? DefaultChannels())
            .Select(channel => (channel ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-'))
            .Where(channel => channel.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count != allowed.Count || selected.Any(channel => !allowed.Contains(channel))
            || allowed.Any(channel => !selected.Contains(channel, StringComparer.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Readiness must collect every reviewed scalar and peer channel; inventory collection is deferred.",
                nameof(requestedChannels));
        }

        return selected.OrderBy(channel => channel, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> DefaultChannels() =>
        RequiredChannels.ToArray();

    public static bool HasRequiredChannels(IEnumerable<string>? channels)
    {
        if (channels is null) return false;
        var normalized = channels
            .Select(channel => (channel ?? string.Empty).Trim().ToLowerInvariant())
            .Where(channel => channel.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == RequiredChannels.Length
               && RequiredChannels.All(channel => normalized.Contains(channel, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Dashboard-local pairing state. CorrelationCode is intentionally excluded from all result
/// manifests and runtime configuration; it is retained here only so a dashboard restart can
/// continue the same offline pairing workflow.
/// </summary>
public sealed record ReadinessCampaignLocalPairing(
    string CorrelationCode,
    string PairId,
    string ManifestId,
    string InventoryStage,
    IReadOnlyList<string> EnabledChannels,
    DateTimeOffset CreatedAtUtc)
{
    public bool HasValidPair => ReadinessCampaignContracts.IsOpaquePairId(PairId)
                                && ReadinessCampaignContracts.IsOpaqueIdentifier(ManifestId)
                                && ReadinessCampaignContracts.IsDeferredInventoryStage(InventoryStage)
                                && ReadinessCampaignContracts.HasRequiredChannels(EnabledChannels);
}

/// <summary>
/// Persisted under the game results directory. This mirrors
/// readiness-campaign-manifest-v1 and intentionally has no correlation code field.
/// </summary>
public sealed record ReadinessCampaignManifest(
    string SchemaVersion,
    string ManifestId,
    string CampaignId,
    long CampaignGeneration,
    string SessionId,
    string MachineId,
    string SelectedRole,
    string ProfileId,
    string PairId,
    DateTimeOffset PreparedAtUtc,
    string InventoryStage,
    IReadOnlyList<string> EnabledChannels,
    bool PeerSnapshotsEnabled,
    int MaxPeers,
    ReadinessIntervals Intervals,
    ReadinessManifestSafety Safety);

public sealed record ReadinessIntervals(
    double HealthSeconds,
    double ScalarSeconds,
    double InventorySeconds,
    double UnchangedHeartbeatSeconds);

public sealed record ReadinessManifestSafety(
    bool ReadOnly,
    bool WriteProbes,
    bool RpcCalls,
    bool Mutation,
    bool Hooks,
    bool RuntimeDiscovery,
    bool DeepInventory,
    bool RawIdentity);

/// <summary>
/// Typed, additive status published by the runtime while a readiness campaign is active.
/// It contains only pair-safe identifiers and bounded aggregate counts.
/// </summary>
public sealed record ReadinessCampaignStatus(
    bool Enabled,
    string PairId,
    string ManifestId,
    string InventoryStage,
    string StageState,
    IReadOnlyList<string> EnabledChannels,
    bool? SafeReadChannelsReady,
    int VisiblePlayerCount,
    int StablePlayerCount,
    long PeerSnapshotCount,
    int InventoryCategoryCount,
    int MaxPeers,
    int MaxInventoryItems,
    int MaxEnhancements,
    string Detail)
{
    public bool HasValidContract => Enabled
                                    && ReadinessCampaignContracts.IsOpaquePairId(PairId)
                                    && ReadinessCampaignContracts.IsOpaqueIdentifier(ManifestId)
                                    && ReadinessCampaignContracts.IsDeferredInventoryStage(InventoryStage)
                                    && ReadinessCampaignContracts.HasRequiredChannels(EnabledChannels)
                                    && MaxPeers is >= 1 and <= ReadinessCampaignContracts.MaxPeers
                                    && VisiblePlayerCount >= 0
                                    && VisiblePlayerCount <= MaxPeers
                                    && StablePlayerCount >= 0
                                    && StablePlayerCount <= MaxPeers
                                    && PeerSnapshotCount >= 0
                                    // Disabled means disabled: a status that advertises an inventory
                                    // budget/category is not a v1.1 readiness status.
                                    && InventoryCategoryCount == 0
                                    && MaxInventoryItems == 0
                                    && MaxEnhancements == 0;
}
