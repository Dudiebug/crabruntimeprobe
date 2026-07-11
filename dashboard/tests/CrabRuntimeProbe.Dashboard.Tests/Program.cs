using CrabRuntimeProbe.Dashboard.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Func<Task> Body)[]
{
    ("status schema version and additive fields", StatusSchemaAsync),
    ("atomic ring partial-read fallback and parser update", RingFallbackAsync),
    ("campaign-scoped last-good status isolation", ScopedStatusAsync),
    ("stale heartbeat plus crash and dirty state", StaleCrashDirtyAsync),
    ("live dashboard freshness, sequence, profile, category, and readiness states", LiveDashboardReducerAsync),
    ("progressive catalog, canary, breadcrumbs, attribution, compatibility, and promotion", ResearchContractsAsync),
    ("hook-free snapshot replay qualification and rejection", SnapshotReplayAsync),
    ("snapshot evidence file filtering and fail-closed merge", SnapshotEvidenceServiceAsync),
    ("active-scope collection and terminal stale snapshot preservation", ScopedTerminalCollectionAsync),
    ("data-driven checklist prerequisites and qualifying evidence", ChecklistAsync),
    ("friend-facing Play Guide projection, mapping, states, and filters", PlayGuideAsync),
    ("real exhaustive coverage catalog aliases and terminal states", CoverageCatalogAsync),
    ("atomic file replacement", AtomicFileAsync),
    ("safe prepare, mods merge, status archive, and resume", PrepareAndResumeAsync),
    ("paired readiness preparation is private and inventory-deferred", ReadinessPrepareAsync),
    ("readiness evidence is closed, cleanly exported, and pair-bound", ReadinessEvidenceAndBundleAsync),
    ("game process handoff grace and confirmed exit", GameProcessExitDetectorAsync),
    ("identity redaction", RedactionAsync),
    ("byte-identical canonical collection and unsafe omission", CollectionAsync),
    ("snapshot-qualified export and evidence-derived safety", SnapshotCollectionSafetyAsync),
    ("strict host-client correlation and tamper rejection", CorrelationAsync),
    ("resource locator packaged and repository modes", ResourceLocatorAsync),
    ("source guards, fixture, and demo", SourceGuardsAndFixturesAsync)
};

static async Task ResearchContractsAsync()
{
    var results = await ResearchContractSelfTest.RunAsync();
    Require(results.Count >= 14 && results.Any(result => result.Contains("every enabled begin boundary", StringComparison.Ordinal)),
        "progressive research contract self-tests did not complete");
}

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static Task StatusSchemaAsync()
{
    var reader = new LiveStatusReader();
    var snapshot = reader.Parse(DemoStatus.Json, "demo");
    Require(snapshot.SchemaVersion == 1 && snapshot.Sequence == 7, "v1 fixture did not parse");
    Require(snapshot.Safety.AllRequiredSafe, "safety state lost");
    var additive = reader.Parse(DemoStatus.Json.Replace(
        "\"additiveFutureField\": { \"safe\": true }",
        "\"additiveFutureField\": { \"safe\": true }, \"futureV1Field\": [1,2,3]"), "additive");
    Require(additive.Sequence == 7, "additive v1 fields were not tolerated");
    var aliasJson = StatusJson(8, DateTimeOffset.UtcNow, 3, "alias-session")
        .Replace("\"currentProbeStage\":\"observe\"", "\"probeStage\":\"inventory-stage\"", StringComparison.Ordinal)
        .Replace("\"rpcsDisabled\":true", "\"rpcCallsDisabled\":true", StringComparison.Ordinal);
    var aliases = reader.Parse(aliasJson, "lua-aliases");
    Require(aliases.Runtime.CurrentProbeStage == "inventory-stage", "runtime.probeStage alias not parsed");
    Require(aliases.Safety.RpcsDisabled, "safety.rpcCallsDisabled alias not parsed");
    Throws<StatusSchemaException>(() => reader.Parse(DemoStatus.Json.Replace(
        "\"schemaVersion\": 1", "\"schemaVersion\": 2")));
    return Task.CompletedTask;
}

static async Task RingFallbackAsync()
{
    using var temp = new TempDirectory();
    var reader = new LiveStatusReader();
    var now = DateTimeOffset.UtcNow;
    await File.WriteAllTextAsync(Path.Combine(temp.Path, "live_status.slot0.json"), StatusJson(1, now, 9, "runtime-a"));
    await File.WriteAllTextAsync(Path.Combine(temp.Path, "live_status.slot1.json"), "{ partial");
    var first = await reader.ReadLatestAsync(temp.Path, now);
    Require(first.Snapshot.Sequence == 1 && !first.UsedLastGood, "completed slot was not selected");
    await File.WriteAllTextAsync(Path.Combine(temp.Path, "live_status.slot0.json"), "{");
    var fallback = await reader.ReadLatestAsync(temp.Path, now.AddSeconds(1));
    Require(fallback.UsedLastGood && fallback.Snapshot.Sequence == 1, "partial-read fallback lost last good snapshot");
    await File.WriteAllTextAsync(Path.Combine(temp.Path, "live_status.slot2.json"), StatusJson(2, now.AddSeconds(2), 9, "runtime-a"));
    var updated = await reader.ReadLatestAsync(temp.Path, now.AddSeconds(2));
    Require(updated.Snapshot.Sequence == 2 && updated.PreviousSequence == 1 && reader.History.Count == 2,
        "parser state did not advance");
}

static async Task ScopedStatusAsync()
{
    using var temp = new TempDirectory();
    var reader = new LiveStatusReader();
    var now = DateTimeOffset.Parse("2026-07-11T18:00:00Z");
    var firstScope = new StatusReadScope(
        "crabsync-full-observe", 1, "session-first", "machine-test", CampaignRole.Host);
    await File.WriteAllTextAsync(
        Path.Combine(temp.Path, "live_status.slot0.json"),
        StatusJson(100, now, 1, "session-first"));
    var first = await reader.ReadLatestAsync(temp.Path, now, scope: firstScope);
    Require(first.HasSnapshot && first.Snapshot.Sequence == 100, "first campaign scope did not load");

    var secondScope = new StatusReadScope(
        "crabsync-full-observe", 2, "session-second", "machine-test", CampaignRole.Host);
    await File.WriteAllTextAsync(Path.Combine(temp.Path, "live_status.slot0.json"), "{ partial");
    await File.WriteAllTextAsync(
        Path.Combine(temp.Path, "live_status.slot1.json"),
        StatusJson(1, now.AddSeconds(1), 2, "session-second"));
    var second = await reader.ReadLatestAsync(temp.Path, now.AddSeconds(1), scope: secondScope);
    Require(second.HasSnapshot && second.Snapshot.Sequence == 1 && !second.UsedLastGood,
        "new generation was contaminated by the prior high sequence");

    await File.WriteAllTextAsync(Path.Combine(temp.Path, "live_status.slot1.json"), "{");
    var secondFallback = await reader.ReadLatestAsync(temp.Path, now.AddSeconds(2), scope: secondScope);
    Require(secondFallback.UsedLastGood && secondFallback.Snapshot.SessionId == "session-second"
            && secondFallback.Snapshot.Sequence == 1,
        "same-scope fallback did not retain the second campaign snapshot");

    var absentScope = new StatusReadScope(
        "crabsync-full-observe", 3, "session-third", "machine-test", CampaignRole.Host);
    var absent = await reader.ReadLatestAsync(temp.Path, now.AddSeconds(3), scope: absentScope);
    Require(!absent.HasSnapshot && absent.Snapshot.Sequence == 0,
        "a missing scope fell back to another campaign's status");
}

static async Task StaleCrashDirtyAsync()
{
    using var temp = new TempDirectory();
    var now = DateTimeOffset.UtcNow;
    var json = StatusJson(4, now.AddMinutes(-2), 1, "runtime", crash: true, dirty: true);
    await File.WriteAllTextAsync(Path.Combine(temp.Path, "live_status.slot0.json"), json);
    var result = await new LiveStatusReader().ReadLatestAsync(temp.Path, now, TimeSpan.FromSeconds(8));
    Require(result.IsStale, "stale heartbeat was accepted as fresh");
    Require(result.Cleanliness == EvidenceCleanliness.CrashSuspect, "crash must dominate dirty classification");
    Require(result.Snapshot.DirtyEvidence && result.Snapshot.CrashSuspected, "dirty/crash flags not parsed");
    await File.WriteAllTextAsync(
        Path.Combine(temp.Path, "live_status.slot0.json"),
        StatusJson(5, now.AddSeconds(10), 1, "runtime"));
    var future = await new LiveStatusReader().ReadLatestAsync(temp.Path, now, TimeSpan.FromSeconds(8));
    Require(future.IsStale && future.Cleanliness == EvidenceCleanliness.Dirty,
        "future heartbeat bypassed fail-closed freshness handling");
}

static async Task LiveDashboardReducerAsync()
{
    var reader = new LiveStatusReader();
    var reducer = new LiveDashboardReducer();
    var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    var warmingSnapshot = await reader.ParseFileAsync(Path.Combine(fixtureRoot, "live_status_warming_v1.json"));
    var warmingNow = warmingSnapshot.HeartbeatAtUtc.AddSeconds(2);
    var warming = reducer.Reduce(new LiveStatusReadResult(
        warmingSnapshot, true, false, false, string.Empty, warmingNow, PreviousSequence: 20),
        localGameRunning: true,
        monitoringExpected: true);
    Require(warming.State == LiveCollectionState.Warming
            && warming.HeartbeatAgeText == "2s ago"
            && warming.SequenceText == "21 (+1)"
            && warming.SequenceAdvanced
            && warming.ActiveProfile == "crabsync-full-observe"
            && warming.ReadinessText == "6/10 stable samples",
        "warming live projection lost age, sequence, profile, or stability progress");

    var collectingSnapshot = await reader.ParseFileAsync(Path.Combine(fixtureRoot, "live_status_collecting_v1.json"));
    var collecting = reducer.Reduce(new LiveStatusReadResult(
        collectingSnapshot, true, false, false, string.Empty,
        collectingSnapshot.HeartbeatAtUtc.AddMilliseconds(500), PreviousSequence: 23),
        localGameRunning: true);
    Require(collecting.State == LiveCollectionState.Collecting
            && collecting.SamplingCategory == "slots"
            && collecting.SamplingCategoryText == "Inventory slots"
            && collecting.CollectionReady
            && collecting.HasFreshWriter
            && collecting.SafetyProven,
        "collecting state did not expose category/readiness truth");

    var progressiveSnapshot = collectingSnapshot with
    {
        Runtime = collectingSnapshot.Runtime with { ActiveProfile = "progressive-broad-observation" },
        Safety = collectingSnapshot.Safety with
        {
            HooksDisabled = false,
            ControlledResearchHooks = true,
            CompatibilityValidated = true,
            TrustedDepthEnforced = true,
            ActiveCanaries = 1
        }
    };
    var progressive = reducer.Reduce(new LiveStatusReadResult(
        progressiveSnapshot, true, false, false, string.Empty,
        progressiveSnapshot.HeartbeatAtUtc.AddSeconds(1)), localGameRunning: true);
    Require(progressive.State == LiveCollectionState.Collecting
            && progressive.SafetyProven
            && progressive.Capabilities.ObservableChecklistIds.Count == 0,
        "controlled research hooks were treated as a normal-mode safety failure or Play Guide capability");

    var stableSnapshot = collectingSnapshot with
    {
        Runtime = collectingSnapshot.Runtime with
        {
            CurrentProbeStage = "snapshot:waiting-for-stable-game",
            CurrentSamplingCategory = string.Empty,
            CollectionReady = false
        }
    };
    var stable = reducer.Reduce(new LiveStatusReadResult(
        stableSnapshot, true, false, false, string.Empty, stableSnapshot.HeartbeatAtUtc.AddSeconds(1)), true);
    Require(stable.State == LiveCollectionState.Stable && !stable.CollectionReady,
        "stable state was collapsed into ready");
    var readySnapshot = stableSnapshot with
    {
        Runtime = stableSnapshot.Runtime with { CollectionReady = true }
    };
    var ready = reducer.Reduce(new LiveStatusReadResult(
        readySnapshot, true, false, false, string.Empty, readySnapshot.HeartbeatAtUtc.AddSeconds(1)), true);
    Require(ready.State == LiveCollectionState.Ready && ready.CollectionReady,
        "ready state was not distinguished from stable");

    var staleResult = new LiveStatusReadResult(
        collectingSnapshot, true, true, false, string.Empty, collectingSnapshot.HeartbeatAtUtc.AddSeconds(30), 24);
    var stale = reducer.Reduce(staleResult, localGameRunning: true, monitoringExpected: true);
    Require(staleResult.Cleanliness == EvidenceCleanliness.Dirty
            && stale.State == LiveCollectionState.Stale
            && !stale.HasFreshWriter
            && !stale.CollectionReady,
        "stale-only status remained clean or collection-ready");

    var stoppedSnapshot = collectingSnapshot with
    {
        Runtime = collectingSnapshot.Runtime with { RuntimeProbeState = "stopped", StopRequested = true }
    };
    var stopped = reducer.Reduce(new LiveStatusReadResult(
        stoppedSnapshot, true, true, false, string.Empty, stoppedSnapshot.HeartbeatAtUtc.AddMinutes(1)), false);
    Require(stopped.State == LiveCollectionState.Stopped,
        "explicit clean stop was misreported as a stalled writer");

    var unsafeSnapshot = collectingSnapshot with
    {
        Safety = collectingSnapshot.Safety with { WritesDisabled = false }
    };
    var faulted = reducer.Reduce(new LiveStatusReadResult(
        unsafeSnapshot, true, false, false, string.Empty, unsafeSnapshot.HeartbeatAtUtc.AddSeconds(1)), true);
    Require(faulted.State == LiveCollectionState.Faulted && !faulted.SafetyProven,
        "missing hook-free safety proof did not fault closed");

    var future = reducer.Reduce(new LiveStatusReadResult(
        collectingSnapshot, true, false, false, string.Empty, collectingSnapshot.HeartbeatAtUtc.AddSeconds(-10)), true);
    Require(future.State == LiveCollectionState.Faulted && future.HasClockSkew,
        "future heartbeat timestamp was presented as healthy");

    var unavailable = reducer.Reduce(new LiveStatusReadResult(
        LiveStatusSnapshot.Empty, false, true, false, "missing", warmingNow), localGameRunning: false);
    var writerPending = reducer.Reduce(new LiveStatusReadResult(
        LiveStatusSnapshot.Empty, false, true, false, "missing", warmingNow), localGameRunning: true);
    Require(unavailable.State == LiveCollectionState.GameUnavailable
            && writerPending.State == LiveCollectionState.Warming
            && !unavailable.SafetyProven
            && !LiveStatusSnapshot.Empty.Safety.AllRequiredSafe,
        "missing writer states were presented as ready or safety-proven");
}

static Task SnapshotReplayAsync()
{
    const string session = "snapshot-session";
    const string machine = "machine-test";
    const long generation = 7;
    var scope = new SnapshotReplayScope(
        session,
        generation,
        "crabsync-full-observe",
        CampaignRole.Host,
        "host",
        machine);
    var reducer = new SnapshotEvidenceReducer();

    var health = reducer.ReplayJsonl(
        SnapshotDeltaJsonl("health", "currentHealth", 100m, 75m, session, generation, machine),
        scope);
    Require(health.Rejections.Count == 0 && health.Qualifications.Count == 1,
        "clean stable health delta was not qualified exactly once");
    Require(health.Checklist.TryGetValue("health-damage", out var damage)
            && damage.QualifyingEvidence && damage.ObservationCount == 1
            && health.Checklist.TryGetValue("health-current-change", out var current)
            && current.QualifyingEvidence,
        "health decrease did not project to the mapped player-facing checklist rows");

    var crystals = reducer.ReplayJsonl(
        SnapshotDeltaJsonl("crystals", "crystals", 10m, 25m, session, generation, machine),
        scope);
    Require(crystals.Checklist.TryGetValue("resource-crystal-gain", out var gain)
            && gain.QualifyingEvidence,
        "crystal increase rule did not qualify");

    var slots = reducer.ReplayJsonl(
        SnapshotDeltaJsonl("slots", "weaponModSlots", 8m, 9m, session, generation, machine),
        scope);
    Require(slots.Checklist.TryGetValue("slot-weapon-increment", out var slot)
            && slot.QualifyingEvidence,
        "slot increase rule did not qualify");

    var equipment = reducer.ReplayJsonl(
        SnapshotDeltaJsonl(
            "equipment", "weaponFingerprint", "oldweapon", "newweapon",
            session, generation, machine, fingerprint: true),
        scope);
    Require(equipment.Checklist.TryGetValue("transaction-equipment-change", out var equip)
            && equip.QualifyingEvidence,
        "redacted equipment fingerprint change did not qualify");

    var unsafeJsonl = SnapshotDeltaJsonl(
        "health", "currentHealth", 100m, 50m, session, generation, machine,
        hooksDisabled: false);
    var unsafeReplay = reducer.ReplayJsonl(unsafeJsonl, scope);
    Require(unsafeReplay.Qualifications.Count == 0 && unsafeReplay.Checklist.Count == 0
            && unsafeReplay.Rejections.Any(item => item.Code is "unsafe-row" or "profile-safety-mismatch"),
        "unsafe snapshot rows were not rejected fail-closed");
    var progressiveReplay = reducer.ReplayJsonl(
        unsafeJsonl.Replace("\"worldFingerprint\"",
            "\"observationProfile\":\"progressive-broad-observation\",\"worldFingerprint\"",
            StringComparison.Ordinal), scope);
    Require(progressiveReplay.Qualifications.Count == 0 && progressiveReplay.Checklist.Count == 0
            && progressiveReplay.Rejections.Any(item => item.Code == "unsafe-row"),
        "truthful progressive rows entered the hook-free Play Guide reducer");

    var dirtyReplay = reducer.ReplayJsonl(
        SnapshotDeltaJsonl("health", "currentHealth", 100m, 50m, session, generation, machine,
            dirtyEvidence: true),
        scope);
    Require(dirtyReplay.Qualifications.Count == 0
            && dirtyReplay.Rejections.Any(item => item.Code == "dirty-row"),
        "dirty snapshot evidence was allowed to qualify");

    var wrongMachine = reducer.ReplayJsonl(
        SnapshotDeltaJsonl("health", "currentHealth", 100m, 50m, session, generation, "other-machine"),
        scope);
    Require(wrongMachine.Qualifications.Count == 0
            && wrongMachine.Rejections.Any(item => item.Code == "machine-mismatch"),
        "wrong-machine snapshot evidence was allowed to qualify");

    var malformed = reducer.ReplayJsonl(
        SnapshotDeltaJsonl("health", "currentHealth", 100m, 50m, session, generation, machine)
        + "\n{\"recordType\":\"snapshot-observation\"",
        scope);
    Require(malformed.Qualifications.Count == 0 && malformed.Checklist.Count == 0
            && malformed.Rejections.Any(item => item.Code == "invalid-json"),
        "a malformed tail was bridged around to invent a qualifying delta");

    var insufficientStability = reducer.ReplayJsonl(
        SnapshotDeltaJsonl("health", "currentHealth", 100m, 50m, session, generation, machine)
            .Replace("\"sampleCount\":10", "\"sampleCount\":9", StringComparison.Ordinal),
        scope);
    Require(insufficientStability.Qualifications.Count == 0
            && insufficientStability.Rejections.Any(item => item.Code == "unstable-row"),
        "stable=true bypassed the required 10-sample barrier");

    var missingDwell = reducer.ReplayJsonl(
        SnapshotRow(1, "health", "currentHealth", 100m, session, generation, machine)
            .Replace("\"dwellSeconds\":30,", string.Empty, StringComparison.Ordinal),
        scope);
    Require(missingDwell.Rejections.Any(item => item.Code == "invalid-stability"),
        "missing stability dwellSeconds was accepted despite the strict schema");

    var scopeChanged = string.Join('\n', Enumerable.Range(1, 3)
        .Select(index => SnapshotRow(index, "health", "currentHealth", 100m, session, generation, machine,
            worldFingerprint: "world-a"))
        .Concat(Enumerable.Range(4, 3)
            .Select(index => SnapshotRow(index, "health", "currentHealth", 50m, session, generation, machine,
                worldFingerprint: "world-b"))));
    var scopeReplay = reducer.ReplayJsonl(scopeChanged, scope);
    Require(scopeReplay.Qualifications.Count == 0 && scopeReplay.Rejections.Count == 0,
        "a normal world-scope transition was treated as a gameplay delta or corrupt evidence");

    var readinessScope = scope with { ObservationProfile = ObservationProfileIds.ReadinessCampaign };
    var readinessInventory = reducer.ReplayJsonl(
        SnapshotRow(1, "inventory", "wrapper", 1m, session, generation, machine,
            observationProfile: ObservationProfileIds.ReadinessCampaign), readinessScope);
    Require(readinessInventory.Rejections.Any(item => item.Code == "readiness-category-blocked"),
        "readiness replay accepted a non-reviewed inventory snapshot category");
    return Task.CompletedTask;
}

static async Task SnapshotEvidenceServiceAsync()
{
    using var temp = new TempDirectory();
    const string session = "snapshot-session";
    const string machine = "machine-test";
    const long generation = 7;
    var statusDirectory = Path.Combine(temp.Path, "Scripts", "results");
    Directory.CreateDirectory(statusDirectory);
    var accessPath = Path.Combine(statusDirectory, $"access_evidence_{session}.jsonl");
    var generic = "{\"schemaVersion\":2,\"recordType\":\"coordinator-status\",\"result\":\"ok\"}";
    await File.WriteAllTextAsync(
        accessPath,
        generic + "\n" + SnapshotDeltaJsonl(
            "crystals", "crystals", 10m, 20m, session, generation, machine) + "\n");
    await File.AppendAllTextAsync(
        accessPath,
        SnapshotRow(99, "crystals", "crystals", 99m, session, generation, machine,
            hooksDisabled: false, observationProfile: "progressive-broad-observation") + "\n");
    await File.WriteAllTextAsync(
        Path.Combine(statusDirectory, "snapshot_observations_old-session.jsonl"),
        "{ malformed old session");
    var campaign = new LocalCampaignState(
        1,
        "crabsync-full-observe",
        "Snapshot Test",
        generation,
        session,
        machine,
        CampaignRole.Host,
        temp.Path,
        Path.Combine(temp.Path, "CrabChampions-Win64-Shipping.exe"),
        statusDirectory,
        "monitoring",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        string.Empty);
    var service = new SnapshotEvidenceService();
    var loaded = await service.LoadAsync(campaign);
    Require(loaded.Replay.InputRows == 6 && loaded.Replay.Rejections.Count == 0
            && loaded.SourceFiles.All(path => !path.Contains("old-session", StringComparison.OrdinalIgnoreCase)),
        "generic, foreign-session, or foreign-profile evidence was not ignored by the snapshot reader");

    var statusSnapshot = new LiveStatusReader().Parse(
        StatusJson(20, DateTimeOffset.UtcNow, generation, session));
    var status = new LiveStatusReadResult(
        statusSnapshot,
        true,
        false,
        false,
        string.Empty,
        DateTimeOffset.UtcNow);
    var merged = service.Merge(status, loaded.Replay, SnapshotReplayScope.FromCampaign(campaign));
    Require(merged.Snapshot.Checklist.TryGetValue("resource-crystal-gain", out var evidence)
            && evidence.QualifyingEvidence,
        "clean snapshot evidence was not merged into live checklist status");
    var staleMerge = service.Merge(
        status with { IsStale = true }, loaded.Replay, SnapshotReplayScope.FromCampaign(campaign));
    Require(staleMerge.Cleanliness == EvidenceCleanliness.Dirty
            && !staleMerge.Snapshot.Checklist.ContainsKey("resource-crystal-gain"),
        "stale live status was allowed to qualify snapshot evidence");

    await File.AppendAllTextAsync(accessPath, "{\"recordType\":\"snapshot-observation\"");
    var rejected = await service.LoadAsync(campaign);
    var failClosed = service.Merge(status, rejected.Replay, SnapshotReplayScope.FromCampaign(campaign));
    Require(rejected.Replay.Rejections.Count > 0
            && !failClosed.Snapshot.Checklist.ContainsKey("resource-crystal-gain"),
        "malformed snapshot tail was allowed to overlay checklist evidence");
}

static async Task ScopedTerminalCollectionAsync()
{
    using var temp = new TempDirectory();
    var package = CreatePackage(temp.Path);
    const long generation = 119;
    const string session = "terminal-snapshot-session";
    const string machine = "machine-test";
    var game = Path.Combine(temp.Path, "terminal-game");
    var scripts = Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts");
    var statusDirectory = Path.Combine(scripts, "results");
    Directory.CreateDirectory(statusDirectory);
    var executable = Path.Combine(game, "CrabChampions-Win64-Shipping.exe");
    await File.WriteAllBytesAsync(executable, Array.Empty<byte>());
    await File.WriteAllTextAsync(
        Path.Combine(statusDirectory, "live_status.slot0.json"),
        StatusJson(30, DateTimeOffset.UtcNow.AddMinutes(-2), generation, session)
            .Replace("\"runtimeProbeState\":\"healthy\"",
                "\"runtimeProbeState\":\"stopped\",\"stopRequested\":true",
                StringComparison.Ordinal));
    var activeCanonical = Path.Combine(statusDirectory, $"access_evidence_{session}.jsonl");
    await File.WriteAllTextAsync(
        activeCanonical,
        SnapshotDeltaJsonl("crystals", "crystals", 10m, 20m, session, generation, machine) + "\n");

    // These prior files are intentionally unsafe and one is not JSON. Their identity is
    // outside the active run, so they must be recorded as neutral omissions rather than
    // poisoning clean current snapshot evidence.
    await File.WriteAllTextAsync(
        Path.Combine(statusDirectory, "access_evidence_old-terminal-session.jsonl"),
        "{\"sessionId\":\"old-terminal-session\",\"campaignGeneration\":1,\"hooksEnabled\":true,\"UniqueId\":\"76561198000000000\"}\n");
    await File.WriteAllBytesAsync(
        Path.Combine(statusDirectory, "access_evidence_old-terminal-session.zip"),
        Encoding.UTF8.GetBytes("PK\u0003\u0004not-current-evidence"));
    await File.WriteAllTextAsync(
        Path.Combine(statusDirectory, "session_manifest_old-terminal-session.json"),
        "{\"hooksEnabled\":true,\"UniqueId\":\"76561198000000000\"}\n");
    var priorProfile = Path.Combine(statusDirectory, $"probe_results_{session}.jsonl");
    await File.WriteAllTextAsync(
        priorProfile,
        $"{{\"sessionId\":\"{session}\",\"campaignGeneration\":{generation},\"observationProfile\":\"progressive-broad-observation\",\"hooksEnabled\":true}}\n");

    var state = new LocalCampaignState(
        1,
        "crabsync-full-observe",
        "Terminal scope test",
        generation,
        session,
        machine,
        CampaignRole.Host,
        game,
        executable,
        statusDirectory,
        "monitoring",
        DateTimeOffset.UtcNow.AddMinutes(-3),
        DateTimeOffset.UtcNow,
        string.Empty);

    var clean = await new EvidenceCollector().CollectAsync(
        state,
        Path.Combine(temp.Path, "terminal-exports"),
        resourceStartPath: package);
    var cleanOmissions = await File.ReadAllTextAsync(
        Path.Combine(clean.BundleDirectory, "omissions", "omitted_or_rejected_sources.txt"));
    var cleanDiagnostic = await File.ReadAllTextAsync(clean.SummaryPath);
    Require(!clean.DirtyEvidence && !clean.CrashSuspected,
        $"a clean post-game stale status discarded valid current snapshot evidence: {cleanDiagnostic} omissions={cleanOmissions}");
    var cleanCanonicalDirectory = Path.Combine(clean.BundleDirectory, "evidence", "canonical");
    Require(File.Exists(Path.Combine(cleanCanonicalDirectory, Path.GetFileName(activeCanonical)))
            && !Directory.EnumerateFiles(cleanCanonicalDirectory, "*old-terminal-session*", SearchOption.TopDirectoryOnly).Any()
            && !File.Exists(Path.Combine(cleanCanonicalDirectory, Path.GetFileName(priorProfile))),
        "out-of-scope canonical artifacts were exported with the active session");
    Require(cleanOmissions.Contains("old-terminal-session", StringComparison.OrdinalIgnoreCase)
            && !cleanOmissions.Contains("unsafe write/RPC/hook", StringComparison.OrdinalIgnoreCase),
        "prior unsafe artifacts were not treated as neutral omissions");

    await File.WriteAllTextAsync(
        priorProfile,
        $"{{\"sessionId\":\"{session}\",\"campaignGeneration\":{generation},\"hooksEnabled\":true}}\n");
    var unsafeCurrent = await new EvidenceCollector().CollectAsync(
        state,
        Path.Combine(temp.Path, "unsafe-current-exports"),
        resourceStartPath: package);
    Require(unsafeCurrent.DirtyEvidence,
        "an unsafe current-session canonical row was not marked dirty");

    File.Delete(priorProfile);
    await File.AppendAllTextAsync(activeCanonical, "{\"recordType\":\"snapshot-observation\"");
    var malformedCurrent = await new EvidenceCollector().CollectAsync(
        state,
        Path.Combine(temp.Path, "malformed-current-exports"),
        resourceStartPath: package);
    Require(malformedCurrent.DirtyEvidence,
        "a malformed current-session snapshot row was not marked dirty");
}

static async Task ChecklistAsync()
{
    var repo = FindRepoRoot();
    var resources = new DashboardResourceLocator().Locate(repo);
    var definitions = await new ChecklistDefinitionLoader().LoadAuthoritativeOrFallbackAsync(resources);
    Require(definitions.Count > 50, "authoritative entries array was not loaded");
    Require(definitions.Any(item => item.Id == "session-runtimeprobe-loaded"), "authoritative IDs were not retained");

    var authoritativeReduced = new ChecklistReducer(definitions).Reduce(new LiveStatusReader().Parse(DemoStatus.Json));
    Require(authoritativeReduced.Count == definitions.Count
            && authoritativeReduced.All(item => item.Group != "Discovered / uncatalogued"),
        "embedded demo status invented non-canonical checklist rows");

    var staticReduced = new ChecklistReducer().Reduce(new LiveStatusReader().Parse(DemoStatus.Json));
    Require(staticReduced.Single(item => item.Id == "transaction-server-interact").State == ChecklistDisplayState.InProgress,
        "hook registration was treated as completion");
    Require(staticReduced.Single(item => item.Id == "health-damage").State == ChecklistDisplayState.Confirmed,
        "qualifying natural evidence did not complete row");
    Require(staticReduced.Single(item => item.Id == "inventory.first-da-identity").State == ChecklistDisplayState.BlockedByPrerequisite,
        "prerequisite gate not applied");
    var timestampJson = DemoStatus.Json
        .Replace("\"evidenceSessions\": [\"demo-session\"]",
            "\"evidenceSessionReferences\": [\"demo-session\"], \"firstTimestamp\": \"2026-07-10T17:00:00Z\", \"latestTimestamp\": \"2026-07-10T18:00:00Z\"",
            StringComparison.Ordinal);
    var timestampItem = new ChecklistReducer().Reduce(new LiveStatusReader().Parse(timestampJson))
        .Single(item => item.Id == "health-damage");
    Require(timestampItem.FirstObservedAtUtc is not null && timestampItem.LastObservedAtUtc is not null,
        "first/latest checklist timestamps were not parsed");
    Require(timestampItem.EvidenceSessions.Contains("demo-session", StringComparison.Ordinal),
        "evidenceSessionReferences alias not surfaced");
}

static async Task PlayGuideAsync()
{
    var repo = FindRepoRoot();
    var resources = new DashboardResourceLocator().Locate(repo);
    var definitions = await new ChecklistDefinitionLoader().LoadAuthoritativeOrFallbackAsync(resources);
    var reducer = new PlayGuideReducer();
    Require(definitions.Count == 109, "authoritative checklist count changed without a reviewed Play Guide mapping update");
    Require(reducer.CanonicalChecklistIds.Count == 109, "Play Guide map must contain every canonical checklist ID");
    Require(definitions.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(reducer.CanonicalChecklistIds),
        "Play Guide map omitted or invented canonical checklist IDs");
    Require(reducer.ActionCount == 25 && reducer.CategoryCount == 9,
        "Play Guide must remain a 25-action, nine-category friend-facing projection");

    var unobserved = definitions.Select(definition => ChecklistItem(definition, ChecklistDisplayState.NotObserved)).ToArray();
    var hostGuide = reducer.Reduce(unobserved, CampaignRole.Host);
    Require(hostGuide.Count == 9 && hostGuide.Sum(category => category.Actions.Count) == 25,
        "Play Guide category/action projection changed");
    var powerUps = hostGuide.SelectMany(category => category.Actions).Single(action => action.Id == "power-ups");
    Require(powerUps.Subtasks.Select(item => item.Label).SequenceEqual(new[]
    {
        "Weapon mod", "Ability mod", "Melee mod", "Perk", "Local relic count increased"
    }), "power-up action does not expose the five required friend-facing chips");
    var inventoryWatch = hostGuide.SelectMany(category => category.Actions)
        .Single(action => action.Id == "inventory-watch");
    Require(inventoryWatch.Subtasks.Any(item => item.Label == "Pickup callback observed"),
        "pickup callback outcome is not labeled independently from local relic count");
    var inventoryStageSource = await File.ReadAllTextAsync(
        Path.Combine(repo, "client", "Mods", "CrabRuntimeProbe", "Scripts", "inventory_stage_manager.lua"));
    foreach (var canonicalRuntimeId in new[]
             {
                 "inventory-array-counts", "inventory-first-da-identity", "inventory-info-parent",
                 "inventory-level", "inventory-accumulated-buff", "inventory-enhancements-shape",
                 "inventory-enhancements-values", "inventory-capped-iteration", "inventory-duplicate-semantics",
                 "inventory-order-index-stability", "inventory-joined-client-reads", "inventory-remote-visibility"
             })
        Require(inventoryStageSource.Contains($"'{canonicalRuntimeId}'", StringComparison.Ordinal)
                && reducer.CanonicalChecklistIds.Contains(canonicalRuntimeId, StringComparer.OrdinalIgnoreCase),
            $"runtime inventory stage is not linked to canonical Play Guide ID {canonicalRuntimeId}");
    foreach (var obsoleteRuntimeId in new[]
             {
                 "inventory.wrapper-shape", "inventory.array-counts", "inventory.first-element",
                 "inventory.item-da-identity", "inventory.inventoryinfo-parent", "inventory.enhancements-count",
                 "inventory.capped-full-iteration", "inventory.slot-index-stability"
             })
        Require(!inventoryStageSource.Contains($"'{obsoleteRuntimeId}'", StringComparison.Ordinal),
            $"legacy uncatalogued runtime checklist ID remains: {obsoleteRuntimeId}");
    Require(powerUps.State == PlayGuideDisplayState.ToDo, "unobserved player action should be TO DO");
    Require(hostGuide.SelectMany(category => category.Actions).Single(action => action.Id == "safety").State
            == PlayGuideDisplayState.Waiting,
        "unobserved automatic action should be WAITING");
    var sameLobbyHost = hostGuide.SelectMany(category => category.Actions).Single(action => action.Id == "same-lobby");
    Require(!sameLobbyHost.LinkedChecklistIds.Contains("session-joined-client-detected", StringComparer.OrdinalIgnoreCase)
            && sameLobbyHost.LinkedChecklistIds.Contains("session-host-detected", StringComparer.OrdinalIgnoreCase),
        "host Play Guide was blocked by the opposite computer's role-only signal");
    var joinedGuide = reducer.Reduce(unobserved, CampaignRole.JoinedClient);
    var sameLobbyJoined = joinedGuide.SelectMany(category => category.Actions).Single(action => action.Id == "same-lobby");
    Require(!sameLobbyJoined.LinkedChecklistIds.Contains("session-host-detected", StringComparer.OrdinalIgnoreCase)
            && sameLobbyJoined.LinkedChecklistIds.Contains("session-joined-client-detected", StringComparer.OrdinalIgnoreCase),
        "joined-client Play Guide was blocked by the opposite computer's role-only signal");

    var hookFreeCapabilities = NormalObservationCapabilities.ForProfile("crabsync-full-observe");
    var capabilityGuide = reducer.Reduce(
        unobserved, CampaignRole.Host, EvidenceCleanliness.Clean, hookFreeCapabilities);
    var earnCrystals = capabilityGuide.SelectMany(category => category.Actions)
        .Single(action => action.Id == "earn-crystals");
    var chest = capabilityGuide.SelectMany(category => category.Actions)
        .Single(action => action.Id == "chest");
    var capabilityPowerUps = capabilityGuide.SelectMany(category => category.Actions)
        .Single(action => action.Id == "power-ups");
    var capabilityInventoryWatch = capabilityGuide.SelectMany(category => category.Actions)
        .Single(action => action.Id == "inventory-watch");
    Require(earnCrystals.CanObserve && earnCrystals.HasObservabilityExplanation,
        "partially observable crystal action did not disclose its profile limitation");
    Require(!chest.CanObserve && chest.State == PlayGuideDisplayState.Waiting
            && chest.ObservabilityExplanation.Contains("Not observable", StringComparison.OrdinalIgnoreCase),
        "undetectable chest action was still presented as an ordinary actionable task");
    Require(capabilityGuide.Single(category => category.Id == "shops").NextRecommendedAction
            .Contains("cannot be detected", StringComparison.OrdinalIgnoreCase),
        "category recommendation still suggested an undetectable action");
    Require(capabilityPowerUps.Subtasks.Single(item => item.Label == "Local relic count increased").IsNotObservable
            && capabilityInventoryWatch.Subtasks.Single(item => item.Label == "Pickup callback observed").IsNotObservable,
        "relic count and exact pickup callback did not retain separate not-observable labels");

    var confirmed = definitions.Select(definition => ChecklistItem(definition, ChecklistDisplayState.Confirmed)).ToArray();
    var completedGuide = reducer.Reduce(confirmed, CampaignRole.Host);
    Require(completedGuide.SelectMany(category => category.Actions).All(action => action.State == PlayGuideDisplayState.Done),
        "all clean terminal signals did not complete every action");
    var oneMissing = confirmed.Select(item => item.Id == "inventory-relic-pickup"
        ? item with { State = ChecklistDisplayState.NotObserved }
        : item).ToArray();
    Require(reducer.Reduce(oneMissing, CampaignRole.Host).SelectMany(category => category.Actions)
            .Single(action => action.Id == "power-ups").State == PlayGuideDisplayState.InProgress,
        "one missing signal among completed signals must remain IN PROGRESS");
    var retry = confirmed.Select(item => item.Id == "transaction-anvil"
        ? item with { State = ChecklistDisplayState.DirtyEvidence }
        : item).ToArray();
    Require(reducer.Reduce(retry, CampaignRole.Host).SelectMany(category => category.Actions)
            .Single(action => action.Id == "anvil-use").State == PlayGuideDisplayState.Retry,
        "dirty evidence did not override otherwise completed action signals");
    var globallyDirty = reducer.Reduce(confirmed, CampaignRole.Host, EvidenceCleanliness.Dirty);
    Require(globallyDirty.SelectMany(category => category.Actions)
                .All(action => action.State == PlayGuideDisplayState.Retry
                               && action.Subtasks.All(subtask => subtask.State == PlayGuideDisplayState.Retry))
            && reducer.Reduce(confirmed, CampaignRole.Host, EvidenceCleanliness.CrashSuspect)
                .SelectMany(category => category.Actions).All(action => action.State == PlayGuideDisplayState.Retry),
        "global dirty/crash-suspect evidence allowed a Play Guide action or chip to remain DONE");
    var blocked = unobserved.Select(item => item.Id is "inventory-enhancements-shape" or "inventory-enhancements-values"
        ? item with { State = ChecklistDisplayState.BlockedByPrerequisite }
        : item).ToArray();
    Require(reducer.Reduce(blocked, CampaignRole.Host).SelectMany(category => category.Actions)
            .Single(action => action.Id == "anvil-use").State == PlayGuideDisplayState.Waiting,
        "blocked action did not show WAITING");
    var resolvedAlternatives = confirmed.Select(item => item.Id switch
    {
        "health-shield" => item with { State = ChecklistDisplayState.Unsupported },
        "health-revival" => item with { State = ChecklistDisplayState.NotApplicable },
        _ => item
    }).ToArray();
    Require(reducer.Reduce(resolvedAlternatives, CampaignRole.Host).SelectMany(category => category.Actions)
            .Where(action => action.Id is "defenses" or "death-respawn")
            .All(action => action.State == PlayGuideDisplayState.Done),
        "clean unsupported/not-applicable signals were not terminal in Play Guide");

    var future = ChecklistItem(
        new ChecklistDefinition("future-onrep-playerstate", "Discovered", "OnRep_PlayerState RPC",
            "Inspect a canonical terminal disposition.", true, Array.Empty<string>()),
        ChecklistDisplayState.NotObserved);
    var futureGuide = reducer.Reduce(unobserved.Append(future).ToArray(), CampaignRole.Host);
    var other = futureGuide.SelectMany(category => category.Actions).Single(action => action.Id == "other-automatic");
    Require(other.Subtasks.Any(item => item.Label == "Additional automatic check 1"),
        "future unmapped entry was not surfaced under Other tasks");
    var friendFacing = futureGuide.SelectMany(category => category.Actions)
        .SelectMany(action => new[] { action.Title, action.Instruction }
            .Concat(action.Subtasks.Select(subtask => subtask.Label)))
        .ToArray();
    foreach (var forbidden in new[] { "PlayerState", "OnRep", " RPC", "canonical", "terminal disposition" })
        Require(friendFacing.All(text => !text.Contains(forbidden, StringComparison.OrdinalIgnoreCase)),
            $"technical term leaked into Play Guide: {forbidden}");

    Require(PlayGuideReducer.MatchesFilter(powerUps, PlayGuideFilter.ToDo)
            && PlayGuideReducer.MatchesFilter(powerUps, PlayGuideFilter.All)
            && !PlayGuideReducer.MatchesFilter(powerUps, PlayGuideFilter.Completed),
        "TO DO filter semantics changed");
    var donePowerUps = completedGuide.SelectMany(category => category.Actions).Single(action => action.Id == "power-ups");
    Require(!PlayGuideReducer.MatchesFilter(donePowerUps, PlayGuideFilter.ToDo)
            && PlayGuideReducer.MatchesFilter(donePowerUps, PlayGuideFilter.Completed),
        "Completed filter semantics changed");
}

static async Task CoverageCatalogAsync()
{
    var path = Path.Combine(FindRepoRoot(), "campaign", "crabsync_coverage_catalog.json");
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
    var expected = document.RootElement.GetProperty("rows").GetArrayLength();
    var rows = await new CoverageCatalogReader().ReadAsync(path);
    Require(rows.Count == expected && rows.Count > 1600, "real catalog lost rows");
    Require(rows.All(row => !string.IsNullOrWhiteSpace(row.RowId)), "id alias not parsed");
    Require(rows.Any(row => row.Relevance.Contains("CrabSync", StringComparison.OrdinalIgnoreCase)),
        "relevanceToCrabSync alias not parsed");
    var expectedTerminal = expected - document.RootElement.GetProperty("summary")
        .GetProperty("needsCoverageCount").GetInt32();
    Require(rows.Count(row => !row.NeedsCoverage) == expectedTerminal,
        "catalog terminal dispositions should be reflected exactly by the Needs Coverage view");
    Require(rows.Any(row => row.TerminalDisposition is "unsafe_rejected" or "rejected-unsafe")
            && rows.Any(row => row.TerminalDisposition is "policy_excluded" or "excluded-product-policy"),
        "unsafe and intentional policy exclusions should both be terminal catalog states");
    Require(rows.Any(row => row.ChecklistLinks.Count > 0), "checklistLinkage alias not parsed");
    Require(rows.Any(row => row.CoverageCapabilities.Contains("official-apply-candidates")),
        "coverageCapabilities were not parsed");
    var readiness = new CapabilityReadinessService().Calculate(rows);
    foreach (var verdict in document.RootElement.GetProperty("readinessVerdicts").EnumerateObject())
    {
        var actual = readiness.Single(item => item.Category.Equals(verdict.Name, StringComparison.OrdinalIgnoreCase));
        Require(actual.TotalRows == verdict.Value.GetProperty("rowCount").GetInt32(),
            $"readiness denominator diverged for {verdict.Name}");
        Require(actual.NeedsCoverageRows == verdict.Value.GetProperty("unresolvedCount").GetInt32(),
            $"readiness unresolved count diverged for {verdict.Name}");
    }
}

static async Task AtomicFileAsync()
{
    using var temp = new TempDirectory();
    var path = Path.Combine(temp.Path, "nested", "state.json");
    await AtomicFile.WriteTextAsync(path, "one");
    await AtomicFile.WriteTextAsync(path, "two");
    Require(await File.ReadAllTextAsync(path) == "two", "atomic replacement content mismatch");
    Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "temporary file leaked");
}

static async Task PrepareAndResumeAsync()
{
    using var temp = new TempDirectory();
    var package = CreatePackage(temp.Path);
    var game = Path.Combine(temp.Path, "game");
    Directory.CreateDirectory(Path.Combine(game, "Mods"));
    var executable = Path.Combine(game, "CrabChampions.exe");
    await File.WriteAllBytesAsync(executable, Array.Empty<byte>());
    await File.WriteAllTextAsync(Path.Combine(game, "Mods", "mods.txt"),
        "; keep this comment\r\nUnrelatedMod : 0\r\nCrabRuntimeProbe : 0\r\n");
    var status = Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "results");
    Directory.CreateDirectory(status);
    await File.WriteAllTextAsync(Path.Combine(status, "live_status.slot0.json"), StatusJson(1, DateTimeOffset.UtcNow, 1, "old"));
    await File.WriteAllTextAsync(Path.Combine(status, "dashboard_stop_requested.json"), "{}");
    var canonical = Path.Combine(status, "access_evidence_old.jsonl");
    await File.WriteAllTextAsync(canonical, "{\"sessionId\":\"old\"}\n");
    var dashboardExecutable = Path.Combine(temp.Path, "CrabRuntimeProbe.Dashboard.exe");
    await File.WriteAllBytesAsync(dashboardExecutable, Array.Empty<byte>());

    var store = new DashboardStateStore(Path.Combine(temp.Path, "state"));
    var service = new CampaignService(store);
    var state = await service.PrepareAsync(
        new GameInstallation(game, executable, "test"), CampaignRole.JoinedClient, "Test Campaign", package,
        dashboardExecutable);
    var config = await File.ReadAllTextAsync(Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "config.txt"));
    foreach (var required in new[]
             {
                 "enabled = true", "tickDriver = executeDelay", "mode = observe",
                 "probeSet = crabsync-full-observe", "allowWriteProbes = false", "allowRpcProbes = false",
                 "allowHudTickHook = false", "allowRawIdentityEvidence = false",
                 "fullObserveEnabled = true", "snapshotSamplerEnabled = true",
                 "snapshotStableSamplesRequired = 10", "snapshotStableDwellSeconds = 30",
                 "allowPassiveObservationHooks = false", "allowFullObserveInventoryStages = false",
                 "allowFullObserveRuntimeDiscovery = false", "allowDeepArrayProbes = false",
                 "allowInventoryInfoProbes = false", "allowHealthProbes = false",
                 "allowIdentityProbes = false", "allowResourceVisibilityProbes = false",
                 "allowCrystalsReadProbes = false", "allowSlotsReadProbes = false",
                 "allowSafeScalarWatchProbes = false", "allowPerkDataAssetCatalogProbes = false",
                 "allowMaxSafePlayRecorderProbes = false", "allowInventoryArrayShallowProbes = false",
                 "allowInventoryArrayShapeConfirmProbes = false",
                 "allowInventoryUserdataIntrospectionProbes = false",
                 "allowInventoryArrayCountProbes = false",
                 "allowInventoryElementDataAssetReadProbes = false",
                 "progressiveObservationEnabled = false", "canaryCandidateId = unassigned",
                 "canaryValidationDepth = 0", "trustedCandidateSelections = "
             })
        Require(config.Contains(required, StringComparison.Ordinal), $"safe config missing {required}");
    Require(config.Contains($"campaignSessionId = {state.SessionId}", StringComparison.Ordinal),
        "runtime config session does not match dashboard campaign session");
    var mods = await File.ReadAllTextAsync(Path.Combine(game, "Mods", "mods.txt"));
    Require(mods.Contains("; keep this comment") && mods.Contains("UnrelatedMod : 0"), "mods.txt merge lost user lines");
    foreach (var name in new[] { "BPModLoaderMod : 1", "BPML_GenericFunctions : 1", "CrabRuntimeProbe : 1" })
        Require(mods.Contains(name, StringComparison.OrdinalIgnoreCase), $"required mod not enabled: {name}");
    Require(File.Exists(canonical), "prepare deleted canonical append-only evidence");

    var research = await new ResearchPreparationService().PlanAsync(
        state, ResearchRunType.Combined, resourceStartPath: package);
    Require(research.Plan.IsValid && research.Plan.Manifest?.TrustedCandidates.Count == 0
            && research.Plan.Manifest.Canary?.CandidateId == "hook-crabps-onrep-islandrewardrarity",
        "initial progressive research plan was not zero-trusted plus the principal canary");
    var runManifest = await service.ArmProgressiveObservationAsync(state, research.Plan);
    Require(File.Exists(runManifest), "run identity was not persisted before arming");
    var researchConfig = await File.ReadAllTextAsync(
        Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "config.txt"));
    foreach (var required in new[]
             {
                 "progressiveObservationEnabled = true", "researchRunType = combined",
                 "trustedCandidateSelections = ",
                 "canaryCandidateId = hook-crabps-onrep-islandrewardrarity", "canaryValidationDepth = 1",
                 "canaryState = armed", "allowPassiveObservationHooks = false",
                 "allowWriteProbes = false", "allowRpcProbes = false"
             })
        Require(researchConfig.Contains(required, StringComparison.Ordinal), $"research config missing {required}");
    await service.DisarmProgressiveObservationAsync(state);
    var disarmedConfig = await File.ReadAllTextAsync(
        Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "config.txt"));
    Require(disarmedConfig.Contains("progressiveObservationEnabled = false", StringComparison.Ordinal)
            && disarmedConfig.Contains("researchRunId = unassigned", StringComparison.Ordinal)
            && disarmedConfig.Contains("canaryCandidateId = unassigned", StringComparison.Ordinal)
            && disarmedConfig.Contains("canaryValidationDepth = 0", StringComparison.Ordinal),
        "completed research authorization was not restored to a hook-free next-launch config");
    var autostartPath = await File.ReadAllTextAsync(
        Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "dashboard_autostart.txt"));
    Require(autostartPath.Trim() == Path.GetFullPath(dashboardExecutable),
        "prepare did not configure game-triggered dashboard autostart");
    Require(Directory.EnumerateFiles(Path.Combine(status, "status-archive"), "*", SearchOption.AllDirectories).Any(),
        "prior live status/control markers were not archived");
    var request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(status, "dashboard_campaign_request.json")));
    Require(request.RootElement.GetProperty("command").GetString() == "prepare", "prepare marker command mismatch");
    var installedConfig = Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "config.txt");
    await File.WriteAllTextAsync(
        installedConfig,
        (await File.ReadAllTextAsync(installedConfig))
        .Replace("snapshotSamplerEnabled = true", "snapshotSamplerEnabled = false", StringComparison.Ordinal)
        .Replace("allowPassiveObservationHooks = false", "allowPassiveObservationHooks = true", StringComparison.Ordinal));
    var resumed = await service.ResumeAsync();
    Require(resumed?.Phase == "monitoring", "resume did not restore campaign");
    var resumedConfig = await File.ReadAllTextAsync(installedConfig);
    Require(resumedConfig.Contains("snapshotSamplerEnabled = true", StringComparison.Ordinal)
            && resumedConfig.Contains("allowPassiveObservationHooks = false", StringComparison.Ordinal),
        "resume did not reinstall and reassert the hook-free snapshot profile");
    Require((await File.ReadAllTextAsync(
            Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "dashboard_autostart.txt"))).Trim()
            == Path.GetFullPath(dashboardExecutable),
        "resume payload refresh lost dashboard autostart");
    request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(status, "dashboard_campaign_request.json")));
    Require(request.RootElement.GetProperty("command").GetString() == "resume", "resume marker command mismatch");
}

static async Task ReadinessPrepareAsync()
{
    using var temp = new TempDirectory();
    var package = CreatePackage(temp.Path);
    var game = Path.Combine(temp.Path, "readiness-game");
    Directory.CreateDirectory(Path.Combine(game, "Mods"));
    var executable = Path.Combine(game, "CrabChampions.exe");
    await File.WriteAllBytesAsync(executable, Array.Empty<byte>());
    var service = new CampaignService(new DashboardStateStore(Path.Combine(temp.Path, "readiness-state")));
    const string code = "ABCD2345";
    var state = await service.PrepareReadinessCampaignAsync(
        new GameInstallation(game, executable, "test"),
        CampaignRole.Host,
        correlationCode: code,
        resourceStartPath: package);
    var pairing = state.ReadinessPairing
                  ?? throw new InvalidOperationException("readiness campaign lost local pairing metadata");
    Require(state.ProfileId == ReadinessCampaignContracts.ProfileId
            && pairing.CorrelationCode == code
            && pairing.InventoryStage == ReadinessCampaignContracts.DeferredInventoryStage,
        "readiness state did not retain the local-only pairing contract");

    var scripts = Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts");
    var config = await File.ReadAllTextAsync(Path.Combine(scripts, "config.txt"));
    Require(config.Contains("campaignProfile = crabsync-readiness-campaign", StringComparison.Ordinal)
            && config.Contains("readinessCampaignEnabled = true", StringComparison.Ordinal)
            && config.Contains("readinessInventoryStage = disabled", StringComparison.Ordinal)
            && config.Contains($"readinessPairId = {pairing.PairId}", StringComparison.Ordinal)
            && !config.Contains(code, StringComparison.Ordinal)
            && !config.Contains("readinessInventoryIntervalSeconds", StringComparison.Ordinal),
        "readiness config exposed a code or enabled inventory collection");

    var status = Path.Combine(scripts, "results");
    var manifestJson = await File.ReadAllTextAsync(Path.Combine(status, "readiness_campaign_manifest.json"));
    var requestJson = await File.ReadAllTextAsync(Path.Combine(status, "dashboard_campaign_request.json"));
    Require(!manifestJson.Contains(code, StringComparison.Ordinal)
            && !requestJson.Contains(code, StringComparison.Ordinal),
        "readiness result artifacts persisted the human correlation code");
    using var manifest = JsonDocument.Parse(manifestJson);
    Require(manifest.RootElement.GetProperty("inventoryStage").GetString() == "disabled"
            && manifest.RootElement.GetProperty("pairId").GetString() == pairing.PairId
            && manifest.RootElement.GetProperty("enabledChannels").GetArrayLength() == 5,
        "readiness manifest did not retain the bounded deferred inventory contract");

    await service.ResumeAsync();
    var resumedConfig = await File.ReadAllTextAsync(Path.Combine(scripts, "config.txt"));
    Require(resumedConfig.Contains("readinessInventoryStage = disabled", StringComparison.Ordinal)
            && !resumedConfig.Contains(code, StringComparison.Ordinal),
        "readiness resume did not restore the code-private deferred inventory profile");
    await ThrowsAsync<ArgumentException>(() => service.PrepareReadinessCampaignAsync(
        new GameInstallation(game, executable, "test"), CampaignRole.JoinedClient, resourceStartPath: package));
}

static async Task ReadinessEvidenceAndBundleAsync()
{
    using var temp = new TempDirectory();
    const long generation = 17;
    const string session = "session-readiness";
    const string machine = "machine-readiness";
    const string manifestId = "readiness-manifest-12345678";
    var pairId = ReadinessCampaignContracts.DerivePairId("ABCD2345");
    var scope = new ReadinessEvidenceScope(
        ReadinessCampaignContracts.CampaignId, generation, session, machine, CampaignRole.Host, pairId);
    var scripts = Path.Combine(temp.Path, "game", "Mods", "CrabRuntimeProbe", "Scripts");
    var status = Path.Combine(scripts, "results");
    Directory.CreateDirectory(status);
    var evidencePath = Path.Combine(status, $"access_evidence_{session}.jsonl");
    await File.WriteAllTextAsync(evidencePath, string.Join('\n', new[]
    {
        ReadinessPeerJson(generation, session, machine, "host", pairId, 1),
        ReadinessTerminalJson(generation, session, machine, "host", pairId, 2)
    }) + "\n");

    var reader = new ReadinessEvidenceReader();
    var evidence = await reader.ReadAsync(status, scope);
    Require(evidence.PeerSnapshots.Count == 1 && evidence.TerminalLifecycles.Count == 1 && evidence.Rejections.Count == 0,
        "closed readiness rows were not accepted as a complete active scope");
    var report = ReadinessReportReducer.Reduce(scope, evidence);
    Require(report.Gates.Single(gate => gate.Id == "local-safe-scalars").Disposition == ReadinessGateDisposition.Confirmed
            && report.Gates.Single(gate => gate.Id == "peer-visible-playerstate").Disposition == ReadinessGateDisposition.Blocked
            && report.Gates.Single(gate => gate.Id == "inventory-item-proof").Disposition == ReadinessGateDisposition.Blocked,
        "readiness report overclaimed remote or inventory evidence");

    await File.AppendAllTextAsync(evidencePath, "{truncated\n");
    var malformed = await reader.ReadAsync(status, scope);
    Require(malformed.Rejections.Any(rejection => rejection.Code == "invalid-json"),
        "a malformed active readiness row was treated as neutral evidence");
    await File.WriteAllTextAsync(evidencePath, string.Join('\n', new[]
    {
        ReadinessPeerJson(generation, session, machine, "host", pairId, 1),
        ReadinessTerminalJson(generation, session, machine, "host", pairId, 2)
    }) + "\n");

    var pairing = new ReadinessCampaignLocalPairing(
        "ABCD2345", pairId, manifestId, ReadinessCampaignContracts.DeferredInventoryStage,
        ReadinessCampaignContracts.DefaultChannels(), DateTimeOffset.UtcNow.AddMinutes(-1));
    var game = Path.Combine(temp.Path, "game");
    var executable = Path.Combine(game, "CrabChampions.exe");
    await File.WriteAllBytesAsync(executable, Array.Empty<byte>());
    var state = new LocalCampaignState(
        2, ReadinessCampaignContracts.CampaignId, ReadinessCampaignContracts.DefaultCampaignName,
        generation, session, machine, CampaignRole.Host, game, executable, status, "monitoring",
        pairing.CreatedAtUtc, DateTimeOffset.UtcNow, string.Empty, ReadinessCampaignContracts.ProfileId, pairing);
    await File.WriteAllTextAsync(Path.Combine(status, "live_status.slot0.json"),
        ReadinessStatusJson(generation, session, machine, pairId, manifestId));
    await File.WriteAllTextAsync(Path.Combine(status, "readiness_campaign_manifest.json"),
        ReadinessManifestJson(generation, session, machine, "host", pairId, manifestId, pairing.CreatedAtUtc));
    var package = CreatePackage(temp.Path);
    var collection = await new EvidenceCollector().CollectAsync(
        state, Path.Combine(temp.Path, "exports"), resourceStartPath: package);
    Require(!collection.DirtyEvidence
            && File.Exists(Path.Combine(collection.BundleDirectory, "readiness_report.json"))
            && File.Exists(Path.Combine(collection.BundleDirectory, "readiness_report.md"))
            && Directory.EnumerateFiles(collection.BundleDirectory, "readiness_campaign_manifest.json", SearchOption.AllDirectories).Count() == 1,
        "a clean readiness collection did not preserve its report and pairing manifest");

    var prepared = DateTimeOffset.UtcNow.AddMinutes(-3);
    var collected = DateTimeOffset.UtcNow;
    var host = await CreateReadinessBundleAsync(temp.Path, "host", "host", "machine-host", "session-host", prepared, collected, pairId);
    var joined = await CreateReadinessBundleAsync(temp.Path, "joined", "joined-client", "machine-client", "session-client",
        prepared.AddSeconds(5), collected.AddSeconds(5), pairId);
    var combined = await new BundleCorrelationService().CombineAsync(new[] { host, joined }, Path.Combine(temp.Path, "combined"));
    Require(combined.CorrelationEstablished, "matching readiness pair manifests did not correlate");
    var otherPair = ReadinessCampaignContracts.DerivePairId("WXYZ6789");
    var mismatch = await CreateReadinessBundleAsync(temp.Path, "mismatch", "joined-client", "machine-other", "session-other",
        prepared.AddSeconds(10), collected.AddSeconds(10), otherPair);
    var mismatched = await new BundleCorrelationService().CombineAsync(new[] { host, mismatch }, Path.Combine(temp.Path, "mismatch-output"));
    Require(!mismatched.CorrelationEstablished, "different derived readiness pair IDs were correlated");
}

static Task GameProcessExitDetectorAsync()
{
    var started = DateTimeOffset.Parse("2026-07-10T21:06:19Z");
    var detector = new GameProcessExitDetector(TimeSpan.FromSeconds(3), requiredConsecutiveMisses: 3);
    detector.Begin(started, processSeen: true);

    Require(!detector.Observe(false, started.AddSeconds(1)), "launcher handoff failed inside startup grace");
    Require(!detector.Observe(false, started.AddSeconds(3)), "one missed process poll was treated as exit");
    Require(!detector.Observe(true, started.AddSeconds(4)), "running shipping process was treated as exit");
    Require(!detector.Observe(false, started.AddSeconds(5)), "first confirmed miss was treated as exit");
    Require(!detector.Observe(false, started.AddSeconds(6)), "second confirmed miss was treated as exit");
    Require(detector.Observe(false, started.AddSeconds(7)), "consecutive process misses did not confirm exit");

    detector.Reset();
    Require(!detector.Observe(false, started.AddMinutes(1)), "an unseen process was treated as having exited");
    return Task.CompletedTask;
}

static Task RedactionAsync()
{
    var redactor = new EvidenceRedactor();
    var raw = "{\"UniqueId\":\"76561198000000000\",\"PlayerName\":\"Dylan\",\"path\":\"C:\\\\Users\\\\dudie\\\\x\"}";
    var redacted = redactor.Redact(raw);
    Require(!redacted.Contains("76561198000000000") && !redacted.Contains("Dylan") && !redacted.Contains("dudie"),
        "private identity survived redaction");
    Require(redactor.ContainsPrivateIdentity(raw) && !redactor.ContainsPrivateIdentity(redacted),
        "identity validator disagrees with redaction");
    return Task.CompletedTask;
}

static async Task CollectionAsync()
{
    using var temp = new TempDirectory();
    var package = CreatePackage(temp.Path);
    var game = Path.Combine(temp.Path, "game");
    var scripts = Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts");
    var status = Path.Combine(scripts, "results");
    Directory.CreateDirectory(status);
    var generation = 77L;
    var runtimeSession = "runtime-session";
    await File.WriteAllTextAsync(Path.Combine(status, "live_status.slot0.json"),
        StatusJson(9, DateTimeOffset.UtcNow, generation, runtimeSession));
    var bytes = Encoding.UTF8.GetBytes(
        "{\"schemaVersion\":1,\"campaignGeneration\":77,\"sessionId\":\"runtime-session\",\"event\":\"natural-call\"}\r\n");
    var canonical = Path.Combine(status, $"access_evidence_{runtimeSession}.jsonl");
    await File.WriteAllBytesAsync(canonical, bytes);
    await File.WriteAllTextAsync(Path.Combine(status, $"probe_results_{runtimeSession}.jsonl"),
        "{\"campaignGeneration\":77,\"sessionId\":\"runtime-session\",\"UniqueId\":\"76561198000000000\"}\n");
    await File.WriteAllTextAsync(Path.Combine(status, "access_evidence_old.jsonl"),
        "{\"campaignGeneration\":1,\"sessionId\":\"old\"}\n");
    await File.WriteAllTextAsync(Path.Combine(scripts, "CrabRuntimeProbe.log"), "PlayerName=visible 76561198000000000");
    var executable = Path.Combine(game, "CrabChampions.exe");
    await File.WriteAllBytesAsync(executable, Array.Empty<byte>());
    var state = new LocalCampaignState(
        1, "crabsync-full-observe", "Test Campaign", generation, runtimeSession, "machine-test",
        CampaignRole.Host, game, executable, status, "monitoring", DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow, string.Empty);
    var result = await new EvidenceCollector().CollectAsync(
        state, Path.Combine(temp.Path, "exports"), resourceStartPath: package);
    var copied = Directory.EnumerateFiles(Path.Combine(result.BundleDirectory, "evidence", "canonical"),
        "access_evidence_runtime-session.jsonl").Single();
    Require(bytes.SequenceEqual(await File.ReadAllBytesAsync(copied)), "canonical evidence bytes changed");
    Require(!Directory.EnumerateFiles(Path.Combine(result.BundleDirectory, "evidence", "canonical"), "probe_results*").Any(),
        "unsafe canonical evidence was copied");
    var omission = await File.ReadAllTextAsync(Path.Combine(result.BundleDirectory, "omissions", "omitted_or_rejected_sources.txt"));
    Require(omission.Contains("raw identity", StringComparison.OrdinalIgnoreCase)
            && omission.Contains("prior campaign generation", StringComparison.OrdinalIgnoreCase),
        "unsafe/unrelated omissions were not explicit");
    Require(result.DirtyEvidence, "unsafe omission must mark bundle dirty");
    var manifest = JsonSerializer.Deserialize<BundleManifest>(
        await File.ReadAllTextAsync(Path.Combine(result.BundleDirectory, "bundle_manifest.json")), JsonOptions())!;
    Require(manifest.ManifestSelfExcluded && manifest.Files.Count > 0, "manifest inventory missing");
    Require(manifest.Files.All(entry => !Path.IsPathRooted(entry.Path) && !entry.Path.Contains("..")),
        "manifest leaked absolute/escaping paths");
    foreach (var entry in manifest.Files)
    {
        var path = Path.Combine(result.BundleDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        Require(new FileInfo(path).Length == entry.SizeBytes, $"manifest size mismatch {entry.Path}");
        Require(Hash(path) == entry.Hash, $"manifest hash mismatch {entry.Path}");
    }
    using var manifestDocument = JsonDocument.Parse(
        await File.ReadAllTextAsync(Path.Combine(result.BundleDirectory, "bundle_manifest.json")));
    var manifestRoot = manifestDocument.RootElement;
    foreach (var name in new[]
             {
                 "schemaVersion", "bundleFormat", "campaignId", "campaignName", "profileId",
                 "campaignGeneration", "machineId", "sessionId", "selectedRole", "preparedAtUtc",
                 "collectedAtUtc", "crashSuspected", "dirtyEvidence", "safety", "evidenceFileCount",
                 "catalogSchemaVersion", "catalogHash", "manifestSelfExcluded", "files"
             })
        Require(manifestRoot.TryGetProperty(name, out _), $"manifest contract missing {name}");
    Require(!manifestRoot.TryGetProperty("role", out _) && !manifestRoot.TryGetProperty("createdAtUtc", out _),
        "legacy manifest property leaked");
    var safety = manifestRoot.GetProperty("safety");
    foreach (var name in new[]
             {
                 "writesDisabled", "rpcCallsDisabled", "mutationDisabled", "rawIdentityDisabled", "hudHookDisabled",
                 "hooksDisabled", "runtimeDiscoveryDisabled", "inventoryStagesDisabled"
             })
        Require(safety.GetProperty(name).ValueKind == JsonValueKind.True, $"safety contract mismatch {name}");
    foreach (var name in new[] { "controlledResearchHooks", "compatibilityValidated", "trustedDepthEnforced" })
        Require(safety.GetProperty(name).ValueKind == JsonValueKind.False,
            $"normal bundle falsely claimed research safety field {name}");
    Require(safety.GetProperty("activeCanaries").GetInt32() == 0,
        "normal bundle falsely claimed an active canary");
    var fileEntry = manifestRoot.GetProperty("files").EnumerateArray().First();
    Require(fileEntry.GetProperty("path").ValueKind == JsonValueKind.String
            && fileEntry.GetProperty("sizeBytes").ValueKind == JsonValueKind.Number
            && fileEntry.GetProperty("hash").ValueKind == JsonValueKind.String
            && fileEntry.GetProperty("sourceHash").ValueKind == JsonValueKind.String
            && fileEntry.GetProperty("kind").ValueKind == JsonValueKind.String,
        "bundle file contract names/types mismatch");
    using var archive = ZipFile.OpenRead(result.ZipPath);
    Require(archive.Entries.Any(entry => entry.FullName == "bundle_manifest.json"), "ZIP missing manifest");
}

static async Task SnapshotCollectionSafetyAsync()
{
    using var temp = new TempDirectory();
    var package = CreatePackage(temp.Path);
    const long generation = 88;
    const string session = "snapshot-export-session";
    const string machine = "machine-test";
    var game = Path.Combine(temp.Path, "safe-game");
    var scripts = Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts");
    var statusDirectory = Path.Combine(scripts, "results");
    Directory.CreateDirectory(statusDirectory);
    var executable = Path.Combine(game, "CrabChampions.exe");
    await File.WriteAllBytesAsync(executable, Array.Empty<byte>());
    await File.WriteAllTextAsync(
        Path.Combine(statusDirectory, "live_status.slot0.json"),
        StatusJson(30, DateTimeOffset.UtcNow, generation, session));
    await File.WriteAllTextAsync(
        Path.Combine(statusDirectory, $"access_evidence_{session}.jsonl"),
        "{\"schemaVersion\":2,\"recordType\":\"coordinator-status\",\"campaignGeneration\":88,\"sessionId\":\"snapshot-export-session\"}\n"
        + SnapshotDeltaJsonl("crystals", "crystals", 10m, 20m, session, generation, machine)
        + "\n");
    var state = new LocalCampaignState(
        1,
        "crabsync-full-observe",
        "Snapshot Export",
        generation,
        session,
        machine,
        CampaignRole.Host,
        game,
        executable,
        statusDirectory,
        "monitoring",
        DateTimeOffset.UtcNow.AddMinutes(-2),
        DateTimeOffset.UtcNow,
        string.Empty);
    var safeResult = await new EvidenceCollector().CollectAsync(
        state,
        Path.Combine(temp.Path, "safe-exports"),
        resourceStartPath: package);
    var checklist = await File.ReadAllTextAsync(Path.Combine(safeResult.BundleDirectory, "checklist_report.md"));
    Require(checklist.Contains("[x] Crystal gain observed - `Confirmed`", StringComparison.Ordinal),
        "export did not replay GUI snapshot evidence into its checklist report");
    var safeManifest = JsonSerializer.Deserialize<BundleManifest>(
        await File.ReadAllTextAsync(Path.Combine(safeResult.BundleDirectory, "bundle_manifest.json")),
        JsonOptions())!;
    Require(safeManifest.Safety.AllDisabled, "clean status did not produce a hook-free bundle safety claim");

    const string unsafeSession = "legacy-hook-session";
    var unsafeGame = Path.Combine(temp.Path, "unsafe-game");
    var unsafeStatusDirectory = Path.Combine(unsafeGame, "Mods", "CrabRuntimeProbe", "Scripts", "results");
    Directory.CreateDirectory(unsafeStatusDirectory);
    var unsafeExecutable = Path.Combine(unsafeGame, "CrabChampions.exe");
    await File.WriteAllBytesAsync(unsafeExecutable, Array.Empty<byte>());
    var legacyStatus = StatusJson(2, DateTimeOffset.UtcNow, generation, unsafeSession)
        .Replace("\"hooksDisabled\":true,", string.Empty, StringComparison.Ordinal);
    await File.WriteAllTextAsync(Path.Combine(unsafeStatusDirectory, "live_status.slot0.json"), legacyStatus);
    var unsafeState = state with
    {
        SessionId = unsafeSession,
        GameDirectory = unsafeGame,
        ExecutablePath = unsafeExecutable,
        StatusDirectory = unsafeStatusDirectory
    };
    var unsafeResult = await new EvidenceCollector().CollectAsync(
        unsafeState,
        Path.Combine(temp.Path, "unsafe-exports"),
        resourceStartPath: package);
    var unsafeManifest = JsonSerializer.Deserialize<BundleManifest>(
        await File.ReadAllTextAsync(Path.Combine(unsafeResult.BundleDirectory, "bundle_manifest.json")),
        JsonOptions())!;
    Require(!unsafeManifest.Safety.HooksDisabled && !unsafeManifest.Safety.AllDisabled
            && unsafeManifest.DirtyEvidence && unsafeResult.DirtyEvidence,
        "legacy status without hooksDisabled was falsely exported as hook-free");
    var diagnostics = await File.ReadAllTextAsync(Path.Combine(unsafeResult.BundleDirectory, "diagnostic_summary.txt"));
    Require(diagnostics.Contains("hooksDisabled=false", StringComparison.Ordinal),
        "diagnostic summary hardcoded a hook-free claim for legacy status");

    const string researchSession = "controlled-research-session";
    var researchGame = Path.Combine(temp.Path, "research-game");
    var researchStatusDirectory = Path.Combine(researchGame, "Mods", "CrabRuntimeProbe", "Scripts", "results");
    Directory.CreateDirectory(researchStatusDirectory);
    var researchExecutable = Path.Combine(researchGame, "CrabChampions.exe");
    var ue4ss = Path.Combine(researchGame, "UE4SS.dll");
    await File.WriteAllBytesAsync(researchExecutable, Encoding.UTF8.GetBytes("test-game-build"));
    await File.WriteAllBytesAsync(ue4ss, Encoding.UTF8.GetBytes("test-ue4ss-build"));
    var researchState = state with
    {
        SessionId = researchSession,
        GameDirectory = researchGame,
        ExecutablePath = researchExecutable,
        StatusDirectory = researchStatusDirectory,
        PreparedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2)
    };
    var artifactStore = new ResearchArtifactStore();
    var researchCatalog = await artifactStore.ReadCatalogAsync(
        Path.Combine(package, "campaign", "hook_candidate_catalog.json"));
    var compatibility = await new CompatibilityFingerprintService().FromInstallationAsync(
        researchExecutable, ue4ss, researchCatalog);
    var canaryCandidate = researchCatalog.ById[researchCatalog.PrincipalCandidateId];
    var runId = "research-test-run";
    var runManifest = new HookRunManifest(
        ResearchContracts.RunManifestSchema,
        runId,
        researchSession,
        generation,
        DateTimeOffset.UtcNow,
        ResearchRunType.CanaryOnly,
        CampaignRole.Host,
        compatibility,
        true,
        Array.Empty<HookCandidateSelection>(),
        new HookCandidateSelection(
            canaryCandidate.Id,
            canaryCandidate.HookPathFingerprint,
            HookValidationDepth.RegistrationOnly),
        new[] { "safe-snapshot-baseline", canaryCandidate.Id },
        false);
    await artifactStore.WriteRunManifestAsync(
        Path.Combine(researchStatusDirectory, $"hook_run_manifest_{runId}.json"), runManifest);
    await File.WriteAllTextAsync(
        Path.Combine(researchStatusDirectory, $"hook_run_consumed_{runId}.json"),
        JsonSerializer.Serialize(new
        {
            schemaVersion = ResearchContracts.RunConsumedSchema,
            runId,
            consumedAtUtc = DateTimeOffset.UtcNow,
            automaticRearmAllowed = false
        }, JsonOptions()));
    var researchStatus = StatusJson(3, DateTimeOffset.UtcNow, generation, researchSession)
        .Replace("\"currentProbeStage\":\"observe\"",
            "\"currentProbeStage\":\"observe\",\"activeProfile\":\"progressive-broad-observation\",\"profileId\":\"progressive-broad-observation\"",
            StringComparison.Ordinal)
        .Replace("\"hooksDisabled\":true", "\"hooksDisabled\":false", StringComparison.Ordinal);
    await File.WriteAllTextAsync(
        Path.Combine(researchStatusDirectory, "live_status.slot0.json"), researchStatus);
    await File.WriteAllTextAsync(
        Path.Combine(researchStatusDirectory, $"access_evidence_{researchSession}.jsonl"),
        SnapshotRow(1, "crystals", "crystals", 10m, researchSession, generation, machine,
            hooksDisabled: false) + "\n");
    var researchResult = await new EvidenceCollector().CollectAsync(
        researchState,
        Path.Combine(temp.Path, "research-exports"),
        resourceStartPath: package);
    var researchBundle = JsonSerializer.Deserialize<BundleManifest>(
        await File.ReadAllTextAsync(Path.Combine(researchResult.BundleDirectory, "bundle_manifest.json")),
        JsonOptions())!;
    Require(researchBundle.ProfileId == "progressive-broad-observation"
            && researchBundle.Safety.ControlledResearchHooks
            && researchBundle.Safety.CompatibilityValidated
            && researchBundle.Safety.TrustedDepthEnforced
            && researchBundle.Safety.ActiveCanaries == 1
            && !researchBundle.Safety.HooksDisabled
            && !researchBundle.DirtyEvidence,
        "strict one-canary research bundle was not distinguished from unsafe or normal-mode evidence");
    Require(Directory.EnumerateFiles(
            Path.Combine(researchResult.BundleDirectory, "evidence", "research-redacted"),
            "hook_run_manifest_*.json").Any()
            && Directory.EnumerateFiles(
                Path.Combine(researchResult.BundleDirectory, "evidence", "research-redacted"),
                "hook_run_consumed_*.json").Any(),
        "controlled research bundle omitted its redacted manifest or consumption marker");
    File.Delete(Path.Combine(researchStatusDirectory, $"hook_run_consumed_{runId}.json"));
    var unconsumedResult = await new EvidenceCollector().CollectAsync(
        researchState,
        Path.Combine(temp.Path, "unconsumed-research-exports"),
        resourceStartPath: package);
    var unconsumedBundle = JsonSerializer.Deserialize<BundleManifest>(
        await File.ReadAllTextAsync(Path.Combine(unconsumedResult.BundleDirectory, "bundle_manifest.json")),
        JsonOptions())!;
    Require(unconsumedBundle.DirtyEvidence && !unconsumedBundle.Safety.ControlledResearchHooks,
        "research evidence without a single-process consumption marker was presented as controlled");
}

static async Task CorrelationAsync()
{
    using var temp = new TempDirectory();
    var prepared = DateTimeOffset.UtcNow.AddMinutes(-5);
    var collected = DateTimeOffset.UtcNow;
    var host = await CreateBundleAsync(temp.Path, "host", "machine-host", "session-host", prepared, collected);
    var joined = await CreateBundleAsync(temp.Path, "joined-client", "machine-client", "session-client",
        prepared.AddSeconds(10), collected.AddSeconds(10));
    var result = await new BundleCorrelationService().CombineAsync(new[] { host, joined }, Path.Combine(temp.Path, "combined"));
    Require(result.CorrelationEstablished && result.HasHost && result.HasJoinedClient,
        "compatible opposite-role bundles did not correlate");
    var report = await File.ReadAllTextAsync(result.ReportPath);
    Require(report.Contains("does not itself prove remote visibility", StringComparison.OrdinalIgnoreCase),
        "correlation report overclaims visibility");

    var tampered = Path.Combine(temp.Path, "tampered.zip");
    File.Copy(joined, tampered);
    using (var archive = ZipFile.Open(tampered, ZipArchiveMode.Update))
    {
        var entry = archive.GetEntry("payload.txt")!;
        entry.Delete();
        var replacement = archive.CreateEntry("payload.txt");
        await using var writer = new StreamWriter(replacement.Open());
        await writer.WriteAsync("tampered");
    }
    await ThrowsAsync<InvalidDataException>(() =>
        new BundleCorrelationService().CombineAsync(new[] { host, tampered }, Path.Combine(temp.Path, "bad")));
}

static Task ResourceLocatorAsync()
{
    var repo = FindRepoRoot();
    var repository = new DashboardResourceLocator().Locate(repo);
    Require(!repository.IsPackaged && Directory.Exists(repository.PayloadRoot), "repository resources not found");
    using var temp = new TempDirectory();
    Directory.CreateDirectory(Path.Combine(temp.Path, "Payload"));
    Directory.CreateDirectory(Path.Combine(temp.Path, "campaign"));
    var packaged = new DashboardResourceLocator().Locate(temp.Path);
    Require(packaged.IsPackaged, "packaged resources not preferred");
    return Task.CompletedTask;
}

static async Task SourceGuardsAndFixturesAsync()
{
    var repo = FindRepoRoot();
    var dashboard = Path.Combine(repo, "dashboard");
    var forbiddenText = new[]
    {
        new string(new[] { (char)0x00E2, (char)0x20AC }),
        new string(new[] { (char)0x00EF, (char)0x00BF }),
        new string(new[] { (char)0xFFFD })
    };
    foreach (var source in Directory.EnumerateFiles(dashboard, "*", SearchOption.AllDirectories)
                 .Where(path => new[] { ".cs", ".xaml", ".json", ".props", ".csproj" }
                     .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                 .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
    {
        var text = await File.ReadAllTextAsync(source);
        Require(forbiddenText.All(value => !text.Contains(value, StringComparison.Ordinal)),
            $"mojibake in {source}");
        if (Path.GetExtension(source).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            Require(!text.Contains("Http" + "Client", StringComparison.Ordinal)
                    && !text.Contains("System.Net." + "Sockets", StringComparison.Ordinal),
                $"forbidden external relay primitive in {source}");
    }
    var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "live_status_v1.json");
    var parsed = await new LiveStatusReader().ParseFileAsync(fixture);
    Require(parsed.SchemaVersion == 1, "copied fixture did not parse");
    Require((await CoreSelfTest.RunAsync()).Count >= 4, "embedded demo/self-test failed");

    using var schemaDocument = JsonDocument.Parse(
        await File.ReadAllTextAsync(Path.Combine(repo, "schemas", "evidence-bundle-v1.schema.json")));
    var schema = schemaDocument.RootElement;
    var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToHashSet();
    foreach (var name in new[]
             {
                 "selectedRole", "preparedAtUtc", "collectedAtUtc", "safety", "manifestSelfExcluded", "files"
             })
        Require(required.Contains(name), $"evidence schema required list missing {name}");
    var fileSchema = schema.GetProperty("properties").GetProperty("files").GetProperty("items");
    var fileRequired = fileSchema.GetProperty("required").EnumerateArray()
        .Select(item => item.GetString()).ToHashSet();
    Require(fileRequired.SetEquals(new[] { "path", "sizeBytes", "hash", "sourceHash", "kind" }),
        "evidence schema file contract diverged");
    Require(fileSchema.GetProperty("properties").GetProperty("sizeBytes").GetProperty("type").GetString() == "integer",
        "evidence schema sizeBytes type mismatch");
    var safetyRequired = schema.GetProperty("properties").GetProperty("safety").GetProperty("required")
        .EnumerateArray().Select(item => item.GetString()).ToHashSet();
    Require(safetyRequired.SetEquals(new[]
    {
        "writesDisabled", "rpcCallsDisabled", "mutationDisabled", "rawIdentityDisabled", "hudHookDisabled",
        "hooksDisabled", "runtimeDiscoveryDisabled", "inventoryStagesDisabled", "controlledResearchHooks",
        "compatibilityValidated", "trustedDepthEnforced", "activeCanaries"
    }), "evidence schema safety contract diverged");
    foreach (var name in safetyRequired.Where(name => name != "activeCanaries"))
        Require(schema.GetProperty("properties").GetProperty("safety").GetProperty("properties")
                .GetProperty(name!).GetProperty("type").GetString() == "boolean",
            $"evidence schema must represent diagnostic true/false safety for {name}");
    Require(schema.GetProperty("properties").GetProperty("safety").GetProperty("properties")
            .GetProperty("activeCanaries").GetProperty("type").GetString() == "integer",
        "evidence schema activeCanaries must be an integer");
}

static string CreatePackage(string root)
{
    var package = Path.Combine(root, "package");
    var scripts = Path.Combine(package, "Payload", "Mods", "CrabRuntimeProbe", "Scripts");
    Directory.CreateDirectory(scripts);
    File.WriteAllText(Path.Combine(scripts, "config.txt"), "enabled = false\nmode = read\nallowWriteProbes = false\n");
    Directory.CreateDirectory(Path.Combine(package, "Payload", "Mods"));
    File.WriteAllBytes(Path.Combine(package, "Payload", "UE4SS.dll"), Array.Empty<byte>());
    File.WriteAllText(Path.Combine(package, "Payload", "Mods", "mods.txt"),
        "BPModLoaderMod : 1\nBPML_GenericFunctions : 1\nCrabRuntimeProbe : 1\n");
    var campaign = Path.Combine(package, "campaign");
    Directory.CreateDirectory(campaign);
    File.WriteAllText(Path.Combine(campaign, "crabsync_coverage_catalog.json"),
        "{\"schemaVersion\":\"coverage-catalog-v1\",\"catalogHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"rows\":[{\"id\":\"health-row\",\"category\":\"health\",\"symbolPath\":\"/Script/Test:Health\",\"type\":\"property\",\"source\":\"object dump\",\"relevanceToCrabSync\":\"health\",\"coverageDisposition\":\"needs-coverage\",\"checklistLinkage\":[\"health-damage\"]}]}" );
    File.WriteAllText(Path.Combine(campaign, "crabsync-full-observe.checklist.json"),
        "{\"schemaVersion\":\"crabsync-checklist-v1\",\"entries\":[{\"id\":\"health-damage\",\"section\":\"Health\",\"label\":\"Damage\",\"nextAction\":\"Take damage\",\"completionRule\":\"qualifying-evidence\"},{\"id\":\"resource-crystal-gain\",\"section\":\"Resources and slots\",\"label\":\"Crystal gain observed\",\"nextAction\":\"Earn crystals\",\"completionRule\":\"qualifying-evidence\"}]}" );
    File.WriteAllText(Path.Combine(campaign, "crabsync-full-observe.profile.json"),
        "{\"id\":\"crabsync-full-observe\",\"safety\":{\"writesEnabled\":false,\"rpcInvocationEnabled\":false,\"propertyMutationEnabled\":false,\"hudHookEnabled\":false,\"rawIdentityEnabled\":false,\"externalRelayEnabled\":false,\"syntheticValuesEnabled\":false,\"staleUObjectRetentionEnabled\":false},\"normalMode\":{\"snapshotSamplerEnabled\":true,\"gameplayHooksEnabled\":false,\"lifecycleHooksEnabled\":false,\"runtimeDiscoveryEnabled\":false,\"inventoryEscalationEnabled\":false},\"passiveHooks\":{\"enabled\":false},\"inventoryEscalation\":{\"enabled\":false},\"runtimeDiscovery\":{\"enabled\":false}}" );
    var repositoryCampaign = Path.Combine(FindRepoRoot(), "campaign");
    foreach (var name in new[]
             {
                 "hook_candidate_catalog.json", "hook_validation_ledger.json",
                 "trusted_hook_manifest.json", "hook_quarantine.json",
                 "progressive_observation.defaults.json"
             })
        File.Copy(Path.Combine(repositoryCampaign, name), Path.Combine(campaign, name), true);
    Directory.CreateDirectory(Path.Combine(package, "schemas"));
    return package;
}

static string ReadinessPeerJson(
    long generation,
    string session,
    string machine,
    string selectedRole,
    string pairId,
    long sequence)
{
    object Scalar(decimal value) => new { status = "observed", value };
    object Fingerprint(string value) => new { status = "observed", value, valueFingerprint = value };
    return JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        recordType = "readiness-peer-snapshot",
        @event = "Readiness.PeerSnapshot",
        readinessSchema = "peer-snapshot-v1",
        campaignId = ReadinessCampaignContracts.CampaignId,
        campaignGeneration = generation,
        sessionId = session,
        machineId = machine,
        sequence,
        timestampUtc = DateTimeOffset.Parse("2026-07-11T20:00:00Z").AddSeconds(sequence),
        selectedRole,
        observedRole = selectedRole,
        authorityStatus = selectedRole == "host" ? "runtime-authority" : "runtime-non-authority",
        profileId = ReadinessCampaignContracts.ProfileId,
        readinessPairId = pairId,
        lifecycle = new { state = "stable", generation = 1, context = "run", stable = true },
        source = new { worldFingerprint = "world-readiness", localPlayerStateFingerprint = "player-readiness" },
        subjectCap = 4,
        subjects = new[]
        {
            new
            {
                playerStateFingerprint = "player-readiness",
                relation = "local",
                visibility = "local",
                authorityStatus = selectedRole == "host" ? "runtime-authority" : "runtime-non-authority",
                observedRole = selectedRole,
                stability = "stable",
                categoryResults = new
                {
                    health = new { result = "ok", fields = new { currentHealth = Scalar(100m), currentMaxHealth = Scalar(100m), baseMaxHealth = Scalar(100m), maxHealthMultiplier = Scalar(1m) } },
                    crystals = new { result = "ok", fields = new { crystals = Scalar(20m) } },
                    slots = new { result = "ok", fields = new { weaponModSlots = Scalar(4m), abilityModSlots = Scalar(4m), meleeModSlots = Scalar(4m), perkSlots = Scalar(4m) } },
                    equipment = new { result = "ok", fields = new { weaponFingerprint = Fingerprint("weapon-readiness"), abilityFingerprint = Fingerprint("ability-readiness"), meleeFingerprint = Fingerprint("melee-readiness") } }
                }
            }
        },
        result = "partial",
        changeKind = "initial",
        dirtyEvidence = false,
        crashSuspected = false,
        safety = new
        {
            writesDisabled = true,
            rpcCallsDisabled = true,
            mutationDisabled = true,
            hooksDisabled = true,
            runtimeDiscoveryDisabled = true,
            inventoryStagesDisabled = true,
            rawIdentityDisabled = true
        }
    });
}

static string ReadinessTerminalJson(
    long generation,
    string session,
    string machine,
    string selectedRole,
    string pairId,
    long sequence) => JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    recordType = "readiness-lifecycle-terminal",
    @event = "Readiness.LifecycleTerminal",
    readinessSchema = "terminal-lifecycle-v1",
    campaignId = ReadinessCampaignContracts.CampaignId,
    campaignGeneration = generation,
    sessionId = session,
    machineId = machine,
    sequence,
    timestampUtc = DateTimeOffset.Parse("2026-07-11T20:00:00Z").AddSeconds(sequence),
    selectedRole,
    profileId = ReadinessCampaignContracts.ProfileId,
    readinessPairId = pairId,
    priorLifecycle = new { state = "stable", generation = 1, context = "run", stable = true },
    nextLifecycle = new { state = "stopped", generation = 1, context = "run", stable = false },
    reason = "stop-requested",
    baselineReady = true,
    peerSamplingSummary = new { peerSnapshotCount = 1, visiblePlayerCount = 1, stablePlayerCount = 1 },
    dirtyEvidence = false,
    crashSuspected = false,
    safety = new
    {
        writesDisabled = true,
        rpcCallsDisabled = true,
        mutationDisabled = true,
        hooksDisabled = true,
        runtimeDiscoveryDisabled = true,
        inventoryStagesDisabled = true,
        rawIdentityDisabled = true
    }
});

static string ReadinessManifestJson(
    long generation,
    string session,
    string machine,
    string selectedRole,
    string pairId,
    string manifestId,
    DateTimeOffset preparedAtUtc) => JsonSerializer.Serialize(new
{
    schemaVersion = ReadinessCampaignContracts.ManifestSchema,
    manifestId,
    campaignId = ReadinessCampaignContracts.CampaignId,
    campaignGeneration = generation,
    sessionId = session,
    machineId = machine,
    selectedRole,
    profileId = ReadinessCampaignContracts.ProfileId,
    pairId,
    preparedAtUtc,
    inventoryStage = ReadinessCampaignContracts.DeferredInventoryStage,
    enabledChannels = ReadinessCampaignContracts.DefaultChannels(),
    peerSnapshotsEnabled = true,
    maxPeers = ReadinessCampaignContracts.MaxPeers,
    intervals = new
    {
        healthSeconds = ReadinessCampaignContracts.HealthIntervalSeconds,
        scalarSeconds = ReadinessCampaignContracts.ScalarIntervalSeconds,
        inventorySeconds = ReadinessCampaignContracts.DisabledInventoryIntervalSeconds,
        unchangedHeartbeatSeconds = ReadinessCampaignContracts.UnchangedHeartbeatSeconds
    },
    safety = new
    {
        readOnly = true,
        writeProbes = false,
        rpcCalls = false,
        mutation = false,
        hooks = false,
        runtimeDiscovery = false,
        deepInventory = false,
        rawIdentity = false
    }
}, JsonOptions());

static string ReadinessStatusJson(
    long generation,
    string session,
    string machine,
    string pairId,
    string manifestId) => JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    sequence = 10,
    writtenAtUtc = DateTimeOffset.UtcNow,
    heartbeatAtUtc = DateTimeOffset.UtcNow,
    campaignId = ReadinessCampaignContracts.CampaignId,
    campaignName = ReadinessCampaignContracts.DefaultCampaignName,
    campaignGeneration = generation,
    machineId = machine,
    sessionId = session,
    selectedRole = "host",
    observedRole = "host",
    authorityStatus = "runtime-authority",
    lifecycle = new { state = "stable", generation = 1, world = "Island", context = "run", stable = true },
    runtime = new
    {
        gameProcessRunning = true,
        gameProcessState = "running",
        ue4ssState = "loaded",
        runtimeProbeState = "healthy",
        runtimeProbeLoaded = true,
        currentProbeStage = "readiness:collecting-local-scalars",
        activeProfile = ReadinessCampaignContracts.ProfileId,
        collectionReady = true,
        readiness = new
        {
            enabled = true,
            pairId,
            manifestId,
            inventoryStage = "disabled",
            stageState = "collecting-local-scalars",
            enabledChannels = ReadinessCampaignContracts.DefaultChannels(),
            safeReadChannelsReady = true,
            visiblePlayerCount = 1,
            stablePlayerCount = 1,
            peerSnapshotCount = 1,
            inventoryCategoryCount = 0,
            maxPeers = 4,
            maxInventoryItems = 0,
            maxEnhancements = 0,
            detail = "local scalar readiness foundation; remote visibility and inventory are deferred"
        }
    },
    safety = new
    {
        writesDisabled = true,
        rpcsDisabled = true,
        mutationDisabled = true,
        hudHookDisabled = true,
        rawIdentityDisabled = true,
        hooksDisabled = true,
        runtimeDiscoveryDisabled = true,
        inventoryStagesDisabled = true,
        inventoryDepth = 0,
        circuitBreakers = new { }
    },
    checklist = new { },
    evidenceHealth = new { state = "healthy", canonicalRows = 2, rejectedRows = 0, dirtyRows = 0 },
    crashSuspected = false,
    dirtyEvidence = false
});

static async Task<string> CreateReadinessBundleAsync(
    string root,
    string label,
    string role,
    string machine,
    string session,
    DateTimeOffset prepared,
    DateTimeOffset collected,
    string pairId)
{
    var directory = Path.Combine(root, $"readiness-bundle-{label}");
    Directory.CreateDirectory(directory);
    var payload = Path.Combine(directory, "payload.txt");
    await File.WriteAllTextAsync(payload, "clean local readiness evidence");
    var provenance = Path.Combine(directory, "provenance");
    Directory.CreateDirectory(provenance);
    var catalog = Path.Combine(provenance, "crabsync_coverage_catalog.json");
    await File.WriteAllTextAsync(catalog,
        "{\"schemaVersion\":\"coverage-catalog-v1\",\"catalogHash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"rows\":[{\"id\":\"row\",\"category\":\"health\",\"symbolPath\":\"/Test:Health\",\"coverageDisposition\":\"needs-coverage\"}]}" );
    var readinessDirectory = Path.Combine(directory, "evidence", "derived-redacted");
    Directory.CreateDirectory(readinessDirectory);
    var readinessManifest = Path.Combine(readinessDirectory, "readiness_campaign_manifest.json");
    await File.WriteAllTextAsync(readinessManifest, ReadinessManifestJson(
        42, session, machine, role, pairId, $"readiness-manifest-{label}-12345678", prepared));
    var files = new[]
    {
        Entry(directory, payload, "generated-report"),
        Entry(directory, catalog, "provenance-byte-copy"),
        Entry(directory, readinessManifest, "redacted-derivative")
    };
    var manifest = new BundleManifest(
        1, "crabruntimeprobe-evidence-bundle-v1", ReadinessCampaignContracts.CampaignId,
        ReadinessCampaignContracts.DefaultCampaignName, ReadinessCampaignContracts.ProfileId,
        42, machine, session, role, prepared, collected, false, false, BundleSafety.ReadOnly,
        1, "coverage-catalog-v1", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, files);
    await File.WriteAllTextAsync(Path.Combine(directory, "bundle_manifest.json"),
        JsonSerializer.Serialize(manifest, JsonOptions()));
    var zip = directory + ".zip";
    ZipFile.CreateFromDirectory(directory, zip);
    return zip;
}

static async Task<string> CreateBundleAsync(
    string root, string role, string machine, string session, DateTimeOffset prepared, DateTimeOffset collected)
{
    var directory = Path.Combine(root, $"bundle-{role}");
    Directory.CreateDirectory(directory);
    var payload = Path.Combine(directory, "payload.txt");
    await File.WriteAllTextAsync(payload, "clean evidence");
    var provenance = Path.Combine(directory, "provenance");
    Directory.CreateDirectory(provenance);
    var catalog = Path.Combine(provenance, "crabsync_coverage_catalog.json");
    await File.WriteAllTextAsync(catalog,
        "{\"schemaVersion\":\"coverage-catalog-v1\",\"catalogHash\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"rows\":[{\"id\":\"row\",\"category\":\"health\",\"symbolPath\":\"/Test:Health\",\"coverageDisposition\":\"needs-coverage\"}]}" );
    var files = new[]
    {
        Entry(directory, payload, "generated-report"),
        Entry(directory, catalog, "provenance-byte-copy")
    };
    var manifest = new BundleManifest(
        1, "crabruntimeprobe-evidence-bundle-v1", "crabsync-full-observe", "Shared Campaign",
        "crabsync-full-observe", 42, machine, session, role, prepared, collected,
        false, false, BundleSafety.ReadOnly, 1, "coverage-catalog-v1",
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, files);
    await File.WriteAllTextAsync(Path.Combine(directory, "bundle_manifest.json"),
        JsonSerializer.Serialize(manifest, JsonOptions()));
    var zip = directory + ".zip";
    ZipFile.CreateFromDirectory(directory, zip);
    return zip;
}

static BundleFileEntry Entry(string root, string path, string kind)
{
    var hash = Hash(path);
    return new BundleFileEntry(Path.GetRelativePath(root, path).Replace('\\', '/'), new FileInfo(path).Length,
        hash, hash, kind);
}

static string SnapshotDeltaJsonl(
    string category,
    string field,
    object before,
    object after,
    string session,
    long generation,
    string machine,
    bool fingerprint = false,
    bool hooksDisabled = true,
    bool dirtyEvidence = false)
{
    return string.Join('\n', Enumerable.Range(1, 3)
        .Select(index => SnapshotRow(
            index, category, field, before, session, generation, machine,
            fingerprint: fingerprint,
            hooksDisabled: hooksDisabled,
            dirtyEvidence: dirtyEvidence))
        .Concat(Enumerable.Range(4, 3)
            .Select(index => SnapshotRow(
                index, category, field, after, session, generation, machine,
                fingerprint: fingerprint,
                hooksDisabled: hooksDisabled,
                dirtyEvidence: dirtyEvidence))));
}

static string SnapshotRow(
    long sequence,
    string category,
    string field,
    object value,
    string session,
    long generation,
    string machine,
    string worldFingerprint = "world-a",
    string playerStateFingerprint = "player-a",
    bool fingerprint = false,
    bool hooksDisabled = true,
    bool dirtyEvidence = false,
    bool crashSuspected = false,
    bool stable = true,
    string? observationProfile = null)
{
    var observedField = new Dictionary<string, object?>
    {
        ["status"] = "observed",
        [fingerprint ? "valueFingerprint" : "value"] = value
    };
    var row = new Dictionary<string, object?>
    {
        ["schemaVersion"] = 1,
        ["recordType"] = "snapshot-observation",
        ["sessionId"] = session,
        ["campaignId"] = "crabsync-full-observe",
        ["campaignGeneration"] = generation,
        ["machineId"] = machine,
        ["sequence"] = sequence,
        ["timestampUtc"] = DateTimeOffset.Parse("2026-07-10T20:00:00Z").AddSeconds(sequence),
        ["lifecycleGeneration"] = 1,
        ["context"] = "run",
        ["selectedRole"] = "host",
        ["observedRole"] = "host",
        ["worldFingerprint"] = worldFingerprint,
        ["playerStateFingerprint"] = playerStateFingerprint,
        ["category"] = category,
        ["stability"] = new Dictionary<string, object?>
        {
            ["stable"] = stable,
            ["sampleCount"] = 10,
            ["dwellSeconds"] = 30,
            ["worldStable"] = stable,
            ["playerStateStable"] = stable,
            ["reason"] = string.Empty
        },
        ["fields"] = new Dictionary<string, object?> { [field] = observedField },
        ["safety"] = new Dictionary<string, object?>
        {
            ["writesDisabled"] = true,
            ["rpcCallsDisabled"] = true,
            ["mutationDisabled"] = true,
            ["hooksDisabled"] = hooksDisabled,
            ["runtimeDiscoveryDisabled"] = true,
            ["inventoryStagesDisabled"] = true,
            ["rawIdentityDisabled"] = true
        },
        ["dirtyEvidence"] = dirtyEvidence,
        ["crashSuspected"] = crashSuspected
    };
    if (!string.IsNullOrWhiteSpace(observationProfile))
        row["observationProfile"] = observationProfile;
    return JsonSerializer.Serialize(row);
}

static string StatusJson(long sequence, DateTimeOffset heartbeat, long generation, string session,
    bool crash = false, bool dirty = false) => JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    sequence,
    writtenAtUtc = heartbeat,
    heartbeatAtUtc = heartbeat,
    campaignId = "crabsync-full-observe",
    campaignName = "Test Campaign",
    campaignGeneration = generation,
    machineId = "machine-test",
    sessionId = session,
    selectedRole = "host",
    observedRole = "host",
    authorityStatus = "authority",
    lifecycle = new { state = "stable", generation = 1, world = "Island", context = "run", stable = true },
    runtime = new { gameProcessRunning = true, gameProcessState = "running", ue4ssState = "loaded", runtimeProbeState = "healthy", runtimeProbeLoaded = true, currentProbeStage = "observe" },
    safety = new { writesDisabled = true, rpcsDisabled = true, mutationDisabled = true, hudHookDisabled = true, rawIdentityDisabled = true, hooksDisabled = true, runtimeDiscoveryDisabled = true, inventoryStagesDisabled = true, inventoryDepth = 2, circuitBreakers = new { inventory = "closed" } },
    checklist = new { },
    evidenceHealth = new { state = dirty ? "dirty" : "healthy", canonicalRows = 1, rejectedRows = 0, dirtyRows = dirty ? 1 : 0 },
    crashSuspected = crash,
    dirtyEvidence = dirty
});

static JsonSerializerOptions JsonOptions() => new()
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

static ChecklistViewItem ChecklistItem(ChecklistDefinition definition, ChecklistDisplayState state) => new(
    definition,
    state,
    state == ChecklistDisplayState.NotObserved ? 0 : 1,
    null,
    null,
    string.Empty,
    string.Empty,
    definition.Instruction,
    string.Empty);

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static string FindRepoRoot()
{
    for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        if (Directory.Exists(Path.Combine(current.FullName, "campaign"))
            && Directory.Exists(Path.Combine(current.FullName, "client"))) return current.FullName;
    throw new DirectoryNotFoundException("Repository root not found from test output.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CrabRuntimeProbeDashboardTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
