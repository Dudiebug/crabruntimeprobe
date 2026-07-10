using CrabRuntimeProbe.Dashboard.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var tests = new (string Name, Func<Task> Body)[]
{
    ("status schema version and additive fields", StatusSchemaAsync),
    ("atomic ring partial-read fallback and parser update", RingFallbackAsync),
    ("stale heartbeat plus crash and dirty state", StaleCrashDirtyAsync),
    ("data-driven checklist prerequisites and qualifying evidence", ChecklistAsync),
    ("friend-facing Play Guide projection, mapping, states, and filters", PlayGuideAsync),
    ("real exhaustive coverage catalog aliases and terminal states", CoverageCatalogAsync),
    ("atomic file replacement", AtomicFileAsync),
    ("safe prepare, mods merge, status archive, and resume", PrepareAndResumeAsync),
    ("identity redaction", RedactionAsync),
    ("byte-identical canonical collection and unsafe omission", CollectionAsync),
    ("strict host-client correlation and tamper rejection", CorrelationAsync),
    ("resource locator packaged and repository modes", ResourceLocatorAsync),
    ("source guards, fixture, and demo", SourceGuardsAndFixturesAsync)
};

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
    Require(updated.Snapshot.Sequence == 2 && reader.History.Count == 2, "parser state did not advance");
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
        "Weapon mod", "Ability mod", "Melee mod", "Perk", "Relic"
    }), "power-up action does not expose the five required friend-facing chips");
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

    var store = new DashboardStateStore(Path.Combine(temp.Path, "state"));
    var service = new CampaignService(store);
    var state = await service.PrepareAsync(
        new GameInstallation(game, executable, "test"), CampaignRole.JoinedClient, "Test Campaign", package);
    var config = await File.ReadAllTextAsync(Path.Combine(game, "Mods", "CrabRuntimeProbe", "Scripts", "config.txt"));
    foreach (var required in new[]
             {
                 "enabled = true", "tickDriver = executeDelay", "mode = observe",
                 "probeSet = crabsync-full-observe", "allowWriteProbes = false", "allowRpcProbes = false",
                 "allowHudTickHook = false", "allowRawIdentityEvidence = false"
             })
        Require(config.Contains(required, StringComparison.Ordinal), $"safe config missing {required}");
    var mods = await File.ReadAllTextAsync(Path.Combine(game, "Mods", "mods.txt"));
    Require(mods.Contains("; keep this comment") && mods.Contains("UnrelatedMod : 0"), "mods.txt merge lost user lines");
    foreach (var name in new[] { "BPModLoaderMod : 1", "BPML_GenericFunctions : 1", "CrabRuntimeProbe : 1" })
        Require(mods.Contains(name, StringComparison.OrdinalIgnoreCase), $"required mod not enabled: {name}");
    Require(File.Exists(canonical), "prepare deleted canonical append-only evidence");
    Require(Directory.EnumerateFiles(Path.Combine(status, "status-archive"), "*", SearchOption.AllDirectories).Any(),
        "prior live status/control markers were not archived");
    var request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(status, "dashboard_campaign_request.json")));
    Require(request.RootElement.GetProperty("command").GetString() == "prepare", "prepare marker command mismatch");
    var resumed = await service.ResumeAsync();
    Require(resumed?.Phase == "monitoring", "resume did not restore campaign");
    request = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(status, "dashboard_campaign_request.json")));
    Require(request.RootElement.GetProperty("command").GetString() == "resume", "resume marker command mismatch");
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
        1, "crabsync-full-observe", "Test Campaign", generation, "dashboard-session", "machine-a",
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
                 "writesDisabled", "rpcCallsDisabled", "mutationDisabled", "rawIdentityDisabled", "hudHookDisabled"
             })
        Require(safety.GetProperty(name).ValueKind == JsonValueKind.True, $"safety contract mismatch {name}");
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
        "writesDisabled", "rpcCallsDisabled", "mutationDisabled", "rawIdentityDisabled", "hudHookDisabled"
    }), "evidence schema safety contract diverged");
}

static string CreatePackage(string root)
{
    var package = Path.Combine(root, "package");
    var scripts = Path.Combine(package, "Payload", "Mods", "CrabRuntimeProbe", "Scripts");
    Directory.CreateDirectory(scripts);
    File.WriteAllText(Path.Combine(scripts, "config.txt"), "enabled = false\nmode = read\nallowWriteProbes = false\n");
    Directory.CreateDirectory(Path.Combine(package, "Payload", "Mods"));
    File.WriteAllText(Path.Combine(package, "Payload", "Mods", "mods.txt"),
        "BPModLoaderMod : 1\nBPML_GenericFunctions : 1\nCrabRuntimeProbe : 1\n");
    var campaign = Path.Combine(package, "campaign");
    Directory.CreateDirectory(campaign);
    File.WriteAllText(Path.Combine(campaign, "crabsync_coverage_catalog.json"),
        "{\"schemaVersion\":\"coverage-catalog-v1\",\"catalogHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"rows\":[{\"id\":\"health-row\",\"category\":\"health\",\"symbolPath\":\"/Script/Test:Health\",\"type\":\"property\",\"source\":\"object dump\",\"relevanceToCrabSync\":\"health\",\"coverageDisposition\":\"needs-coverage\",\"checklistLinkage\":[\"health-damage\"]}]}" );
    File.WriteAllText(Path.Combine(campaign, "crabsync-full-observe.checklist.json"),
        "{\"schemaVersion\":\"crabsync-checklist-v1\",\"entries\":[{\"id\":\"health-damage\",\"section\":\"Health\",\"label\":\"Damage\",\"nextAction\":\"Take damage\",\"completionRule\":\"qualifying-evidence\"}]}" );
    File.WriteAllText(Path.Combine(campaign, "crabsync-full-observe.profile.json"),
        "{\"id\":\"crabsync-full-observe\",\"safety\":{\"writesEnabled\":false,\"rpcInvocationEnabled\":false,\"propertyMutationEnabled\":false,\"hudHookEnabled\":false,\"rawIdentityEnabled\":false,\"externalRelayEnabled\":false,\"syntheticValuesEnabled\":false,\"staleUObjectRetentionEnabled\":false}}" );
    Directory.CreateDirectory(Path.Combine(package, "schemas"));
    return package;
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
    safety = new { writesDisabled = true, rpcsDisabled = true, mutationDisabled = true, hudHookDisabled = true, rawIdentityDisabled = true, inventoryDepth = 2, circuitBreakers = new { inventory = "closed" } },
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
