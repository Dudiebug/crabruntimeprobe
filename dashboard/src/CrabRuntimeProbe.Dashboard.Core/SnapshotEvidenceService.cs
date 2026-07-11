using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

/// <summary>Locates, filters, replays, and projects hook-free snapshot evidence for the dashboard.</summary>
public sealed class SnapshotEvidenceService
{
    private readonly SnapshotJsonlReader _reader;
    private readonly SnapshotEvidenceReducer _reducer;
    private string _cachedVersion = string.Empty;
    private SnapshotEvidenceLoadResult? _cachedResult;

    public SnapshotEvidenceService(
        SnapshotEvidenceReducer? reducer = null,
        SnapshotJsonlReader? reader = null)
    {
        _reader = reader ?? new SnapshotJsonlReader();
        _reducer = reducer ?? new SnapshotEvidenceReducer(reader: _reader);
    }

    public Task<SnapshotEvidenceLoadResult> LoadAsync(
        LocalCampaignState campaign,
        CancellationToken cancellationToken = default) =>
        LoadAsync(campaign, SnapshotReplayScope.FromCampaign(campaign), cancellationToken);

    public async Task<SnapshotEvidenceLoadResult> LoadAsync(
        LocalCampaignState campaign,
        SnapshotReplayScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(scope);
        var files = LocateFiles(campaign.StatusDirectory, campaign.SessionId);
        var version = FileVersion(files, scope);
        if (_cachedResult is not null && version.Equals(_cachedVersion, StringComparison.Ordinal))
            return _cachedResult;
        if (files.Count == 0)
        {
            _cachedVersion = version;
            return _cachedResult = new SnapshotEvidenceLoadResult(SnapshotReplayResult.Empty, files);
        }

        var observations = new Dictionary<string, (SnapshotObservation Observation, string Signature)>(
            StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var rejections = new List<SnapshotJsonlRejection>();
        var snapshotLineCount = 0;
        var syntheticLineNumber = 0;

        foreach (var path in files)
        {
            var dedicatedSnapshotFile = Path.GetFileName(path)
                .StartsWith("snapshot", StringComparison.OrdinalIgnoreCase);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                useAsync: true);
            using var textReader = new StreamReader(stream, Encoding.UTF8, true);
            var physicalLine = 0;
            while (await textReader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                physicalLine++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (ClassifyScope(line, scope) == EvidenceLineScope.Foreign)
                {
                    // A prior session/generation/profile may remain in an append-only file.
                    // It is outside this replay, not malformed evidence for the active run.
                    continue;
                }
                var recordType = RecordType(line);
                if (!dedicatedSnapshotFile && recordType is null)
                {
                    snapshotLineCount++;
                    syntheticLineNumber++;
                    rejections.Add(new SnapshotJsonlRejection(
                        syntheticLineNumber,
                        "truncated-jsonl-row",
                        $"{Path.GetFileName(path)} line {physicalLine}: incomplete JSONL row; retry after the writer finishes."));
                    continue;
                }
                if (!dedicatedSnapshotFile
                    && !string.Equals(recordType, "snapshot-observation", StringComparison.Ordinal))
                {
                    // Generic access_evidence JSONL contains many other record types. They are not
                    // malformed snapshot rows and must never enter the snapshot evidence denominator.
                    continue;
                }

                snapshotLineCount++;
                syntheticLineNumber++;
                if (!_reader.TryParse(line, out var observation, out var error))
                {
                    rejections.Add(new SnapshotJsonlRejection(
                        syntheticLineNumber,
                        error.Code,
                        $"{Path.GetFileName(path)} line {physicalLine}: {error.Detail}"));
                    continue;
                }

                var key = $"{observation!.SessionId}\u001f{observation.CampaignGeneration}\u001f{observation.Sequence}";
                var signature = Signature(observation);
                if (conflicts.Contains(key)) continue;
                if (!observations.TryGetValue(key, out var prior))
                {
                    observations[key] = (observation, signature);
                }
                else if (!prior.Signature.Equals(signature, StringComparison.Ordinal))
                {
                    observations.Remove(key);
                    conflicts.Add(key);
                    rejections.Add(new SnapshotJsonlRejection(
                        syntheticLineNumber,
                        "conflicting-snapshot-sequence",
                        $"{Path.GetFileName(path)} line {physicalLine}: conflicting content reused a snapshot sequence."));
                }
                // Byte-for-byte fallback copies normalize to the same signature and are ignored.
            }
        }

        var ordered = observations.Values
            .Select(item => item.Observation)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.TimestampUtc)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ToArray();
        var parsed = new SnapshotJsonlReadResult(ordered, rejections, snapshotLineCount);
        var result = new SnapshotEvidenceLoadResult(
            _reducer.Replay(parsed, scope),
            files);
        _cachedVersion = version;
        _cachedResult = result;
        return result;
    }

    /// <summary>
    /// Overlays only clean snapshot-derived evidence. A caller may explicitly allow a
    /// clean, terminal stale status after proving the persisted snapshot scope.
    /// </summary>
    public LiveStatusReadResult Merge(
        LiveStatusReadResult status,
        SnapshotReplayResult replay,
        SnapshotReplayScope scope,
        bool allowExpectedTerminalStale = false)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(scope);
        if (replay.Rejections.Count > 0
            || !status.HasSnapshot || status.Snapshot.DirtyEvidence || status.Snapshot.CrashSuspected
            || status.UsedLastGood || (status.IsStale && !allowExpectedTerminalStale)
            || !status.Snapshot.Safety.AllRequiredSafe
            || !status.Snapshot.SessionId.Equals(scope.SessionId, StringComparison.Ordinal)
            || status.Snapshot.CampaignGeneration != scope.CampaignGeneration
            || (scope.SelectedRole != CampaignRole.Unknown && status.Snapshot.SelectedRole != scope.SelectedRole)
            || (!string.IsNullOrWhiteSpace(scope.MachineId)
                && !status.Snapshot.MachineId.Equals(scope.MachineId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(scope.CampaignId)
                && !status.Snapshot.CampaignId.Equals(scope.CampaignId, StringComparison.Ordinal))
            || !StatusObservationProfile(status.Snapshot).Equals(
                scope.NormalizedObservationProfile,
                StringComparison.Ordinal))
            return status;

        var merged = new Dictionary<string, ChecklistEvidence>(
            status.Snapshot.Checklist,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in replay.Checklist)
        {
            if (merged.TryGetValue(pair.Key, out var existing))
            {
                if (existing.DirtyEvidence || existing.CrashSuspect) continue;
                if (!pair.Value.QualifyingEvidence && existing.QualifyingEvidence) continue;
            }

            merged[pair.Key] = pair.Value;
        }

        return status with
        {
            Snapshot = status.Snapshot with
            {
                Checklist = new ReadOnlyDictionary<string, ChecklistEvidence>(merged)
            }
        };
    }

    public IReadOnlyList<string> LocateFiles(string statusDirectory, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(statusDirectory) || !Directory.Exists(statusDirectory))
            return Array.Empty<string>();
        var parent = Directory.GetParent(statusDirectory)?.FullName;
        var roots = new[] { statusDirectory, parent }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var accessEvidenceName = $"access_evidence_{sessionId}.jsonl";
        return roots.SelectMany(root => Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly))
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                var currentDedicatedSnapshot = name.StartsWith("snapshot", StringComparison.OrdinalIgnoreCase)
                                               && name.EndsWith(
                                                   $"_{sessionId}.jsonl",
                                                   StringComparison.OrdinalIgnoreCase);
                return currentDedicatedSnapshot
                       || name.Equals(accessEvidenceName, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => Path.GetFileName(path).StartsWith("snapshot", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? RecordType(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("recordType", out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FileVersion(IReadOnlyList<string> files, SnapshotReplayScope scope) =>
        $"{scope.CampaignId}:{scope.SessionId}:{scope.CampaignGeneration}:{scope.MachineId}:{scope.SelectedRole}:"
        + $"{scope.ObservedRole}:{scope.NormalizedObservationProfile}|" + string.Join(
            "|",
            files.Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetFullPath(path)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }));

    private static EvidenceLineScope ClassifyScope(string line, SnapshotReplayScope scope)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return EvidenceLineScope.Active;

            if (TryText(root, "sessionId", out var sessionId)
                && !sessionId.Equals(scope.SessionId, StringComparison.Ordinal))
                return EvidenceLineScope.Foreign;
            if (TryInt64(root, "campaignGeneration", out var generation)
                && generation != scope.CampaignGeneration)
                return EvidenceLineScope.Foreign;
            if (TryText(root, "observationProfile", out var profile)
                && ObservationProfileIds.IsKnown(profile)
                && !ObservationProfileIds.Normalize(profile).Equals(
                    scope.NormalizedObservationProfile,
                    StringComparison.Ordinal))
                return EvidenceLineScope.Foreign;
        }
        catch (JsonException)
        {
            // The active-file caller turns unscoped malformed input into a rejection.
        }

        return EvidenceLineScope.Active;
    }

    private static string StatusObservationProfile(LiveStatusSnapshot snapshot) =>
        ObservationProfileIds.Normalize(!string.IsNullOrWhiteSpace(snapshot.Runtime.ActiveProfile)
            ? snapshot.Runtime.ActiveProfile
            : snapshot.CampaignId);

    private static bool TryText(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }

    private enum EvidenceLineScope
    {
        Active,
        Foreign
    }

    private static string Signature(SnapshotObservation observation)
    {
        var builder = new StringBuilder()
            .Append(observation.SchemaVersion).Append('|')
            .Append(observation.RecordType).Append('|')
            .Append(observation.CampaignId).Append('|')
            .Append(observation.SessionId).Append('|')
            .Append(observation.CampaignGeneration).Append('|')
            .Append(observation.MachineId).Append('|')
            .Append(observation.Sequence).Append('|')
            .Append(observation.TimestampUtc.ToUniversalTime().ToString("O")).Append('|')
            .Append(observation.LifecycleGeneration).Append('|')
            .Append(observation.Context).Append('|')
            .Append(observation.SelectedRole).Append('|')
            .Append(observation.ObservedRole).Append('|')
            .Append(observation.WorldFingerprint).Append('|')
            .Append(observation.PlayerStateFingerprint).Append('|')
            .Append(observation.Category).Append('|')
            .Append(observation.Stability).Append('|')
            .Append(observation.Safety).Append('|')
            .Append(observation.DirtyEvidence).Append('|')
            .Append(observation.CrashSuspected);
        foreach (var field in observation.Fields.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('|').Append(field.Key).Append('=').Append(field.Value.Status).Append(':')
                .Append(field.Value.Value?.Kind).Append(':').Append(field.Value.Value?.Canonical);
        }
        return builder.ToString();
    }
}
