using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public static class ReadinessCampaignContractSelfTest
{
    public static IReadOnlyList<string> Run()
    {
        var messages = new List<string>();
        const string code = "ABCD2345";
        var pairId = ReadinessCampaignContracts.DerivePairId(code);
        Require(ReadinessCampaignContracts.NormalizeCorrelationCode("abcd-2345") == code,
            "readiness correlation code normalization", messages);
        Require(ReadinessCampaignContracts.IsOpaquePairId(pairId), "readiness opaque pair ID", messages);
        Require(!ReadinessCampaignContracts.IsOpaquePairId(code),
            "human correlation code cannot be persisted as pair ID", messages);
        Require(ReadinessCampaignContracts.NormalizeChannels().SequenceEqual(
                new[] { "crystals", "equipment", "health", "peer-snapshots", "slots" }, StringComparer.Ordinal),
            "readiness scalar and peer channel contract", messages);
        RequireThrows(() => ReadinessCampaignContracts.NormalizeChannels(new[] { "inventory-count", "peer-snapshots" }),
            "readiness inventory channels remain deferred", messages);

        var manifest = new ReadinessCampaignManifest(
            ReadinessCampaignContracts.ManifestSchema,
            "readiness-manifest-12345678",
            ReadinessCampaignContracts.CampaignId,
            1,
            "readiness-session-12345678",
            "readiness-machine-12345678",
            "host",
            ReadinessCampaignContracts.ProfileId,
            pairId,
            DateTimeOffset.Parse("2026-07-11T00:00:00Z"),
            ReadinessCampaignContracts.DeferredInventoryStage,
            ReadinessCampaignContracts.DefaultChannels(),
            true,
            ReadinessCampaignContracts.MaxPeers,
            new ReadinessIntervals(
                ReadinessCampaignContracts.HealthIntervalSeconds,
                ReadinessCampaignContracts.ScalarIntervalSeconds,
                ReadinessCampaignContracts.DisabledInventoryIntervalSeconds,
                ReadinessCampaignContracts.UnchangedHeartbeatSeconds),
            new ReadinessManifestSafety(true, false, false, false, false, false, false, false));
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Require(!manifestJson.Contains(code, StringComparison.Ordinal)
                && manifestJson.Contains(pairId, StringComparison.Ordinal)
                && manifestJson.Contains("\"inventoryStage\":\"disabled\"", StringComparison.Ordinal),
            "readiness manifest omits local correlation code", messages);

        var snapshot = new LiveStatusReader().Parse($$"""
        {
          "schemaVersion": 1,
          "sequence": 1,
          "writtenAtUtc": "2026-07-11T00:00:00Z",
          "heartbeatAtUtc": "2026-07-11T00:00:00Z",
          "campaignId": "crabsync-readiness-campaign",
          "campaignName": "Readiness",
          "campaignGeneration": 1,
          "machineId": "readiness-machine-12345678",
          "sessionId": "readiness-session-12345678",
          "selectedRole": "host",
          "observedRole": "host",
          "authorityStatus": "runtime-authority",
          "lifecycle": { "state": "stable", "generation": 1, "world": "island", "context": "run", "stable": true },
          "runtime": {
            "gameProcessRunning": true,
            "gameProcessState": "running",
            "ue4ssState": "loaded",
            "runtimeProbeState": "active",
            "runtimeProbeLoaded": true,
            "activeProfile": "crabsync-readiness-campaign",
            "collectionReady": true
          },
          "safety": {
            "writesDisabled": true, "rpcsDisabled": true, "mutationDisabled": true,
            "hudHookDisabled": true, "rawIdentityDisabled": true, "hooksDisabled": true,
            "runtimeDiscoveryDisabled": true, "inventoryStagesDisabled": true, "inventoryDepth": 0
          },
          "readiness": {
            "enabled": true,
            "pairId": "{{pairId}}",
            "manifestId": "readiness-manifest-12345678",
            "inventoryStage": "disabled",
            "stageState": "deferred",
            "enabledChannels": ["health", "crystals", "slots", "equipment", "peer-snapshots"],
            "safeReadChannelsReady": true,
            "visiblePlayerCount": 1,
            "stablePlayerCount": 1,
            "peerSnapshotCount": 2,
            "inventoryCategoryCount": 0,
            "maxPeers": 4,
            "maxInventoryItems": 0,
            "maxEnhancements": 0,
            "detail": "bounded local scalar snapshots; remote visibility deferred"
          },
          "evidenceHealth": { "state": "healthy", "canonicalRows": 2, "rejectedRows": 0, "dirtyRows": 0 },
          "crashSuspected": false,
          "dirtyEvidence": false
        }
        """);
        var dashboard = new LiveDashboardReducer().Reduce(new LiveStatusReadResult(
            snapshot, true, false, false, string.Empty, DateTimeOffset.Parse("2026-07-11T00:00:01Z")), true);
        Require(snapshot.Readiness is { HasValidContract: true }
                && dashboard.SafetyProven
                && dashboard.CollectionReady
                && dashboard.ReadinessText.Contains("Inventory deferred", StringComparison.Ordinal),
            "typed readiness status is fail-closed and inventory-deferred", messages);
        return messages;
    }

    private static void Require(bool condition, string name, ICollection<string> messages)
    {
        if (!condition) throw new InvalidOperationException($"Readiness self-test failed: {name}");
        messages.Add($"PASS {name}");
    }

    private static void RequireThrows(Action action, string name, ICollection<string> messages)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Readiness self-test failed: {name}");
        }
        catch (ArgumentException)
        {
            messages.Add($"PASS {name}");
        }
    }
}
