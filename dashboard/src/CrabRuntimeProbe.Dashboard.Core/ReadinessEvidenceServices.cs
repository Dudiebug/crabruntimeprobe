using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

/// <summary>
/// A narrow replay path for the opt-in readiness profile. It deliberately consumes only the
/// closed peer/terminal records and never treats the generic access-evidence stream as a
/// transport or an instruction to call game code.
/// </summary>
public sealed record ReadinessEvidenceScope(
    string CampaignId,
    long CampaignGeneration,
    string SessionId,
    string MachineId,
    CampaignRole SelectedRole,
    string PairId)
{
    public static ReadinessEvidenceScope FromCampaign(LocalCampaignState state, string pairId) => new(
        state.CampaignId,
        state.Generation,
        state.SessionId,
        state.MachineId,
        state.Role,
        pairId);
}

public sealed record ReadinessField(string Status)
{
    public bool IsObserved => Status.Equals("observed", StringComparison.OrdinalIgnoreCase)
                              || Status.Equals("unchanged", StringComparison.OrdinalIgnoreCase);
}

public sealed record ReadinessCategoryResult(string Result, IReadOnlyDictionary<string, ReadinessField> Fields)
{
    public bool HasObservedField => Fields.Values.Any(field => field.IsObserved);
    public bool AllFieldsObserved => Fields.Count > 0 && Fields.Values.All(field => field.IsObserved);
}

public sealed record ReadinessPeerSubject(
    string PlayerStateFingerprint,
    string Relation,
    string Visibility,
    string AuthorityStatus,
    string ObservedRole,
    string Stability,
    IReadOnlyDictionary<string, ReadinessCategoryResult> CategoryResults)
{
    public bool IsLocal => Relation.Equals("local", StringComparison.Ordinal);
    public bool IsRemoteVisible => Relation.Equals("remote-visible", StringComparison.Ordinal)
                                 && Visibility.Equals("remote-visible", StringComparison.Ordinal);
    public bool IsStable => Stability.Equals("stable", StringComparison.Ordinal);
}

public sealed record ReadinessPeerSnapshot(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string CampaignId,
    long CampaignGeneration,
    string SessionId,
    string MachineId,
    CampaignRole SelectedRole,
    string ObservedRole,
    string AuthorityStatus,
    string PairId,
    string LifecycleState,
    long LifecycleGeneration,
    string LifecycleContext,
    bool LifecycleStable,
    string WorldFingerprint,
    string LocalPlayerStateFingerprint,
    IReadOnlyList<ReadinessPeerSubject> Subjects,
    string Result,
    string ChangeKind,
    bool DirtyEvidence,
    bool CrashSuspected);

public sealed record ReadinessLifecycleTerminal(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string CampaignId,
    long CampaignGeneration,
    string SessionId,
    string MachineId,
    CampaignRole SelectedRole,
    string PairId,
    string PriorState,
    long PriorGeneration,
    string NextState,
    long NextGeneration,
    string Reason,
    bool BaselineReady,
    long PeerSnapshotCount,
    int VisiblePlayerCount,
    int StablePlayerCount,
    bool DirtyEvidence,
    bool CrashSuspected);

public sealed record ReadinessEvidenceRejection(long? Sequence, string Code, string Detail);

public sealed record ReadinessEvidenceReadResult(
    IReadOnlyList<ReadinessPeerSnapshot> PeerSnapshots,
    IReadOnlyList<ReadinessLifecycleTerminal> TerminalLifecycles,
    IReadOnlyList<ReadinessEvidenceRejection> Rejections,
    int ForeignRows,
    IReadOnlyList<string> SourceFiles)
{
    public static ReadinessEvidenceReadResult Empty(IReadOnlyList<string>? files = null) => new(
        Array.Empty<ReadinessPeerSnapshot>(),
        Array.Empty<ReadinessLifecycleTerminal>(),
        Array.Empty<ReadinessEvidenceRejection>(),
        0,
        files ?? Array.Empty<string>());
}

public enum ReadinessGateDisposition
{
    Confirmed,
    Waiting,
    Blocked,
    Dirty
}

public sealed record ReadinessGate(
    string Id,
    ReadinessGateDisposition Disposition,
    string Detail,
    int EvidenceRows);

public sealed record ReadinessCampaignReport(
    string SchemaVersion,
    string CampaignId,
    string PairId,
    string SessionId,
    string MachineId,
    string SelectedRole,
    int PeerSnapshotCount,
    int TerminalLifecycleCount,
    int ForeignRowsIgnored,
    int RejectionCount,
    IReadOnlyList<ReadinessGate> Gates)
{
    public bool HasDirtyEvidence => Gates.Any(gate => gate.Disposition == ReadinessGateDisposition.Dirty);
    public bool FullCrabSyncReady => Gates.All(gate => gate.Disposition == ReadinessGateDisposition.Confirmed);
}

public sealed class ReadinessEvidenceReader
{
    private const string PeerRecordType = "readiness-peer-snapshot";
    private const string TerminalRecordType = "readiness-lifecycle-terminal";

    public async Task<ReadinessEvidenceReadResult> ReadAsync(
        string statusDirectory,
        ReadinessEvidenceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var files = LocateFiles(statusDirectory, scope.SessionId);
        if (files.Count == 0) return ReadinessEvidenceReadResult.Empty(files);

        var peerRows = new Dictionary<long, (ReadinessPeerSnapshot Row, string Signature)>();
        var terminalRows = new Dictionary<long, (ReadinessLifecycleTerminal Row, string Signature)>();
        var rejections = new List<ReadinessEvidenceRejection>();
        var foreignRows = 0;

        foreach (var file in files)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!TryParseBase(line, out var root, out var parseError))
                {
                    // LocateFiles only opens the current session's canonical evidence file.
                    // A truncated/final partial line is therefore evidence-integrity relevant,
                    // rather than a harmless record from an old run.
                    rejections.Add(new ReadinessEvidenceRejection(null, "invalid-json",
                        $"{Path.GetFileName(file)} line {lineNumber}: {parseError}"));
                    continue;
                }

                var document = root ?? throw new InvalidOperationException("Readiness JSON document was not available.");
                using (document)
                {
                    var recordType = Text(root.RootElement, "recordType");
                    if (recordType is not (PeerRecordType or TerminalRecordType)) continue;
                    if (!MatchesScope(root.RootElement, scope))
                    {
                        // Both possible locations use the active session's exact filename.
                        // A readiness row inside it with a different pair/profile/generation is
                        // not neutral old evidence; it is a current-scope integrity failure.
                        rejections.Add(new ReadinessEvidenceRejection(ReadSequence(root.RootElement), "scope-mismatch",
                            $"{Path.GetFileName(file)} line {lineNumber}: readiness row does not match the active paired scope"));
                        continue;
                    }
                    if (recordType == PeerRecordType)
                    {
                        if (!TryParsePeer(root.RootElement, out var peer, out var error))
                        {
                            rejections.Add(new ReadinessEvidenceRejection(ReadSequence(root.RootElement), error.Code,
                                $"{Path.GetFileName(file)} line {lineNumber}: {error.Detail}"));
                            continue;
                        }
                        AddUnique(peerRows, peer!, PeerSignature(peer!), rejections, "peer-snapshot-conflict");
                    }
                    else
                    {
                        if (!TryParseTerminal(root.RootElement, out var terminal, out var error))
                        {
                            rejections.Add(new ReadinessEvidenceRejection(ReadSequence(root.RootElement), error.Code,
                                $"{Path.GetFileName(file)} line {lineNumber}: {error.Detail}"));
                            continue;
                        }
                        AddUnique(terminalRows, terminal!, TerminalSignature(terminal!), rejections, "terminal-lifecycle-conflict");
                    }
                }
            }
        }

        // CampaignState.nextSequence() is global to the session, so a peer row and
        // terminal row can never legitimately share a sequence. Treat a collision as
        // integrity failure even if both rows otherwise parse cleanly.
        foreach (var sequence in peerRows.Keys.Intersect(terminalRows.Keys).ToArray())
        {
            peerRows.Remove(sequence);
            terminalRows.Remove(sequence);
            rejections.Add(new ReadinessEvidenceRejection(sequence, "cross-record-sequence-conflict",
                "A peer snapshot and terminal lifecycle row reused the same sequence."));
        }

        return new ReadinessEvidenceReadResult(
            peerRows.Values.Select(item => item.Row).OrderBy(row => row.Sequence).ThenBy(row => row.TimestampUtc).ToArray(),
            terminalRows.Values.Select(item => item.Row).OrderBy(row => row.Sequence).ThenBy(row => row.TimestampUtc).ToArray(),
            rejections,
            foreignRows,
            files);
    }

    public static IReadOnlyList<string> LocateFiles(string statusDirectory, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(statusDirectory) || !Directory.Exists(statusDirectory))
            return Array.Empty<string>();
        var parent = Directory.GetParent(statusDirectory)?.FullName;
        var expected = $"access_evidence_{sessionId}.jsonl";
        return new[] { statusDirectory, parent }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(root => Directory.EnumerateFiles(root, expected, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddUnique<T>(
        IDictionary<long, (T Row, string Signature)> output,
        T row,
        string signature,
        ICollection<ReadinessEvidenceRejection> rejections,
        string conflictCode) where T : class
    {
        var sequence = row switch
        {
            ReadinessPeerSnapshot peer => peer.Sequence,
            ReadinessLifecycleTerminal terminal => terminal.Sequence,
            _ => throw new InvalidOperationException("Unsupported readiness evidence row.")
        };
        if (output.TryGetValue(sequence, out var prior) && !prior.Signature.Equals(signature, StringComparison.Ordinal))
        {
            output.Remove(sequence);
            rejections.Add(new ReadinessEvidenceRejection(sequence, conflictCode,
                "Conflicting readiness evidence reused the same sequence."));
            return;
        }
        output.TryAdd(sequence, (row, signature));
    }

    private static bool TryParseBase(string json, out JsonDocument? document, out string error)
    {
        document = null;
        error = string.Empty;
        try
        {
            document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                document = null;
                error = "Readiness evidence must be a JSON object.";
                return false;
            }
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool MatchesScope(JsonElement root, ReadinessEvidenceScope scope) =>
        Text(root, "campaignId").Equals(scope.CampaignId, StringComparison.Ordinal)
        && Long(root, "campaignGeneration") == scope.CampaignGeneration
        && Text(root, "sessionId").Equals(scope.SessionId, StringComparison.Ordinal)
        && Text(root, "machineId").Equals(scope.MachineId, StringComparison.Ordinal)
        && CampaignRoleNames.Parse(Text(root, "selectedRole")) == scope.SelectedRole
        && Text(root, "profileId").Equals(ReadinessCampaignContracts.ProfileId, StringComparison.Ordinal)
        && Text(root, "readinessPairId").Equals(scope.PairId, StringComparison.Ordinal);

    private static bool TryParsePeer(
        JsonElement root,
        out ReadinessPeerSnapshot? row,
        out ReadinessEvidenceRejection error)
    {
        row = null;
        error = Error("invalid-peer-snapshot", "Peer snapshot did not satisfy peer-snapshot-v1.");
        if (!HasExactlyProperties(root, PeerProperties)
            || Integer(root, "schemaVersion") != 1
            || !Text(root, "recordType").Equals(PeerRecordType, StringComparison.Ordinal)
            || !Text(root, "event").Equals("Readiness.PeerSnapshot", StringComparison.Ordinal)
            || !Text(root, "readinessSchema").Equals("peer-snapshot-v1", StringComparison.Ordinal)
            || !IsTextWithin(root, "campaignId", 1, 128)
            || !ReadinessCampaignContracts.IsOpaqueIdentifier(Text(root, "sessionId"))
            || !ReadinessCampaignContracts.IsOpaqueIdentifier(Text(root, "machineId"))
            || Text(root, "selectedRole") is not ("host" or "joined-client")
            || Text(root, "observedRole") is not ("host" or "joined-client" or "solo" or "unknown")
            || !IsAuthorityStatus(Text(root, "authorityStatus"))
            || !Text(root, "profileId").Equals(ReadinessCampaignContracts.ProfileId, StringComparison.Ordinal)
            || !ReadinessCampaignContracts.IsOpaquePairId(Text(root, "readinessPairId"))
            || !TryTimestamp(root, "timestampUtc", out var timestamp)
            || !TryObject(root, "lifecycle", out var lifecycle)
            || !TryObject(root, "source", out var source)
            || !TryArray(root, "subjects", out var subjectsElement)
            || !TryObject(root, "safety", out var safety)
            || !SafetyIsReadOnly(safety)
            || !TryBoolean(root, "dirtyEvidence", out var dirty)
            || !TryBoolean(root, "crashSuspected", out var crash)
            || Long(root, "sequence") < 1
            || Long(root, "campaignGeneration") < 1
            || Text(root, "result") is not ("ok" or "partial" or "error" or "unsupported")
            || Text(root, "changeKind") is not ("initial" or "changed" or "unchanged-heartbeat" or "lifecycle-reset")
            || subjectsElement.GetArrayLength() > ReadinessCampaignContracts.MaxPeers)
        {
            return false;
        }
        if (!TryLifecycle(lifecycle, out var lifecycleState, out var lifecycleGeneration, out var lifecycleContext, out var lifecycleStable)
            || !TryFingerprints(source, out var worldFingerprint, out var localPlayerStateFingerprint))
        {
            error = Error("invalid-peer-snapshot", "Peer snapshot lifecycle or source scope is invalid.");
            return false;
        }
        var cap = Integer(root, "subjectCap");
        if (cap is < 1 or > ReadinessCampaignContracts.MaxPeers || subjectsElement.GetArrayLength() > cap)
        {
            error = Error("invalid-peer-snapshot", "Peer subject cap is invalid or exceeded.");
            return false;
        }
        var subjects = new List<ReadinessPeerSubject>();
        foreach (var subjectElement in subjectsElement.EnumerateArray())
        {
            if (!TryParseSubject(subjectElement, out var subject, out var subjectError))
            {
                error = subjectError;
                return false;
            }
            subjects.Add(subject!);
        }
        row = new ReadinessPeerSnapshot(
            Long(root, "sequence"), timestamp, Text(root, "campaignId"), Long(root, "campaignGeneration"),
            Text(root, "sessionId"), Text(root, "machineId"), CampaignRoleNames.Parse(Text(root, "selectedRole")),
            Text(root, "observedRole"), Text(root, "authorityStatus"), Text(root, "readinessPairId"),
            lifecycleState, lifecycleGeneration, lifecycleContext, lifecycleStable,
            worldFingerprint, localPlayerStateFingerprint, subjects,
            Text(root, "result"), Text(root, "changeKind"), dirty, crash);
        return true;
    }

    private static bool TryParseTerminal(
        JsonElement root,
        out ReadinessLifecycleTerminal? row,
        out ReadinessEvidenceRejection error)
    {
        row = null;
        error = Error("invalid-terminal-lifecycle", "Terminal lifecycle row did not satisfy terminal-lifecycle-v1.");
        if (!HasExactlyProperties(root, TerminalProperties)
            || Integer(root, "schemaVersion") != 1
            || !Text(root, "recordType").Equals(TerminalRecordType, StringComparison.Ordinal)
            || !Text(root, "event").Equals("Readiness.LifecycleTerminal", StringComparison.Ordinal)
            || !Text(root, "readinessSchema").Equals("terminal-lifecycle-v1", StringComparison.Ordinal)
            || !IsTextWithin(root, "campaignId", 1, 128)
            || !ReadinessCampaignContracts.IsOpaqueIdentifier(Text(root, "sessionId"))
            || !ReadinessCampaignContracts.IsOpaqueIdentifier(Text(root, "machineId"))
            || Text(root, "selectedRole") is not ("host" or "joined-client")
            || !Text(root, "profileId").Equals(ReadinessCampaignContracts.ProfileId, StringComparison.Ordinal)
            || !ReadinessCampaignContracts.IsOpaquePairId(Text(root, "readinessPairId"))
            || !TryTimestamp(root, "timestampUtc", out var timestamp)
            || !TryObject(root, "priorLifecycle", out var prior)
            || !TryObject(root, "nextLifecycle", out var next)
            || !TryObject(root, "peerSamplingSummary", out var summary)
            || !HasExactlyProperties(summary, PeerSamplingSummaryProperties)
            || !TryObject(root, "safety", out var safety)
            || !SafetyIsReadOnly(safety)
            || !TryBoolean(root, "baselineReady", out var baselineReady)
            || !TryBoolean(root, "dirtyEvidence", out var dirty)
            || !TryBoolean(root, "crashSuspected", out var crash)
            || Long(root, "sequence") < 1
            || Long(root, "campaignGeneration") < 1
            || Text(root, "reason").Length is 0 or > 240
            || !TryLifecycle(prior, out var priorState, out var priorGeneration, out _, out _)
            || !TryLifecycle(next, out var nextState, out var nextGeneration, out _, out _)
            || Long(summary, "peerSnapshotCount") < 0
            || Integer(summary, "visiblePlayerCount") is < 0 or > ReadinessCampaignContracts.MaxPeers
            || Integer(summary, "stablePlayerCount") is < 0 or > ReadinessCampaignContracts.MaxPeers)
        {
            return false;
        }
        row = new ReadinessLifecycleTerminal(
            Long(root, "sequence"), timestamp, Text(root, "campaignId"), Long(root, "campaignGeneration"),
            Text(root, "sessionId"), Text(root, "machineId"), CampaignRoleNames.Parse(Text(root, "selectedRole")),
            Text(root, "readinessPairId"), priorState, priorGeneration, nextState, nextGeneration,
            Text(root, "reason"), baselineReady, Long(summary, "peerSnapshotCount"),
            Integer(summary, "visiblePlayerCount"), Integer(summary, "stablePlayerCount"), dirty, crash);
        return true;
    }

    private static bool TryParseSubject(
        JsonElement element,
        out ReadinessPeerSubject? subject,
        out ReadinessEvidenceRejection error)
    {
        subject = null;
        error = Error("invalid-peer-subject", "Peer subject did not satisfy the bounded read-only contract.");
        if (!TryObject(element, out var value) || !HasExactlyProperties(value, SubjectProperties)
            || !IsOpaqueId(Text(value, "playerStateFingerprint"))
            || Text(value, "relation") is not ("local" or "remote-visible")
            || Text(value, "visibility") is not ("local" or "remote-visible")
            || !Text(value, "relation").Equals(Text(value, "visibility"), StringComparison.Ordinal)
            || !IsAuthorityStatus(Text(value, "authorityStatus"))
            || Text(value, "observedRole") is not ("host" or "joined-client" or "solo" or "unknown")
            || Text(value, "stability") is not ("stable" or "warming" or "unavailable")
            || !TryObject(value, "categoryResults", out var categories)
            || !HasExactlyProperties(categories, CategoryKeys)) return false;
        var output = new Dictionary<string, ReadinessCategoryResult>(StringComparer.Ordinal);
        foreach (var category in CategoryKeys)
        {
            if (!categories.TryGetProperty(category, out var categoryElement)
                || !CategoryFieldKeys.TryGetValue(category, out var expectedFields)
                || !TryParseCategory(categoryElement, expectedFields, category.Equals("equipment", StringComparison.Ordinal), out var result)) return false;
            output[category] = result!;
        }
        subject = new ReadinessPeerSubject(
            Text(value, "playerStateFingerprint"), Text(value, "relation"), Text(value, "visibility"),
            Text(value, "authorityStatus"), Text(value, "observedRole"), Text(value, "stability"),
            new ReadOnlyDictionary<string, ReadinessCategoryResult>(output));
        return true;
    }

    private static bool TryParseCategory(
        JsonElement element,
        IReadOnlySet<string> expectedFields,
        bool fingerprintsOnly,
        out ReadinessCategoryResult? result)
    {
        result = null;
        if (!TryObject(element, out var category) || !HasExactlyProperties(category, CategoryProperties)
            || !TryObject(category, "fields", out var fields)
            || !HasExactlyProperties(fields, expectedFields)
            || Text(category, "result") is not ("ok" or "partial" or "error" or "unsupported")) return false;
        var output = new Dictionary<string, ReadinessField>(StringComparer.Ordinal);
        var observed = 0;
        foreach (var field in fields.EnumerateObject())
        {
            if (!TryObject(field.Value, out var fieldValue) || !HasOnlyProperties(fieldValue, FieldProperties)) return false;
            var status = Text(fieldValue, "status");
            if (status is not ("observed" or "unavailable" or "unsupported" or "deferred" or "error")) return false;
            if (!FieldValueIsSafe(fieldValue, status, fingerprintsOnly)) return false;
            if (status == "observed") observed++;
            output[field.Name] = new ReadinessField(status);
        }
        if (Text(category, "result") == "ok" && observed == 0) return false;
        result = new ReadinessCategoryResult(Text(category, "result"), new ReadOnlyDictionary<string, ReadinessField>(output));
        return true;
    }

    private static bool TryLifecycle(JsonElement element, out string state, out long generation, out string context, out bool stable)
    {
        state = context = string.Empty;
        generation = 0;
        stable = false;
        return HasExactlyProperties(element, LifecycleProperties)
               && (state = Text(element, "state")).Length is > 0 and <= 64
               && (context = Text(element, "context")).Length is > 0 and <= 64
               && (generation = Long(element, "generation")) >= 0
               && TryBoolean(element, "stable", out stable);
    }

    private static bool TryFingerprints(JsonElement element, out string world, out string localPlayerState)
    {
        world = localPlayerState = string.Empty;
        return HasExactlyProperties(element, SourceProperties)
               && IsOpaqueId(world = Text(element, "worldFingerprint"))
               && IsOpaqueId(localPlayerState = Text(element, "localPlayerStateFingerprint"));
    }

    private static bool SafetyIsReadOnly(JsonElement element) =>
        HasExactlyProperties(element, SafetyProperties)
        && Boolean(element, "writesDisabled") && Boolean(element, "rpcCallsDisabled")
        && Boolean(element, "mutationDisabled") && Boolean(element, "hooksDisabled")
        && Boolean(element, "runtimeDiscoveryDisabled") && Boolean(element, "inventoryStagesDisabled")
        && Boolean(element, "rawIdentityDisabled");

    private static bool FieldValueIsSafe(JsonElement element, string status, bool fingerprintsOnly)
    {
        var hasValue = element.TryGetProperty("value", out var value);
        var hasFingerprint = element.TryGetProperty("valueFingerprint", out var fingerprint);
        var hasReason = element.TryGetProperty("reason", out var reason);
        if (status == "observed")
        {
            if (!hasValue || hasReason) return false;
            if (fingerprintsOnly)
            {
                return value.ValueKind == JsonValueKind.String
                       && hasFingerprint && fingerprint.ValueKind == JsonValueKind.String
                       && IsOpaqueId(value.GetString() ?? string.Empty)
                       && string.Equals(value.GetString(), fingerprint.GetString(), StringComparison.Ordinal);
            }
            return !hasFingerprint && value.ValueKind is (JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);
        }
        return !hasValue && !hasFingerprint && (!hasReason
            || reason.ValueKind == JsonValueKind.String && IsSafeDiagnosticText(reason.GetString() ?? string.Empty));
    }

    private static bool IsSafeDiagnosticText(string value) => value.Length <= 240
        && !value.Contains("C:\\Users\\", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("rawIdentity", StringComparison.OrdinalIgnoreCase)
        && !System.Text.RegularExpressions.Regex.IsMatch(value, @"(?<![0-9])[0-9]{17}(?![0-9])")
        && !System.Text.RegularExpressions.Regex.IsMatch(value, @"0x[0-9A-Fa-f]{6,}");

    private static string PeerSignature(ReadinessPeerSnapshot row) => string.Join('|', new[]
    {
        row.Sequence.ToString(CultureInfo.InvariantCulture), row.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        row.PairId, row.LifecycleGeneration.ToString(CultureInfo.InvariantCulture), row.Result, row.ChangeKind,
        string.Join(';', row.Subjects.Select(subject => subject.PlayerStateFingerprint + ":" + subject.Relation + ":" + subject.Visibility))
    });

    private static string TerminalSignature(ReadinessLifecycleTerminal row) => string.Join('|', new[]
    {
        row.Sequence.ToString(CultureInfo.InvariantCulture), row.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        row.PairId, row.PriorState, row.PriorGeneration.ToString(CultureInfo.InvariantCulture), row.NextState,
        row.NextGeneration.ToString(CultureInfo.InvariantCulture), row.Reason
    });

    private static readonly HashSet<string> PeerProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "recordType", "event", "readinessSchema", "campaignId", "campaignGeneration", "sessionId", "machineId", "sequence", "timestampUtc", "selectedRole", "observedRole", "authorityStatus", "profileId", "readinessPairId", "lifecycle", "source", "subjectCap", "subjects", "result", "changeKind", "dirtyEvidence", "crashSuspected", "safety"
    };
    private static readonly HashSet<string> TerminalProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "recordType", "event", "readinessSchema", "campaignId", "campaignGeneration", "sessionId", "machineId", "sequence", "timestampUtc", "selectedRole", "profileId", "readinessPairId", "priorLifecycle", "nextLifecycle", "reason", "baselineReady", "peerSamplingSummary", "dirtyEvidence", "crashSuspected", "safety"
    };
    private static readonly HashSet<string> LifecycleProperties = new(StringComparer.Ordinal) { "state", "generation", "context", "stable" };
    private static readonly HashSet<string> SourceProperties = new(StringComparer.Ordinal) { "worldFingerprint", "localPlayerStateFingerprint" };
    private static readonly HashSet<string> SubjectProperties = new(StringComparer.Ordinal) { "playerStateFingerprint", "relation", "visibility", "authorityStatus", "observedRole", "stability", "categoryResults" };
    private static readonly HashSet<string> CategoryKeys = new(StringComparer.Ordinal) { "health", "crystals", "slots", "equipment" };
    private static readonly HashSet<string> CategoryProperties = new(StringComparer.Ordinal) { "result", "fields" };
    private static readonly HashSet<string> FieldProperties = new(StringComparer.Ordinal) { "status", "value", "valueFingerprint", "reason" };
    private static readonly HashSet<string> SafetyProperties = new(StringComparer.Ordinal) { "writesDisabled", "rpcCallsDisabled", "mutationDisabled", "hooksDisabled", "runtimeDiscoveryDisabled", "inventoryStagesDisabled", "rawIdentityDisabled" };
    private static readonly HashSet<string> PeerSamplingSummaryProperties = new(StringComparer.Ordinal) { "peerSnapshotCount", "visiblePlayerCount", "stablePlayerCount" };
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> CategoryFieldKeys =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["health"] = new HashSet<string>(StringComparer.Ordinal) { "currentHealth", "currentMaxHealth", "baseMaxHealth", "maxHealthMultiplier" },
            ["crystals"] = new HashSet<string>(StringComparer.Ordinal) { "crystals" },
            ["slots"] = new HashSet<string>(StringComparer.Ordinal) { "weaponModSlots", "abilityModSlots", "meleeModSlots", "perkSlots" },
            ["equipment"] = new HashSet<string>(StringComparer.Ordinal) { "weaponFingerprint", "abilityFingerprint", "meleeFingerprint" }
        };

    private static ReadinessEvidenceRejection Error(string code, string detail) => new(null, code, detail);
    private static bool HasOnlyProperties(JsonElement element, IReadOnlySet<string> allowed) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().All(property => allowed.Contains(property.Name));
    private static bool HasExactlyProperties(JsonElement element, IReadOnlySet<string> allowed) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Count() == allowed.Count
        && element.EnumerateObject().All(property => allowed.Contains(property.Name));
    private static bool TryObject(JsonElement element, string property, out JsonElement value) =>
        element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;
    private static bool TryObject(JsonElement element, out JsonElement value)
    {
        value = element;
        return value.ValueKind == JsonValueKind.Object;
    }
    private static bool TryArray(JsonElement element, string property, out JsonElement value) =>
        element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Array;
    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static bool IsTextWithin(JsonElement element, string property, int minimum, int maximum) =>
        Text(element, property).Length is var length && length >= minimum && length <= maximum;
    private static bool IsAuthorityStatus(string value) =>
        value is "runtime-authority" or "runtime-non-authority" or "unknown";
    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : -1;
    private static int Integer(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : -1;
    private static long? ReadSequence(JsonElement element) => Long(element, "sequence") is var sequence and >= 0 ? sequence : null;
    private static bool Boolean(JsonElement element, string property) => TryBoolean(element, property, out var value) && value;
    private static bool TryBoolean(JsonElement element, string property, out bool value)
    {
        if (element.TryGetProperty(property, out var propertyValue)
            && propertyValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = propertyValue.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }
    private static bool TryTimestamp(JsonElement element, string property, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
               && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
    }
    private static bool IsOpaqueId(string value) => value.Length is >= 1 and <= 128
                                                    && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

public static class ReadinessReportReducer
{
    public const string Schema = "crabsync-readiness-report-v1";

    public static ReadinessCampaignReport Reduce(ReadinessEvidenceScope scope, ReadinessEvidenceReadResult evidence)
    {
        var cleanPeers = evidence.PeerSnapshots.Where(row => !row.DirtyEvidence && !row.CrashSuspected).ToArray();
        var cleanTerminals = evidence.TerminalLifecycles.Where(row => !row.DirtyEvidence && !row.CrashSuspected).ToArray();
        var localStable = cleanPeers.SelectMany(row => row.Subjects).Where(subject => subject.IsLocal && subject.IsStable).ToArray();
        var scalarCategories = new[] { "health", "crystals", "slots", "equipment" };
        var localScalarsObserved = scalarCategories.All(category => localStable.Any(subject =>
            subject.CategoryResults.TryGetValue(category, out var result) && result.AllFieldsObserved));
        var dirty = evidence.Rejections.Count > 0 || evidence.PeerSnapshots.Any(row => row.DirtyEvidence || row.CrashSuspected)
                    || evidence.TerminalLifecycles.Any(row => row.DirtyEvidence || row.CrashSuspected);

        var gates = new[]
        {
            Gate("current-session-integrity", dirty ? ReadinessGateDisposition.Dirty : ReadinessGateDisposition.Confirmed,
                dirty ? "Current readiness evidence contains a rejected, dirty, or crash-suspect row." : "All consumed readiness rows are clean and session-scoped.",
                cleanPeers.Length + cleanTerminals.Length),
            Gate("local-safe-scalars", localScalarsObserved ? ReadinessGateDisposition.Confirmed : ReadinessGateDisposition.Waiting,
                localScalarsObserved ? "All reviewed stable local health, crystals, slots, and equipment fields were captured." : "Wait for a stable local PlayerState with every reviewed scalar field available.", localStable.Length),
            Gate("peer-visible-playerstate", ReadinessGateDisposition.Blocked,
                "Remote PlayerState enumeration is deliberately deferred; paired local bundles do not prove remote visibility.", 0),
            Gate("lifecycle-terminal", cleanTerminals.Length > 0 ? ReadinessGateDisposition.Confirmed : ReadinessGateDisposition.Waiting,
                cleanTerminals.Length > 0 ? "A terminal lifecycle transition was durably recorded before scope reset." : "No clean terminal lifecycle row has been captured yet.", cleanTerminals.Length),
            Gate("inventory-item-proof", ReadinessGateDisposition.Blocked,
                "Inventory collection is disabled; item identity, metadata, counts, and enhancements remain deferred.", 0),
            Gate("transport-or-carrier", ReadinessGateDisposition.Blocked,
                "Peer snapshots do not prove a safe transport or carrier.", 0),
            Gate("write-apply", ReadinessGateDisposition.Blocked,
                "RuntimeProbe is read-only. Passive evidence cannot establish write/apply safety.", 0)
        };
        return new ReadinessCampaignReport(Schema, scope.CampaignId, scope.PairId, scope.SessionId, scope.MachineId,
            scope.SelectedRole.ToContract(), evidence.PeerSnapshots.Count, evidence.TerminalLifecycles.Count,
            evidence.ForeignRows, evidence.Rejections.Count, gates);
    }

    public static string RenderMarkdown(ReadinessCampaignReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# CrabSync Readiness Report");
        builder.AppendLine();
        builder.AppendLine($"- Campaign: `{report.CampaignId}`");
        builder.AppendLine($"- Pair: `{report.PairId}`");
        builder.AppendLine($"- Role: `{report.SelectedRole}`");
        builder.AppendLine($"- Peer snapshots: {report.PeerSnapshotCount}; terminal lifecycle rows: {report.TerminalLifecycleCount}");
        builder.AppendLine($"- Foreign rows ignored: {report.ForeignRowsIgnored}; current-row rejections: {report.RejectionCount}");
        builder.AppendLine();
        builder.AppendLine("| Gate | Status | Evidence rows | Detail |");
        builder.AppendLine("|---|---|---:|---|");
        foreach (var gate in report.Gates)
            builder.AppendLine($"| {gate.Id} | {Display(gate.Disposition)} | {gate.EvidenceRows} | {gate.Detail} |");
        builder.AppendLine();
        builder.AppendLine("This report is read-only evidence. It does not authorize transport, RPC calls, writes, or CrabSync apply behavior.");
        return builder.ToString();
    }

    private static ReadinessGate Gate(string id, ReadinessGateDisposition disposition, string detail, int rows) =>
        new(id, disposition, detail, rows);

    private static string Display(ReadinessGateDisposition disposition) => disposition switch
    {
        ReadinessGateDisposition.Confirmed => "confirmed",
        ReadinessGateDisposition.Waiting => "waiting",
        ReadinessGateDisposition.Blocked => "blocked",
        ReadinessGateDisposition.Dirty => "dirty",
        _ => "waiting"
    };
}
