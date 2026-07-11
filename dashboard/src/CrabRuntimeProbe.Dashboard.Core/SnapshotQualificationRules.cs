using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

/// <summary>
/// Hook-free player-facing rules. These are data definitions rather than executable game probes.
/// An external rule document can replace or extend this catalog through SnapshotQualificationRuleLoader.
/// </summary>
public static class SnapshotQualificationRuleCatalog
{
    private static readonly string[] InRunContexts = ["in-run", "run", "island", "gameplay"];

    public static IReadOnlyList<SnapshotQualificationRule> PlayerFacing { get; } =
    [
        Rule(
            "health-damage",
            ["health-damage", "health-current-change"],
            ["health"],
            ["currentHealth", "health.current", "health.currentHealth"],
            SnapshotDeltaOperator.Decreased,
            "Stable current health decreased."),
        Rule(
            "health-healing",
            ["health-healing", "health-current-change"],
            ["health"],
            ["currentHealth", "health.current", "health.currentHealth"],
            SnapshotDeltaOperator.Increased,
            "Stable current health increased."),
        Rule(
            "health-current-max-change",
            ["health-current-max-change"],
            ["health"],
            ["currentMaxHealth", "health.currentMax", "health.currentMaxHealth"],
            SnapshotDeltaOperator.Changed,
            "Stable maximum health changed."),

        Rule(
            "crystal-gain",
            ["resource-crystal-gain"],
            ["crystals", "resources"],
            ["crystals", "currency.crystals", "resources.crystals"],
            SnapshotDeltaOperator.Increased,
            "Stable crystal balance increased."),
        Rule(
            "crystal-spend",
            ["resource-crystal-spend"],
            ["crystals", "resources"],
            ["crystals", "currency.crystals", "resources.crystals"],
            SnapshotDeltaOperator.Decreased,
            "Stable crystal balance decreased."),

        Rule(
            "equipment-weapon-change",
            ["transaction-equipment-change"],
            ["equipment", "loadout"],
            ["weaponFingerprint", "equipment.weaponFingerprint", "loadout.weaponFingerprint"],
            SnapshotDeltaOperator.Changed,
            "Stable weapon fingerprint changed."),
        Rule(
            "equipment-ability-change",
            ["transaction-equipment-change"],
            ["equipment", "loadout"],
            ["abilityFingerprint", "equipment.abilityFingerprint", "loadout.abilityFingerprint"],
            SnapshotDeltaOperator.Changed,
            "Stable ability fingerprint changed."),
        Rule(
            "equipment-melee-change",
            ["transaction-equipment-change"],
            ["equipment", "loadout"],
            ["meleeFingerprint", "equipment.meleeFingerprint", "loadout.meleeFingerprint"],
            SnapshotDeltaOperator.Changed,
            "Stable melee fingerprint changed."),

        InventoryIncrease(
            "inventory-weapon-mod-count-increase",
            "inventory-weapon-mod-pickup",
            ["weaponModCount", "counts.weaponMods", "weaponMods.count"]),
        InventoryIncrease(
            "inventory-ability-mod-count-increase",
            "inventory-ability-mod-pickup",
            ["abilityModCount", "counts.abilityMods", "abilityMods.count"]),
        InventoryIncrease(
            "inventory-melee-mod-count-increase",
            "inventory-melee-mod-pickup",
            ["meleeModCount", "counts.meleeMods", "meleeMods.count"]),
        InventoryIncrease(
            "inventory-perk-count-increase",
            "inventory-perk-pickup",
            ["perkCount", "counts.perks", "perks.count"]),
        InventoryIncrease(
            "inventory-relic-count-increase",
            "inventory-relic-pickup",
            ["relicCount", "counts.relics", "relics.count"]),

        SlotIncrease(
            "slot-weapon-increase",
            "slot-weapon-increment",
            ["weaponModSlots", "slots.weaponMods", "slotCounts.weaponMods"]),
        SlotIncrease(
            "slot-ability-increase",
            "slot-ability-increment",
            ["abilityModSlots", "slots.abilityMods", "slotCounts.abilityMods"]),
        SlotIncrease(
            "slot-melee-increase",
            "slot-melee-increment",
            ["meleeModSlots", "slots.meleeMods", "slotCounts.meleeMods"]),
        SlotIncrease(
            "slot-perk-increase",
            "slot-perk-increment",
            ["perkSlots", "slots.perks", "slotCounts.perks"]),

        Rule(
            "lifecycle-island-travel",
            ["session-island-travel"],
            ["lifecycle"],
            ["islandFingerprint", "island.fingerprint", "currentIsland"],
            SnapshotDeltaOperator.Changed,
            "Stable island identity changed across a lifecycle transition.",
            SnapshotRuleScopePolicy.LifecycleTransition,
            InRunContexts,
            InRunContexts)
    ];

    private static SnapshotQualificationRule InventoryIncrease(
        string id,
        string checklistId,
        IReadOnlyList<string> fields) => Rule(
        id,
        [checklistId],
        ["inventory", "inventory-counts"],
        fields,
        SnapshotDeltaOperator.Increased,
        "Stable inventory category count increased.");

    private static SnapshotQualificationRule SlotIncrease(
        string id,
        string checklistId,
        IReadOnlyList<string> fields) => Rule(
        id,
        [checklistId],
        ["slots", "inventory-slots"],
        fields,
        SnapshotDeltaOperator.Increased,
        "Stable usable slot count increased.");

    private static SnapshotQualificationRule Rule(
        string id,
        IReadOnlyList<string> checklistIds,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> fields,
        SnapshotDeltaOperator deltaOperator,
        string description,
        SnapshotRuleScopePolicy? scope = null,
        IReadOnlyList<string>? beforeContexts = null,
        IReadOnlyList<string>? afterContexts = null) => new(
        id,
        checklistIds,
        categories,
        fields,
        deltaOperator,
        StableBeforeSamples: 3,
        StableAfterSamples: 3,
        scope ?? SnapshotRuleScopePolicy.SamePlayerScope,
        beforeContexts ?? Array.Empty<string>(),
        afterContexts ?? Array.Empty<string>(),
        description);
}

/// <summary>Loads the same rule model from JSON so qualification policy can evolve without code branches.</summary>
public sealed class SnapshotQualificationRuleLoader
{
    public async Task<IReadOnlyList<SnapshotQualificationRule>> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(json);
    }

    public IReadOnlyList<SnapshotQualificationRule> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rules = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rules", out var nested)
                ? nested
                : default;
        if (rules.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Snapshot rule document must be an array or contain a rules array.");

        var output = new List<SnapshotQualificationRule>();
        foreach (var item in rules.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Every snapshot rule must be an object.");
            var id = Text(item, "id");
            var checklistIds = Strings(item, "checklistIds");
            var categories = Strings(item, "categories");
            var fieldPaths = Strings(item, "fieldPaths");
            if (string.IsNullOrWhiteSpace(id) || checklistIds.Count == 0
                || categories.Count == 0 || fieldPaths.Count == 0)
                throw new InvalidDataException("A snapshot rule requires id, checklistIds, categories, and fieldPaths.");

            var deltaOperator = Normalize(Text(item, "operator")) switch
            {
                "changed" => SnapshotDeltaOperator.Changed,
                "increased" => SnapshotDeltaOperator.Increased,
                "decreased" => SnapshotDeltaOperator.Decreased,
                _ => throw new InvalidDataException($"Snapshot rule {id} has an unsupported operator.")
            };
            var beforeSamples = Integer(item, "stableBeforeSamples", 3);
            var afterSamples = Integer(item, "stableAfterSamples", 3);
            if (beforeSamples < 1 || afterSamples < 1)
                throw new InvalidDataException($"Snapshot rule {id} sample requirements must be positive.");

            var scope = item.TryGetProperty("scope", out var scopeElement)
                        && scopeElement.ValueKind == JsonValueKind.Object
                ? new SnapshotRuleScopePolicy(
                    Boolean(scopeElement, "sameLifecycleGeneration", true),
                    Boolean(scopeElement, "sameWorldFingerprint", true),
                    Boolean(scopeElement, "samePlayerStateFingerprint", true),
                    Boolean(scopeElement, "sameContext", true),
                    Boolean(scopeElement, "sameObservedRole", true),
                    Boolean(scopeElement, "allowUnstableBridge", false))
                : SnapshotRuleScopePolicy.SamePlayerScope;
            output.Add(new SnapshotQualificationRule(
                id,
                checklistIds,
                categories,
                fieldPaths,
                deltaOperator,
                beforeSamples,
                afterSamples,
                scope,
                Strings(item, "beforeContexts"),
                Strings(item, "afterContexts"),
                Text(item, "evidenceDescription", $"Snapshot rule {id} matched.")));
        }

        if (output.Count == 0) throw new InvalidDataException("Snapshot rule document contains no rules.");
        var duplicate = output.GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate snapshot rule id: {duplicate.Key}");
        return output;
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray()
            : Array.Empty<string>();

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int Integer(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static bool Boolean(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
}
