namespace CrabRuntimeProbe.Dashboard.Core;

public static class CoreSelfTest
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var messages = new List<string>();
        var parser = new LiveStatusReader();
        var snapshot = parser.Parse(DemoStatus.Json, "embedded-demo");
        Require(snapshot.Sequence == 7, "status sequence", messages);
        Require(snapshot.Safety.AllRequiredSafe, "safety markers", messages);
        var checklist = new ChecklistReducer().Reduce(snapshot);
        Require(checklist.Single(item => item.Id == "transaction-server-interact").State == ChecklistDisplayState.InProgress,
            "hook registration is not completion", messages);
        Require(checklist.Single(item => item.Id == "health-damage").State == ChecklistDisplayState.Confirmed,
            "qualifying natural evidence completes", messages);
        var redacted = new EvidenceRedactor().Redact("{\"UniqueId\":\"76561198000000000\",\"PlayerName\":\"Dylan\"}");
        Require(!redacted.Contains("76561198000000000", StringComparison.Ordinal)
                && !redacted.Contains("Dylan", StringComparison.Ordinal), "identity redaction", messages);
        messages.AddRange(await ResearchContractSelfTest.RunAsync().ConfigureAwait(false));
        return messages;
    }

    private static void Require(bool condition, string name, ICollection<string> messages)
    {
        if (!condition) throw new InvalidOperationException($"Self-test failed: {name}");
        messages.Add($"PASS {name}");
    }
}

public static class DemoStatus
{
    public const string Json = """
    {
      "schemaVersion": 1,
      "sequence": 7,
      "writtenAtUtc": "2026-07-10T18:00:00Z",
      "heartbeatAtUtc": "2026-07-10T18:00:00Z",
      "campaignId": "crabsync-full-observe",
      "campaignName": "Demo Full Observe",
      "campaignGeneration": 42,
      "machineId": "demo-machine",
      "sessionId": "demo-session",
      "selectedRole": "host",
      "observedRole": "host",
      "authorityStatus": "authority",
      "lifecycle": { "state": "stable", "generation": 2, "world": "Island", "context": "run", "stable": true },
      "runtime": { "gameProcessRunning": true, "gameProcessState": "running", "ue4ssState": "loaded", "runtimeProbeState": "active", "runtimeProbeLoaded": true, "probeStage": "passive-observe", "currentProbeStage": "passive-observe", "activeProfile": "crabsync-full-observe", "collectionReady": true, "stopRequested": false },
      "safety": {
        "writesDisabled": true, "rpcsDisabled": true, "mutationDisabled": true,
        "hudHookDisabled": true, "rawIdentityDisabled": true, "hooksDisabled": true,
        "runtimeDiscoveryDisabled": true, "inventoryStagesDisabled": true, "inventoryDepth": 2,
        "circuitBreakers": { "inventory": "closed", "health": "closed" }
      },
      "checklist": {
        "transaction-server-interact": { "status": "confirmed", "observationCount": 0, "hookRegistered": true, "qualifyingEvidence": false, "evidenceKinds": ["hook-registration"] },
        "health-damage": { "status": "confirmed", "observationCount": 1, "qualifyingEvidence": true, "evidenceKinds": ["natural-call"], "sourceRoles": ["host"], "evidenceSessions": ["demo-session"] }
      },
      "evidenceHealth": { "state": "healthy", "canonicalRows": 12, "rejectedRows": 0, "dirtyRows": 0 },
      "crashSuspected": false,
      "dirtyEvidence": false,
      "additiveFutureField": { "safe": true }
    }
    """;
}
