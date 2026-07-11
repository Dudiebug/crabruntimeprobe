using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class BreadcrumbJournalReader
{
    public const int MaximumJournalBytes = 8 * 1024 * 1024;
    public const int MaximumRecordBytes = 4096;
    public const int MaximumRecords = 8192;

    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "registration", "pre", "post", "blueprint-post-only", "runtime"
    };

    private static readonly HashSet<string> Boundaries = new(StringComparer.Ordinal)
    {
        "registration-begin", "registration-complete", "registration-failed", "callback-enter",
        "context-resolve-begin", "context-resolve-complete", "scope-resolve-begin", "scope-resolve-complete",
        "prestate-read-begin", "prestate-read-complete", "arguments-read-begin", "arguments-read-complete",
        "poststate-read-begin", "poststate-read-complete", "evidence-write-begin", "evidence-write-complete",
        "callback-exit", "run-complete"
    };

    public async Task<BreadcrumbReadResult> ReadAsync(
        string path,
        string expectedRunId,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) return Empty("journal-missing", "Breadcrumb journal does not exist.", true);
        if (info.Length <= 0) return Empty("journal-empty", "Breadcrumb journal is empty.", true);
        if (info.Length > MaximumJournalBytes)
            return Empty("journal-oversize", "Breadcrumb journal exceeds its bounded size.", true);
        var text = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return Parse(text, expectedRunId);
    }

    public BreadcrumbReadResult Parse(string text, string expectedRunId)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumJournalBytes)
            return Empty("journal-oversize", "Breadcrumb journal exceeds its bounded size.", true);
        if (string.IsNullOrEmpty(text)) return Empty("journal-empty", "Breadcrumb journal is empty.", true);

        var records = new List<HookBreadcrumb>();
        var issues = new List<BreadcrumbReadIssue>();
        var sequences = new HashSet<long>();
        var callbackCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hasTrailingNewline = text.EndsWith('\n');
        var truncatedFinal = false;
        long previousSequence = 0;
        long highestLifecycleGeneration = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var sourceLine = index + 1;
            var line = lines[index];
            var finalPhysicalLine = index == lines.Length - 1;
            if (line.Length == 0)
            {
                if (!finalPhysicalLine) issues.Add(new BreadcrumbReadIssue("blank-record", "Blank journal record was ignored.", sourceLine, false));
                continue;
            }
            if (records.Count >= MaximumRecords)
            {
                issues.Add(new BreadcrumbReadIssue("record-cap", "Breadcrumb journal exceeds its bounded record count.", sourceLine, true));
                break;
            }
            if (Encoding.UTF8.GetByteCount(line) > MaximumRecordBytes)
            {
                issues.Add(new BreadcrumbReadIssue("record-oversize", "Breadcrumb record exceeds its bounded size.", sourceLine, true));
                continue;
            }
            HookBreadcrumb record;
            try
            {
                record = ParseLine(line, sourceLine);
            }
            catch (Exception ex) when (ex is JsonException or ResearchSchemaException)
            {
                if (finalPhysicalLine && !hasTrailingNewline)
                {
                    truncatedFinal = true;
                    issues.Add(new BreadcrumbReadIssue("truncated-final-write", "The incomplete final breadcrumb was ignored; prior complete records were recovered.", sourceLine, false));
                    continue;
                }
                issues.Add(new BreadcrumbReadIssue("invalid-record", "An interior breadcrumb is malformed or contains an unknown value.", sourceLine, true));
                continue;
            }
            if (record.ValidationDepth < MinimumDepth(record.Boundary))
            {
                issues.Add(new BreadcrumbReadIssue(
                    "boundary-over-depth",
                    $"Boundary '{record.Boundary}' is forbidden at Depth {(int)record.ValidationDepth}.",
                    sourceLine,
                    true));
                continue;
            }
            if (!string.Equals(record.RunId, expectedRunId, StringComparison.Ordinal))
            {
                issues.Add(new BreadcrumbReadIssue("wrong-run", "A breadcrumb belongs to a different run and was rejected.", sourceLine, true));
                continue;
            }
            if (!sequences.Add(record.Sequence))
            {
                issues.Add(new BreadcrumbReadIssue("duplicate-sequence", $"Duplicate breadcrumb sequence {record.Sequence} was rejected.", sourceLine, true));
                continue;
            }
            if (record.Sequence <= previousSequence)
            {
                issues.Add(new BreadcrumbReadIssue("nonmonotonic-sequence", "Breadcrumb sequences are not strictly increasing.", sourceLine, true));
                continue;
            }
            if (record.LifecycleGeneration < highestLifecycleGeneration)
            {
                issues.Add(new BreadcrumbReadIssue("stale-lifecycle-generation", "A stale lifecycle-generation breadcrumb was rejected.", sourceLine, true));
                continue;
            }
            previousSequence = record.Sequence;
            highestLifecycleGeneration = Math.Max(highestLifecycleGeneration, record.LifecycleGeneration);
            records.Add(record);
            if (record.Boundary == "callback-enter")
                callbackCounts[record.CandidateId] = callbackCounts.GetValueOrDefault(record.CandidateId) + 1;
        }

        var matching = Match(records);
        issues.AddRange(matching.Issues);
        return new BreadcrumbReadResult(
            records,
            issues,
            truncatedFinal,
            matching.LastCompleted,
            matching.LastUnmatched,
            new ReadOnlyDictionary<string, int>(callbackCounts));
    }

    private static HookValidationDepth MinimumDepth(string boundary) => boundary switch
    {
        "callback-exit" => HookValidationDepth.CallbackEntryExit,
        "context-resolve-begin" or "context-resolve-complete" => HookValidationDepth.ContextResolution,
        "scope-resolve-begin" or "scope-resolve-complete" => HookValidationDepth.PlayerStateScope,
        "prestate-read-begin" or "prestate-read-complete"
            or "poststate-read-begin" or "poststate-read-complete" => HookValidationDepth.ReviewedStateReads,
        "arguments-read-begin" or "arguments-read-complete" => HookValidationDepth.DocumentedArguments,
        "evidence-write-begin" or "evidence-write-complete" => HookValidationDepth.FullPassiveEvidence,
        _ => HookValidationDepth.RegistrationOnly
    };

    private static HookBreadcrumb ParseLine(string line, int sourceLine)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        HookCandidateCatalogReader.RequireProperties(root, "breadcrumb",
            "schemaVersion", "sequence", "runId", "candidateId", "hookPathFingerprint", "validationDepth",
            "candidateRole", "invocationId", "phase", "boundary", "lifecycleGeneration", "timestampUtc",
            "monotonicMicros");
        var schema = HookCandidateCatalogReader.RequiredString(root, "schemaVersion", 64);
        if (schema != ResearchContracts.BreadcrumbSchema)
            throw new ResearchSchemaException($"Unsupported breadcrumb schema '{schema}'.");
        var role = HookCandidateCatalogReader.RequiredString(root, "candidateRole", 16);
        if (role is not ("trusted" or "canary")) throw new ResearchSchemaException("Unknown candidate role.");
        var phase = HookCandidateCatalogReader.RequiredString(root, "phase", 32);
        if (!Phases.Contains(phase)) throw new ResearchSchemaException("Unknown breadcrumb phase.");
        var boundary = HookCandidateCatalogReader.RequiredString(root, "boundary", 64);
        if (!Boundaries.Contains(boundary)) throw new ResearchSchemaException("Unknown breadcrumb boundary.");
        var depth = HookCandidateCatalogReader.RequiredInt(root, "validationDepth", 1, 7);
        var fingerprint = HookCandidateCatalogReader.RequiredHash(root, "hookPathFingerprint");
        return new HookBreadcrumb(
            HookCandidateCatalogReader.RequiredLong(root, "sequence", 1, 100000),
            SafeId(root, "runId"), SafeCandidateId(root), fingerprint, (HookValidationDepth)depth, role,
            SafeId(root, "invocationId"), phase, boundary,
            HookCandidateCatalogReader.RequiredLong(root, "lifecycleGeneration", 0, 9007199254740991),
            HookCandidateCatalogReader.RequiredDate(root, "timestampUtc"),
            HookCandidateCatalogReader.RequiredLong(root, "monotonicMicros", 0, 9007199254740991),
            sourceLine);
    }

    private static string SafeId(JsonElement root, string name)
    {
        var value = HookCandidateCatalogReader.RequiredString(root, name, 128);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ResearchSchemaException($"Unsafe {name}.");
        return value;
    }

    private static string SafeCandidateId(JsonElement root)
    {
        var value = SafeId(root, "candidateId");
        if (!value.StartsWith("hook-", StringComparison.Ordinal)) throw new ResearchSchemaException("Invalid candidate ID.");
        return value;
    }

    private static BreadcrumbMatchResult Match(
        IReadOnlyList<HookBreadcrumb> records)
    {
        var open = new Dictionary<string, Stack<HookBreadcrumb>>(StringComparer.Ordinal);
        var issues = new List<BreadcrumbReadIssue>();
        HookBreadcrumb? lastCompleted = null;
        foreach (var record in records)
        {
            if (record.Boundary == "registration-begin")
            {
                if (!Push(open, Key(record, "registration"), record))
                    issues.Add(OrphanIssue("duplicate-open-boundary", record,
                        "A registration boundary was opened twice for one invocation."));
                continue;
            }
            if (record.Boundary is "registration-complete" or "registration-failed")
            {
                if (Pop(open, Key(record, "registration"))) lastCompleted = record;
                else issues.Add(OrphanIssue("orphan-complete-boundary", record,
                    "A registration completion has no matching begin record."));
                continue;
            }
            if (record.Boundary == "callback-enter")
            {
                // Depth 1's single entry is its complete, intentionally minimal callback observation.
                if (record.ValidationDepth >= HookValidationDepth.CallbackEntryExit)
                {
                    if (!Push(open, Key(record, "callback"), record))
                        issues.Add(OrphanIssue("duplicate-open-boundary", record,
                            "A callback boundary was opened twice for one invocation."));
                }
                else
                    lastCompleted = record;
                continue;
            }
            if (record.Boundary == "callback-exit")
            {
                if (Pop(open, Key(record, "callback"))) lastCompleted = record;
                else issues.Add(OrphanIssue("orphan-complete-boundary", record,
                    "A callback exit has no matching callback entry."));
                continue;
            }
            if (record.Boundary.EndsWith("-begin", StringComparison.Ordinal))
            {
                if (!Push(open, Key(record, record.Boundary[..^"-begin".Length]), record))
                    issues.Add(OrphanIssue("duplicate-open-boundary", record,
                        "A validation boundary was opened twice for one invocation."));
                continue;
            }
            if (record.Boundary.EndsWith("-complete", StringComparison.Ordinal))
            {
                if (Pop(open, Key(record, record.Boundary[..^"-complete".Length]))) lastCompleted = record;
                else issues.Add(OrphanIssue("orphan-complete-boundary", record,
                    "A validation completion has no matching begin record."));
                continue;
            }
            if (record.Boundary == "run-complete") lastCompleted = record;
        }
        var unmatched = open.Values.SelectMany(stack => stack).OrderBy(record => record.Sequence).LastOrDefault();
        return new BreadcrumbMatchResult(lastCompleted, unmatched, issues);
    }

    private static string Key(HookBreadcrumb record, string boundary) =>
        $"{record.CandidateId}|{record.InvocationId}|{boundary}";

    private static bool Push(Dictionary<string, Stack<HookBreadcrumb>> open, string key, HookBreadcrumb record)
    {
        if (!open.TryGetValue(key, out var stack)) open[key] = stack = new Stack<HookBreadcrumb>();
        if (stack.Count > 0) return false;
        stack.Push(record);
        return true;
    }

    private static bool Pop(Dictionary<string, Stack<HookBreadcrumb>> open, string key)
    {
        if (!open.TryGetValue(key, out var stack) || stack.Count == 0) return false;
        stack.Pop();
        if (stack.Count == 0) open.Remove(key);
        return true;
    }

    private static BreadcrumbReadIssue OrphanIssue(string code, HookBreadcrumb record, string detail) =>
        new(code, detail, record.SourceLine, true);

    private static BreadcrumbReadResult Empty(string code, string detail, bool fatal) =>
        new(Array.Empty<HookBreadcrumb>(), new[] { new BreadcrumbReadIssue(code, detail, 0, fatal) },
            false, null, null, new ReadOnlyDictionary<string, int>(new Dictionary<string, int>()));

    private sealed record BreadcrumbMatchResult(
        HookBreadcrumb? LastCompleted,
        HookBreadcrumb? LastUnmatched,
        IReadOnlyList<BreadcrumbReadIssue> Issues);
}
