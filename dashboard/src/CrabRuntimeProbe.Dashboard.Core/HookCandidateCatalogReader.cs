using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class ResearchSchemaException : Exception
{
    public ResearchSchemaException(string message) : base(message) { }
}

public sealed class HookCandidateCatalogReader
{
    private const int MaximumCatalogBytes = 4 * 1024 * 1024;
    private const int MaximumCandidates = 512;

    public async Task<HookCandidateCatalog> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 1 or > MaximumCatalogBytes)
            throw new ResearchSchemaException($"Candidate catalog size {stream.Length} is outside the accepted range.");
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    public HookCandidateCatalog Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumCatalogBytes)
            throw new ResearchSchemaException("Candidate catalog exceeds the accepted size.");
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    public HookCandidateCatalog Parse(JsonElement root)
    {
        RequireObject(root, "Candidate catalog");
        RequireProperties(root, "candidate catalog",
            "schemaVersion", "generatedAtUtc", "coverageCatalogHash", "hookCatalogIdentity",
            "callbackImplementationVersion", "callbackSchemaVersion", "validationBehaviorVersion",
            "principalCandidateId", "candidateCount", "candidates");
        var schema = RequiredString(root, "schemaVersion", 96);
        if (schema != ResearchContracts.CandidateCatalogSchema)
            throw new ResearchSchemaException($"Unsupported candidate catalog schema '{schema}'.");
        var coverageHash = RequiredHash(root, "coverageCatalogHash");
        var identity = RequiredHash(root, "hookCatalogIdentity");
        var generated = RequiredDate(root, "generatedAtUtc");
        var candidatesElement = RequiredArray(root, "candidates");
        var declaredCount = RequiredInt(root, "candidateCount", 1, MaximumCandidates);
        var candidates = new List<HookCandidateDefinition>(declaredCount);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in candidatesElement.EnumerateArray())
        {
            if (candidates.Count >= MaximumCandidates)
                throw new ResearchSchemaException("Candidate catalog exceeds its bounded candidate count.");
            var candidate = ParseCandidate(element);
            if (!ids.Add(candidate.Id)) throw new ResearchSchemaException($"Duplicate candidate ID '{candidate.Id}'.");
            if (!paths.Add(candidate.HookPath)) throw new ResearchSchemaException($"Duplicate hook path '{candidate.HookPath}'.");
            if (!fingerprints.Add(candidate.HookPathFingerprint))
                throw new ResearchSchemaException($"Duplicate hook path fingerprint '{candidate.HookPathFingerprint}'.");
            candidates.Add(candidate);
        }
        if (candidates.Count != declaredCount)
            throw new ResearchSchemaException($"Candidate count {candidates.Count} does not match declared count {declaredCount}.");
        var principal = RequiredString(root, "principalCandidateId", 128);
        if (!ids.Contains(principal)) throw new ResearchSchemaException("Principal candidate is not present in the catalog.");
        if (candidates.Count != 111)
            throw new ResearchSchemaException($"v1.0.4 expects the preserved 111 candidate identities; found {candidates.Count}.");
        return new HookCandidateCatalog(
            schema, generated, coverageHash, identity,
            RequiredToken(root, "callbackImplementationVersion", 96),
            RequiredToken(root, "callbackSchemaVersion", 96),
            RequiredToken(root, "validationBehaviorVersion", 96),
            principal,
            candidates.OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray());
    }

    private static HookCandidateDefinition ParseCandidate(JsonElement element)
    {
        RequireObject(element, "Candidate");
        RequireProperties(element, "candidate",
            "id", "displayName", "category", "hookPath", "hookPathFingerprint", "ownerPath", "ownerKind",
            "candidateType", "priority", "suggestedAction", "roleApplicability", "allowedDepths",
            "maximumValidationDepth", "callbackPhase", "scopeProperties", "reviewedStateFields",
            "argumentSchema", "checklistLinks", "knownCrashContext", "staticCatalogValidated",
            "naturalObservationOnly", "neverInvoke", "noMutation", "staleUObjectRetention", "explicitExclusions");
        var id = RequiredString(element, "id", 128);
        if (!id.StartsWith("hook-", StringComparison.Ordinal) ||
            id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ResearchSchemaException($"Invalid stable candidate ID '{id}'.");
        var hookPath = RequiredString(element, "hookPath", 512);
        if (!IsReviewedExactHookPath(hookPath)) throw new ResearchSchemaException($"Invalid exact hook path for '{id}'.");
        var pathFingerprint = RequiredHash(element, "hookPathFingerprint");
        if (!string.Equals(pathFingerprint, CompatibilityFingerprintService.Sha256Text(hookPath), StringComparison.Ordinal))
            throw new ResearchSchemaException($"Hook path fingerprint mismatch for '{id}'.");
        var ownerKind = RequiredString(element, "ownerKind", 16);
        if (ownerKind is not ("native" or "blueprint")) throw new ResearchSchemaException($"Unknown owner kind '{ownerKind}'.");
        if (ownerKind == "native" && !hookPath.StartsWith("/Script/", StringComparison.Ordinal))
            throw new ResearchSchemaException($"Native candidate '{id}' does not use a /Script/ path.");
        if (ownerKind == "blueprint" && !hookPath.StartsWith("/Game/", StringComparison.Ordinal))
            throw new ResearchSchemaException($"Blueprint candidate '{id}' does not use a /Game/ path.");
        var maximumDepth = RequiredInt(element, "maximumValidationDepth", 0, 7);
        var depths = RequiredArray(element, "allowedDepths").EnumerateArray().ToArray();
        if (depths.Length != 8 || depths.Select(depth => RequiredInt(depth, "depth", 0, 7)).Distinct().Count() != 8)
            throw new ResearchSchemaException($"Candidate '{id}' does not define the complete depth ladder.");
        foreach (var requiredTrue in new[] { "staticCatalogValidated", "naturalObservationOnly", "neverInvoke", "noMutation" })
            if (!RequiredBool(element, requiredTrue)) throw new ResearchSchemaException($"Candidate '{id}' has unsafe {requiredTrue}=false.");
        if (RequiredBool(element, "staleUObjectRetention"))
            throw new ResearchSchemaException($"Candidate '{id}' permits stale UObject retention.");
        var scopeProperties = StringArray(element, "scopeProperties", 8, 64);
        if (scopeProperties.Any(value => value is not ("OwningPS" or "PlayerState")))
            throw new ResearchSchemaException($"Candidate '{id}' contains an unreviewed ownership property.");
        var stateFields = StringArray(element, "reviewedStateFields", 32, 96);
        if (stateFields.Any(value => !value.StartsWith("CrabPS.", StringComparison.Ordinal) ||
                                     value.Contains("InventoryInfo", StringComparison.OrdinalIgnoreCase) ||
                                     value.Contains("Enhancements", StringComparison.OrdinalIgnoreCase) ||
                                     value.EndsWith("Mods", StringComparison.OrdinalIgnoreCase) ||
                                     value.EndsWith("Relics", StringComparison.OrdinalIgnoreCase) ||
                                     value.EndsWith("Perks", StringComparison.OrdinalIgnoreCase)))
            throw new ResearchSchemaException($"Candidate '{id}' contains a state read outside the Depth 5 allowlist.");
        var arguments = new List<HookArgumentDefinition>();
        foreach (var argument in RequiredArray(element, "argumentSchema").EnumerateArray())
        {
            if (arguments.Count >= 32) throw new ResearchSchemaException($"Candidate '{id}' has too many arguments.");
            RequireProperties(argument, "argument", "name", "propertyType", "valueTypePath", "safeSummary", "redaction");
            arguments.Add(new HookArgumentDefinition(
                RequiredString(argument, "name", 96), RequiredString(argument, "propertyType", 64),
                String(argument, "valueTypePath", 256), RequiredString(argument, "safeSummary", 96),
                RequiredString(argument, "redaction", 96)));
        }
        var exclusions = StringArray(element, "explicitExclusions", 16, 96);
        foreach (var required in new[] { "array-traversal", "inventory-elements", "InventoryInfo", "Enhancements", "arbitrary-uobject-exploration" })
            if (!exclusions.Contains(required, StringComparer.Ordinal))
                throw new ResearchSchemaException($"Candidate '{id}' is missing required exclusion '{required}'.");
        return new HookCandidateDefinition(
            id, RequiredString(element, "displayName", 128), RequiredString(element, "category", 96),
            hookPath, pathFingerprint, RequiredString(element, "ownerPath", 512), ownerKind,
            RequiredString(element, "candidateType", 32), RequiredInt(element, "priority", 0, 100000),
            RequiredString(element, "suggestedAction", 512), RequiredString(element, "roleApplicability", 96),
            (HookValidationDepth)maximumDepth, RequiredString(element, "callbackPhase", 32), scopeProperties,
            stateFields, arguments, StringArray(element, "checklistLinks", 64, 128),
            RequiredBool(element, "knownCrashContext"));
    }

    private static bool IsReviewedExactHookPath(string value)
    {
        if (!(value.StartsWith("/Script/CrabChampions.", StringComparison.Ordinal) ||
              value.StartsWith("/Script/Engine.", StringComparison.Ordinal) ||
              value.StartsWith("/Game/", StringComparison.Ordinal))) return false;
        var colon = value.LastIndexOf(':');
        return colon > 6 && colon < value.Length - 1 &&
               value[(colon + 1)..].All(character => char.IsAsciiLetterOrDigit(character) || character == '_') &&
               value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
    }

    internal static void RequireProperties(JsonElement element, string label, params string[] allowed)
    {
        RequireObject(element, label);
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!set.Contains(property.Name)) throw new ResearchSchemaException($"Unknown {label} property '{property.Name}'.");
        foreach (var required in allowed)
            if (!element.TryGetProperty(required, out _)) throw new ResearchSchemaException($"{label} is missing '{required}'.");
    }

    internal static void RequireObject(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ResearchSchemaException($"{label} must be an object.");
    }

    internal static JsonElement RequiredArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ResearchSchemaException($"'{name}' must be an array.");
        return value;
    }

    internal static string RequiredString(JsonElement element, string name, int maxLength)
    {
        var value = String(element, name, maxLength);
        if (string.IsNullOrWhiteSpace(value)) throw new ResearchSchemaException($"'{name}' must not be empty.");
        return value;
    }

    internal static string String(JsonElement element, string name, int maxLength)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ResearchSchemaException($"'{name}' must be a string.");
        var text = value.GetString() ?? string.Empty;
        if (text.Length > maxLength || text.IndexOf('\0') >= 0)
            throw new ResearchSchemaException($"'{name}' exceeds its accepted length.");
        return text;
    }

    internal static string RequiredToken(JsonElement element, string name, int maxLength)
    {
        var value = RequiredString(element, name, maxLength);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ResearchSchemaException($"'{name}' contains unsafe characters.");
        return value;
    }

    internal static string RequiredHash(JsonElement element, string name)
    {
        var value = RequiredString(element, name, 64);
        if (!ResearchContracts.IsSha256(value)) throw new ResearchSchemaException($"'{name}' must be a lowercase SHA-256 hash.");
        return value;
    }

    internal static int RequiredInt(JsonElement element, string name, int minimum, int maximum)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) || result < minimum || result > maximum)
            throw new ResearchSchemaException($"'{name}' must be between {minimum} and {maximum}.");
        return result;
    }

    internal static long RequiredLong(JsonElement element, string name, long minimum, long maximum)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result) || result < minimum || result > maximum)
            throw new ResearchSchemaException($"'{name}' is outside the accepted range.");
        return result;
    }

    internal static bool RequiredBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ResearchSchemaException($"'{name}' must be a boolean.");
        return value.GetBoolean();
    }

    internal static DateTimeOffset RequiredDate(JsonElement element, string name)
    {
        var value = RequiredString(element, name, 64);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            throw new ResearchSchemaException($"'{name}' must be an ISO-8601 timestamp.");
        return date.ToUniversalTime();
    }

    internal static IReadOnlyList<string> StringArray(JsonElement element, string name, int maxItems, int maxLength)
    {
        var output = new List<string>();
        foreach (var item in RequiredArray(element, name).EnumerateArray())
        {
            if (output.Count >= maxItems || item.ValueKind != JsonValueKind.String)
                throw new ResearchSchemaException($"'{name}' is outside its accepted bounds.");
            var text = item.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength || !output.AddUnique(text))
                throw new ResearchSchemaException($"'{name}' contains an empty, duplicate, or overlong value.");
        }
        return output;
    }
}

public sealed class CompatibilityFingerprintService
{
    public async Task<CompatibilityFingerprint> FromInstallationAsync(
        string gameExecutablePath,
        string ue4ssDllPath,
        HookCandidateCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        var gameBuild = await FileComponentAsync(gameExecutablePath, preferFileVersion: true, cancellationToken)
            .ConfigureAwait(false);
        var ue4ssVersion = await FileComponentAsync(ue4ssDllPath, preferFileVersion: true, cancellationToken)
            .ConfigureAwait(false);
        return Compute(gameBuild, ue4ssVersion, catalog, DateTimeOffset.UtcNow);
    }

    public CompatibilityFingerprint Compute(
        string gameBuild,
        string ue4ssVersion,
        HookCandidateCatalog catalog,
        DateTimeOffset? computedAtUtc = null)
    {
        var components = new[]
        {
            NormalizeComponent(gameBuild), NormalizeComponent(ue4ssVersion), catalog.CoverageCatalogHash,
            catalog.HookCatalogIdentity, catalog.CallbackImplementationVersion, catalog.CallbackSchemaVersion,
            catalog.ValidationBehaviorVersion
        };
        var fingerprint = Sha256Text(string.Join("\n", components));
        return new CompatibilityFingerprint(
            ResearchContracts.CompatibilitySchema, components[0], components[1], components[2], components[3],
            components[4], components[5], components[6], fingerprint, computedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private static async Task<string> FileComponentAsync(
        string path,
        bool preferFileVersion,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return "unavailable";
        if (preferFileVersion)
        {
            var info = FileVersionInfo.GetVersionInfo(fullPath);
            var version = info.ProductVersion ?? info.FileVersion;
            if (!string.IsNullOrWhiteSpace(version) && version.Any(char.IsDigit))
                return NormalizeComponent("version:" + version);
        }
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string NormalizeComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var output = new StringBuilder(Math.Min(value.Length, 128));
        foreach (var character in value.Trim())
        {
            if (output.Length >= 128) break;
            output.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '+' or ':' or '-'
                ? character
                : '_');
        }
        return output.Length == 0 ? "unknown" : output.ToString();
    }

    internal static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal static class ResearchCollectionExtensions
{
    public static bool AddUnique(this List<string> values, string value)
    {
        if (values.Contains(value, StringComparer.Ordinal)) return false;
        values.Add(value);
        return true;
    }
}
