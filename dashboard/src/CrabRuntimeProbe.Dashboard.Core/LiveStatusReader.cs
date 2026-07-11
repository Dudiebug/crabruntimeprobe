using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class StatusSchemaException : Exception
{
    public StatusSchemaException(string message) : base(message)
    {
    }
}

public sealed class LiveStatusReader
{
    public const int SupportedSchemaMajor = 1;
    private const int MaximumStatusBytes = 4 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Queue<LiveStatusSnapshot> _history = new();
    private readonly int _historyCapacity;
    private LiveStatusSnapshot? _lastGood;

    public LiveStatusReader(int historyCapacity = 64)
    {
        if (historyCapacity is < 4 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        }

        _historyCapacity = historyCapacity;
    }

    public IReadOnlyList<LiveStatusSnapshot> History
    {
        get
        {
            lock (_sync)
            {
                return _history.ToArray();
            }
        }
    }

    public async Task<LiveStatusReadResult> ReadLatestAsync(
        string statusDirectory,
        DateTimeOffset? nowUtc = null,
        TimeSpan? staleAfter = null,
        CancellationToken cancellationToken = default)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var staleWindow = staleAfter ?? TimeSpan.FromSeconds(8);
        if (!Directory.Exists(statusDirectory))
        {
            return LastGoodResult(now, staleWindow, $"Status directory not found: {statusDirectory}");
        }

        var candidates = Directory.EnumerateFiles(statusDirectory, "live_status.slot*.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(statusDirectory, "live_status.json", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(16)
            .ToArray();

        if (candidates.Length == 0)
        {
            return LastGoodResult(now, staleWindow, "No completed live-status snapshots are available yet.");
        }

        var valid = new List<LiveStatusSnapshot>();
        var errors = new List<string>();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                valid.Add(await ParseFileAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or StatusSchemaException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (valid.Count == 0)
        {
            return LastGoodResult(now, staleWindow, string.Join(" | ", errors));
        }

        var newest = valid
            .OrderByDescending(snapshot => snapshot.Sequence)
            .ThenByDescending(snapshot => snapshot.WrittenAtUtc)
            .First();

        Remember(newest);
        var stale = now - newest.HeartbeatAtUtc > staleWindow;
        return new LiveStatusReadResult(
            newest,
            true,
            stale,
            false,
            errors.Count == 0 ? string.Empty : string.Join(" | ", errors),
            now);
    }

    public async Task<LiveStatusSnapshot> ParseFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 1 or > MaximumStatusBytes)
        {
            throw new StatusSchemaException($"Status size {stream.Length} is outside the accepted range.");
        }

        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Parse(document.RootElement, fullPath);
    }

    public LiveStatusSnapshot Parse(string json, string sourceFile = "fixture")
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement, sourceFile);
    }

    public LiveStatusSnapshot Parse(JsonElement root, string sourceFile)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new StatusSchemaException("Live status must be a JSON object.");
        }

        var schemaVersion = Int(root, "schemaVersion", 0);
        var schemaMajor = schemaVersion >= 100 ? schemaVersion / 100 : schemaVersion;
        if (schemaMajor != SupportedSchemaMajor)
        {
            throw new StatusSchemaException(
                $"Unsupported live-status schema major {schemaMajor}; supported major is {SupportedSchemaMajor}.");
        }

        var sequence = Long(root, "sequence", -1);
        if (sequence < 0)
        {
            throw new StatusSchemaException("Live status is missing a non-negative sequence.");
        }

        var written = Date(root, "writtenAtUtc") ?? Date(root, "writtenAt")
            ?? throw new StatusSchemaException("Live status is missing writtenAtUtc.");
        var heartbeat = Date(root, "heartbeatAtUtc") ?? Date(root, "heartbeatAt") ?? written;

        var lifecycleElement = Object(root, "lifecycle");
        var lifecycle = new LifecycleInfo(
            String(lifecycleElement, "state", "unknown"),
            Long(lifecycleElement, "generation", Long(root, "lifecycleGeneration", 0)),
            String(lifecycleElement, "world", String(lifecycleElement, "worldName", string.Empty)),
            String(lifecycleElement, "context", string.Empty),
            Bool(lifecycleElement, "stable", false),
            Date(lifecycleElement, "changedAtUtc"));

        var runtimeElement = Object(root, "runtime");
        var gameState = String(runtimeElement, "gameProcessState", String(runtimeElement, "gameState", "unknown"));
        var runtime = new RuntimeInfo(
            Bool(runtimeElement, "gameProcessRunning", gameState.Equals("running", StringComparison.OrdinalIgnoreCase)),
            gameState,
            String(runtimeElement, "ue4ssState", "unknown"),
            String(runtimeElement, "runtimeProbeState", String(runtimeElement, "probeState", "unknown")),
            Bool(runtimeElement, "runtimeProbeLoaded", false),
            String(runtimeElement, "currentProbeStage",
                String(runtimeElement, "probeStage", String(root, "currentProbeStage", "idle"))),
            NullableInt(runtimeElement, "gameProcessId"));

        var safetyElement = Object(root, "safety");
        var breakers = ParseCircuitBreakers(Object(safetyElement, "circuitBreakers"));
        var safety = new SafetyInfo(
            Disabled(safetyElement, "writesDisabled", "noWrites", "writesEnabled"),
            Bool(safetyElement, "rpcCallsDisabled",
                Disabled(safetyElement, "rpcsDisabled", "noRpcs", "rpcCallsEnabled")),
            Disabled(safetyElement, "mutationDisabled", "noMutation", "mutationEnabled"),
            Disabled(safetyElement, "hudHookDisabled", "noHud", "hudHookEnabled"),
            Disabled(safetyElement, "rawIdentityDisabled", "rawIdentityRedacted", "rawIdentityEnabled"),
            Bool(safetyElement, "hooksDisabled", false),
            Bool(safetyElement, "runtimeDiscoveryDisabled", false),
            Bool(safetyElement, "inventoryStagesDisabled", false),
            Int(safetyElement, "inventoryDepth", 0),
            breakers);

        var checklist = ParseChecklist(Object(root, "checklist"));
        var evidenceHealth = ParseEvidenceHealth(root);

        return new LiveStatusSnapshot(
            schemaVersion,
            sequence,
            written,
            heartbeat,
            String(root, "campaignId", string.Empty),
            String(root, "campaignName", "crabsync-full-observe"),
            Long(root, "campaignGeneration", Long(root, "generation", 0)),
            String(root, "machineId", string.Empty),
            String(root, "sessionId", string.Empty),
            CampaignRoleNames.Parse(String(root, "selectedRole", "unknown")),
            String(root, "observedRole", "unknown"),
            String(root, "authorityStatus", "unknown"),
            lifecycle,
            runtime,
            safety,
            checklist,
            evidenceHealth,
            Bool(root, "crashSuspected", false),
            Bool(root, "dirtyEvidence", false),
            sourceFile);
    }

    private static IReadOnlyDictionary<string, ChecklistEvidence> ParseChecklist(JsonElement element)
    {
        var output = new Dictionary<string, ChecklistEvidence>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new ReadOnlyDictionary<string, ChecklistEvidence>(output);
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var item = property.Value;
            var id = String(item, "id", property.Name);
            output[id] = new ChecklistEvidence(
                id,
                String(item, "status", "not-observed"),
                Long(item, "observationCount", Long(item, "count", 0)),
                Date(item, "firstObservedAtUtc") ?? Date(item, "firstTimestamp"),
                Date(item, "lastObservedAtUtc") ?? Date(item, "lastTimestamp") ?? Date(item, "latestTimestamp"),
                StringList(item, "sourceRoles", "sourceRole", "sources"),
                StringList(item, "evidenceSessions", "evidenceSession", "sessionIds", "evidenceSessionReferences"),
                StringList(item, "evidenceKinds", "evidenceKind"),
                Bool(item, "hookRegistered", false),
                Bool(item, "qualifyingEvidence", false),
                Bool(item, "dirtyEvidence", false),
                Bool(item, "crashSuspect", false),
                String(item, "nextInstruction", String(item, "instruction", String(item, "nextAction", string.Empty))),
                String(item, "detail", String(item, "notes", string.Empty)));
        }

        return new ReadOnlyDictionary<string, ChecklistEvidence>(output);
    }

    private static EvidenceHealthInfo ParseEvidenceHealth(JsonElement root)
    {
        if (!root.TryGetProperty("evidenceHealth", out var value))
        {
            return new EvidenceHealthInfo("unknown", 0, 0, 0, string.Empty);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return new EvidenceHealthInfo(value.GetString() ?? "unknown", 0, 0, 0, string.Empty);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return new EvidenceHealthInfo("unknown", 0, 0, 0, string.Empty);
        }

        return new EvidenceHealthInfo(
            String(value, "state", String(value, "status", "unknown")),
            Long(value, "canonicalRows", Long(value, "rowCount", 0)),
            Long(value, "rejectedRows", 0),
            Long(value, "dirtyRows", 0),
            String(value, "detail", string.Empty));
    }

    private static IReadOnlyDictionary<string, string> ParseCircuitBreakers(JsonElement element)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                output[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "unknown",
                    JsonValueKind.True => "closed",
                    JsonValueKind.False => "open",
                    JsonValueKind.Object => String(property.Value, "state", "unknown"),
                    _ => property.Value.GetRawText()
                };
            }
        }

        return new ReadOnlyDictionary<string, string>(output);
    }

    private LiveStatusReadResult LastGoodResult(DateTimeOffset now, TimeSpan staleAfter, string error)
    {
        lock (_sync)
        {
            if (_lastGood is null)
            {
                return new LiveStatusReadResult(LiveStatusSnapshot.Empty, false, true, false, error, now);
            }

            return new LiveStatusReadResult(
                _lastGood,
                true,
                now - _lastGood.HeartbeatAtUtc > staleAfter,
                true,
                error,
                now);
        }
    }

    private void Remember(LiveStatusSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_lastGood is not null && snapshot.Sequence < _lastGood.Sequence)
            {
                return;
            }

            _lastGood = snapshot;
            if (_history.Count == 0 || _history.Last().Sequence != snapshot.Sequence)
            {
                _history.Enqueue(snapshot);
                while (_history.Count > _historyCapacity)
                {
                    _history.Dequeue();
                }
            }
        }
    }

    private static bool Disabled(JsonElement element, string disabledName, string noName, string enabledName)
    {
        if (TryBool(element, disabledName, out var disabled)) return disabled;
        if (TryBool(element, noName, out var noValue)) return noValue;
        if (TryBool(element, enabledName, out var enabled)) return !enabled;
        return false;
    }

    private static JsonElement Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string String(JsonElement element, string name, string fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => fallback
        };
    }

    private static bool Bool(JsonElement element, string name, bool fallback) =>
        TryBool(element, name, out var value) ? value : fallback;

    private static bool TryBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out value))
            return true;
        return false;
    }

    private static int Int(JsonElement element, string name, int fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        if (value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : fallback;
    }

    private static int? NullableInt(JsonElement element, string name)
    {
        var value = Int(element, name, int.MinValue);
        return value == int.MinValue ? null : value;
    }

    private static long Long(JsonElement element, string name, long fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        if (value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : fallback;
    }

    private static DateTimeOffset? Date(JsonElement element, string name)
    {
        var value = String(element, name, string.Empty);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static IReadOnlyList<string> StringList(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Where(item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText())
                    .Where(item => item.Length > 0)
                    .ToArray();
            }

            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return new[] { value.GetString()! };
            }
        }

        return Array.Empty<string>();
    }
}
