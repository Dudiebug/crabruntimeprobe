using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

/// <summary>Fail-closed reader for append-only snapshot-observation-v1 JSONL.</summary>
public sealed class SnapshotJsonlReader
{
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "recordType", "sessionId", "campaignId", "campaignGeneration", "machineId",
        "sequence", "timestampUtc", "lifecycleGeneration", "context", "selectedRole", "observedRole",
        "observationProfile", "worldFingerprint", "playerStateFingerprint", "category", "stability", "fields", "safety",
        "dirtyEvidence", "crashSuspected"
    };

    private static readonly HashSet<string> StabilityProperties = new(StringComparer.Ordinal)
    {
        "stable", "sampleCount", "dwellSeconds", "worldStable", "playerStateStable", "reason"
    };

    private static readonly HashSet<string> SafetyProperties = new(StringComparer.Ordinal)
    {
        "writesDisabled", "rpcCallsDisabled", "mutationDisabled", "hooksDisabled",
        "runtimeDiscoveryDisabled", "inventoryStagesDisabled", "rawIdentityDisabled"
    };

    private static readonly HashSet<string> ObservedFieldProperties = new(StringComparer.Ordinal)
    {
        "status", "value", "valueFingerprint", "reason"
    };

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "heartbeat", "lifecycle", "health", "crystals", "slots", "equipment", "inventory",
        "metadata", "transaction", "persistence", "multiplayer"
    };

    private static readonly HashSet<string> FieldStatuses = new(StringComparer.Ordinal)
    {
        "observed", "unchanged", "unavailable", "unsupported", "deferred", "error"
    };

    private static readonly HashSet<string> ObservedRoles = new(StringComparer.Ordinal)
    {
        "host", "joined-client", "solo", "unknown"
    };

    public async Task<SnapshotJsonlReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var observations = new List<SnapshotObservation>();
        var rejections = new List<SnapshotJsonlRejection>();
        var nonEmptyLines = 0;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            nonEmptyLines++;
            if (TryParse(line, out var observation, out var error))
            {
                observations.Add(observation!);
            }
            else
            {
                rejections.Add(new SnapshotJsonlRejection(lineNumber, error.Code, error.Detail));
            }
        }

        return new SnapshotJsonlReadResult(observations, rejections, nonEmptyLines);
    }

    public SnapshotJsonlReadResult Read(string jsonl)
    {
        var observations = new List<SnapshotObservation>();
        var rejections = new List<SnapshotJsonlRejection>();
        var nonEmptyLines = 0;
        using var reader = new StringReader(jsonl);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            nonEmptyLines++;
            if (TryParse(line, out var observation, out var error))
            {
                observations.Add(observation!);
            }
            else
            {
                rejections.Add(new SnapshotJsonlRejection(lineNumber, error.Code, error.Detail));
            }
        }

        return new SnapshotJsonlReadResult(observations, rejections, nonEmptyLines);
    }

    public bool TryParse(
        string json,
        out SnapshotObservation? observation,
        out SnapshotJsonlRejection error)
    {
        observation = null;
        error = new SnapshotJsonlRejection(0, "invalid-json", "Snapshot row is not valid JSON.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = Error("invalid-shape", "Snapshot row must be a JSON object.");
                return false;
            }
            if (!HasOnlyProperties(root, RootProperties))
            {
                error = Error("unexpected-field", "Snapshot row contains a field outside snapshot-observation-v1.");
                return false;
            }

            var schemaVersion = Integer(root, "schemaVersion");
            if (schemaVersion != 1)
            {
                error = Error("unsupported-schema", "Only snapshot-observation-v1 (schemaVersion 1) is accepted.");
                return false;
            }

            var recordType = Text(root, "recordType");
            if (!recordType.Equals("snapshot-observation", StringComparison.Ordinal))
            {
                error = Error("invalid-record-type", "recordType must be snapshot-observation.");
                return false;
            }

            var sessionId = Text(root, "sessionId");
            var campaignId = Text(root, "campaignId");
            var machineId = Text(root, "machineId");
            var campaignGeneration = Long(root, "campaignGeneration");
            var sequence = Long(root, "sequence");
            var timestamp = Timestamp(root, "timestampUtc");
            var lifecycleGeneration = Long(root, "lifecycleGeneration");
            var context = Text(root, "context");
            var category = Text(root, "category");
            var selectedRoleText = Text(root, "selectedRole");
            var selectedRole = CampaignRoleNames.Parse(selectedRoleText);
            var observedRole = Text(root, "observedRole");
            var observationProfile = "normal-play-guide";
            if (root.TryGetProperty("observationProfile", out var profileElement))
                observationProfile = profileElement.ValueKind == JsonValueKind.String
                    ? profileElement.GetString() ?? string.Empty
                    : string.Empty;
            var hasWorldFingerprint = TryNullableText(root, "worldFingerprint", out var worldFingerprint);
            var hasPlayerStateFingerprint = TryNullableText(
                root, "playerStateFingerprint", out var playerStateFingerprint);
            if (!IsOpaqueId(sessionId, 8, 128) || string.IsNullOrWhiteSpace(campaignId) || campaignId.Length > 128
                || !IsOpaqueId(machineId, 8, 96) || campaignGeneration < 1 || sequence < 1
                || timestamp is null || lifecycleGeneration < 0 || string.IsNullOrWhiteSpace(context)
                || context.Length > 64 || !Categories.Contains(category)
                || selectedRoleText is not ("host" or "joined-client") || selectedRole == CampaignRole.Unknown
                || observationProfile is not ("normal-play-guide" or "progressive-broad-observation")
                || !ObservedRoles.Contains(observedRole) || !hasWorldFingerprint || !hasPlayerStateFingerprint
                || !IsOptionalFingerprint(worldFingerprint) || !IsOptionalFingerprint(playerStateFingerprint))
            {
                error = Error(
                    "missing-required-field",
                    "A snapshot requires campaign/session/machine/generation/sequence/time/lifecycle/role/category/scope fields.");
                return false;
            }

            if (!TryObject(root, "stability", out var stabilityElement)
                || !HasOnlyProperties(stabilityElement, StabilityProperties)
                || !TryBoolean(stabilityElement, "stable", out var stable)
                || !TryInteger(stabilityElement, "sampleCount", out var sampleCount)
                || !TryNumber(stabilityElement, "dwellSeconds", out var dwellSeconds)
                || !TryBoolean(stabilityElement, "worldStable", out var worldStable)
                || !TryBoolean(stabilityElement, "playerStateStable", out var playerStateStable)
                || sampleCount < 0 || dwellSeconds < 0
                || Text(stabilityElement, "reason").Length > 240
                || (stable && worldStable && playerStateStable
                    && (string.IsNullOrWhiteSpace(worldFingerprint)
                        || string.IsNullOrWhiteSpace(playerStateFingerprint))))
            {
                error = Error(
                    "invalid-stability",
                    "Snapshot stability requires stable, sampleCount, worldStable, and playerStateStable values.");
                return false;
            }

            if (!TryObject(root, "safety", out var safetyElement)
                || !HasOnlyProperties(safetyElement, SafetyProperties)
                || !TryBoolean(safetyElement, "writesDisabled", out var writesDisabled)
                || !TryBoolean(safetyElement, "rpcCallsDisabled", out var rpcCallsDisabled)
                || !TryBoolean(safetyElement, "mutationDisabled", out var mutationDisabled)
                || !TryBoolean(safetyElement, "hooksDisabled", out var hooksDisabled)
                || !TryBoolean(safetyElement, "runtimeDiscoveryDisabled", out var runtimeDiscoveryDisabled)
                || !TryBoolean(safetyElement, "inventoryStagesDisabled", out var inventoryStagesDisabled)
                || !TryBoolean(safetyElement, "rawIdentityDisabled", out var rawIdentityDisabled))
            {
                error = Error(
                    "invalid-safety",
                    "Snapshot safety must explicitly report all seven hook-free, read-only safety flags.");
                return false;
            }
            if (observationProfile == "progressive-broad-observation" && hooksDisabled
                || observationProfile != "progressive-broad-observation" && !hooksDisabled)
            {
                error = Error(
                    "profile-safety-mismatch",
                    "Snapshot observationProfile and hooksDisabled must truthfully describe the active runtime mode.");
                return false;
            }

            if (!TryObject(root, "fields", out var fieldsElement))
            {
                error = Error("invalid-fields", "Snapshot fields must be a JSON object.");
                return false;
            }

            var fields = ParseFields(fieldsElement);
            if (!TryBoolean(root, "dirtyEvidence", out var dirtyEvidence)
                || !TryBoolean(root, "crashSuspected", out var crashSuspected))
            {
                error = Error(
                    "invalid-cleanliness",
                    "Snapshot rows must explicitly report dirtyEvidence and crashSuspected.");
                return false;
            }
            observation = new SnapshotObservation(
                schemaVersion,
                recordType,
                campaignId,
                sessionId,
                campaignGeneration,
                machineId,
                sequence,
                timestamp.Value,
                lifecycleGeneration,
                context,
                selectedRole,
                observedRole,
                worldFingerprint,
                playerStateFingerprint,
                category,
                new SnapshotStability(
                    stable,
                    sampleCount,
                    dwellSeconds,
                    worldStable,
                    playerStateStable,
                    Text(stabilityElement, "reason")),
                fields,
                new SnapshotSafety(
                    writesDisabled,
                    rpcCallsDisabled,
                    hooksDisabled,
                    mutationDisabled,
                    runtimeDiscoveryDisabled,
                    inventoryStagesDisabled,
                    rawIdentityDisabled),
                dirtyEvidence,
                crashSuspected,
                observationProfile);
            error = new SnapshotJsonlRejection(0, string.Empty, string.Empty);
            return true;
        }
        catch (JsonException exception)
        {
            error = Error("invalid-json", $"Snapshot row is not valid JSON: {exception.Message}");
            return false;
        }
        catch (FormatException exception)
        {
            error = Error("invalid-value", exception.Message);
            return false;
        }
    }

    private static IReadOnlyDictionary<string, SnapshotObservedField> ParseFields(JsonElement root)
    {
        var output = new Dictionary<string, SnapshotObservedField>(StringComparer.OrdinalIgnoreCase);
        FlattenFields(root, string.Empty, output);
        return new ReadOnlyDictionary<string, SnapshotObservedField>(output);
    }

    private static void FlattenFields(
        JsonElement element,
        string prefix,
        IDictionary<string, SnapshotObservedField> output)
    {
        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(value, ObservedFieldProperties)
                || !value.TryGetProperty("status", out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String)
            {
                throw new FormatException($"Snapshot field {path} must be an observedField object with status.");
            }

            var status = statusElement.GetString() ?? string.Empty;
            var reason = Text(value, "reason");
            if (!FieldStatuses.Contains(status) || reason.Length > 240)
            {
                throw new FormatException($"Snapshot field {path} has an unsupported status.");
            }
            if (value.TryGetProperty("valueFingerprint", out var declaredFingerprint)
                && (declaredFingerprint.ValueKind != JsonValueKind.String
                    || !IsOpaqueId(declaredFingerprint.GetString() ?? string.Empty, 1, 128)))
            {
                throw new FormatException($"Snapshot field {path} has an invalid valueFingerprint.");
            }

            if (value.TryGetProperty("value", out var wrappedValue))
            {
                if (wrappedValue.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    throw new FormatException($"Snapshot field {path} value must be a scalar.");
                output[path] = new SnapshotObservedField(status, ParseValue(wrappedValue));
            }
            else if (value.TryGetProperty("valueFingerprint", out var fingerprint))
            {
                output[path] = new SnapshotObservedField(status, ParseValue(fingerprint));
            }
            else
            {
                output[path] = new SnapshotObservedField(status, null);
            }
        }
    }

    private static SnapshotValue ParseValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => new SnapshotValue(SnapshotValueKind.Null, "null"),
        JsonValueKind.String => new SnapshotValue(
            SnapshotValueKind.String,
            value.GetString() ?? string.Empty,
            Text: value.GetString() ?? string.Empty),
        JsonValueKind.True => new SnapshotValue(SnapshotValueKind.Boolean, "true", Boolean: true),
        JsonValueKind.False => new SnapshotValue(SnapshotValueKind.Boolean, "false", Boolean: false),
        JsonValueKind.Number when decimal.TryParse(
            value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) =>
            new SnapshotValue(
                SnapshotValueKind.Number,
                number.ToString("G29", CultureInfo.InvariantCulture),
                Number: number),
        JsonValueKind.Number => new SnapshotValue(SnapshotValueKind.Json, value.GetRawText()),
        _ => new SnapshotValue(SnapshotValueKind.Json, CanonicalJson(value))
    };

    private static string CanonicalJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static SnapshotJsonlRejection Error(string code, string detail) => new(0, code, detail);

    private static bool TryObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object) return true;
        value = default;
        return false;
    }

    private static bool HasOnlyProperties(JsonElement element, IReadOnlySet<string> allowed) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().All(property => allowed.Contains(property.Name));

    private static bool IsOpaqueId(string value, int minimumLength, int maximumLength) =>
        value.Length >= minimumLength && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsOptionalFingerprint(string value) =>
        string.IsNullOrEmpty(value) || IsOpaqueId(value, 1, 128);

    private static bool TryNullableText(JsonElement element, string name, out string value)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            value = string.Empty;
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            value = string.Empty;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int Integer(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static long Long(JsonElement element, string name, long fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : fallback;

    private static double Number(JsonElement element, string name, double fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : fallback;

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        if (element.TryGetProperty(name, out var property) && property.TryGetDouble(out value)) return true;
        value = 0;
        return false;
    }

    private static bool TryInteger(JsonElement element, string name, out int value)
    {
        if (element.TryGetProperty(name, out var property) && property.TryGetInt32(out value)) return true;
        value = 0;
        return false;
    }

    private static bool TryBoolean(JsonElement element, string name, out bool value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static DateTimeOffset? Timestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
