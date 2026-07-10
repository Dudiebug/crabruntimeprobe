using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class CoverageCatalogReader
{
    public async Task<IReadOnlyList<CoverageRow>> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    public IReadOnlyList<CoverageRow> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    public IReadOnlyList<CoverageRow> Parse(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : TryArray(root, "rows", out var rows)
                ? rows
                : TryArray(root, "catalog", out var catalog)
                    ? catalog
                    : default;
        if (array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CoverageRow>();
        }

        var output = new List<CoverageRow>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var symbol = Text(element, "symbolPath", Text(element, "symbol", Text(element, "path", string.Empty)));
            var category = Text(element, "category", "Unclassified");
            if (string.IsNullOrWhiteSpace(symbol))
            {
                // Unknown candidates remain visible rather than being discarded.
                symbol = $"unknown:{Text(element, "rowId", Text(element, "id", Guid.NewGuid().ToString("N")))}";
            }

            var evidenceCleanliness = Text(element, "evidenceCleanliness", string.Empty);
            var disposition = Text(
                element,
                "terminalDisposition",
                Text(element, "disposition", Text(element, "coverageDisposition", string.Empty)));
            var dirty = Bool(element, "dirtyEvidence")
                        || evidenceCleanliness.Contains("dirty", StringComparison.OrdinalIgnoreCase);
            var crash = Bool(element, "crashSuspect")
                        || evidenceCleanliness.Contains("crash", StringComparison.OrdinalIgnoreCase)
                        || disposition.Contains("crash-suspect", StringComparison.OrdinalIgnoreCase);

            output.Add(new CoverageRow(
                Text(element, "rowId", Text(element, "id", StableId(category, symbol))),
                category,
                symbol,
                Text(element, "type", "unknown"),
                Text(element, "source", Text(element, "sourceStatus", "unknown")),
                Text(element, "relevance", Text(element, "relevanceToCrabSync", "Unclassified CrabSync candidate")),
                Text(element, "readStatus", "not-tested"),
                Text(element, "naturalObservationStatus", Text(element, "naturalStatus", "not-observed")),
                Text(element, "argumentMetadataStatus", Text(element, "argumentStatus", "unknown")),
                Text(element, "ownershipAuthorityStatus", Text(element, "authorityStatus", "unknown")),
                Text(element, "visibilityDirection", Text(element, "direction", "unknown")),
                Flatten(element, "lifecycleCoverage", "unknown"),
                Flatten(element, "persistenceUiCoverage", Flatten(element, "persistence/UI coverage", "unknown")),
                Text(element, "writeApplyStatus", Text(element, "writeStatus", "not-tested")),
                Text(element, "safetyClassification", "unknown"),
                disposition,
                Text(element, "nextRequiredObservation", "Classify and observe this candidate safely."),
                TextList(element, "checklistLinks", "checklistLinkage"),
                TextList(element, "coverageCapabilities"),
                dirty,
                crash));
        }

        return output
            .OrderBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.SymbolPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string StableId(string category, string symbol)
    {
        var source = $"{category}:{symbol}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static bool TryArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Array;
    }

    private static string Text(JsonElement element, string name, string fallback)
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

    private static string Flatten(JsonElement element, string name, string fallback)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? fallback;
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return value.GetRawText();
        return value.ValueKind == JsonValueKind.Undefined ? fallback : value.GetRawText();
    }

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True;

    private static IReadOnlyList<string> TextList(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .ToArray();
            }

            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        return Array.Empty<string>();
    }
}

public sealed class CapabilityReadinessService
{
    private static readonly IReadOnlyDictionary<string, (string Token, string[] LegacyTerms)> Capabilities =
        new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["Inventory"] = ("inventory", new[] { "inventory", "pickup", "drop", "salvage", "duplicate", "reroll", "relic", "perk", "mod" }),
            ["Metadata and enhancements"] = ("metadata-and-enhancements", new[] { "metadata", "inventoryinfo", "enhancement", "level", "accumulated" }),
            ["Slots"] = ("slots", new[] { "slot" }),
            ["Equipment"] = ("equipment", new[] { "equipment", "weapon", "ability", "melee", "loadout" }),
            ["Crystals"] = ("crystals", new[] { "crystal", "currency", "reward", "shop" }),
            ["Health"] = ("health", new[] { "health", "armor", "damage", "heal", "death", "reviv", "respawn" }),
            ["Multiplayer ownership and visibility"] = ("multiplayer-ownership-and-visibility", new[] { "multiplayer", "ownership", "authority", "visibility", "replication", "playerstate", "gamestate" }),
            ["Lifecycle"] = ("lifecycle", new[] { "lifecycle", "join", "leave", "disconnect", "travel", "portal", "save", "restore", "respawn" }),
            ["Official apply candidates"] = ("official-apply-candidates", new[] { "apply", "write", "rpc", "onrep", "server", "client", "multicast" })
        };

    public IReadOnlyList<CapabilityReadiness> Calculate(IReadOnlyList<CoverageRow> rows)
    {
        var hasExactCapabilities = rows.Any(row => row.CoverageCapabilities.Count > 0);
        return Capabilities.Select(pair =>
        {
            var relevant = rows.Where(row => hasExactCapabilities
                ? row.CoverageCapabilities.Contains(pair.Value.Token, StringComparer.OrdinalIgnoreCase)
                : Matches(row, pair.Value.LegacyTerms)).ToArray();
            var closed = relevant.Count(row => !row.NeedsCoverage);
            var complete = relevant.Length > 0 && closed == relevant.Length;
            var needs = relevant.Length - closed;
            var summary = relevant.Length == 0
                ? "No catalog rows were generated for this capability."
                : complete
                    ? "Every material catalog row has a clean terminal disposition."
                    : $"{needs} of {relevant.Length} material row(s) still need coverage.";
            return new CapabilityReadiness(pair.Key, complete, relevant.Length, closed, needs, summary);
        }).ToArray();
    }

    private static bool Matches(CoverageRow row, IEnumerable<string> terms)
    {
        var haystack = $"{row.Category} {row.SymbolPath} {row.Relevance} {row.Type} {row.WriteApplyStatus}";
        return terms.Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
