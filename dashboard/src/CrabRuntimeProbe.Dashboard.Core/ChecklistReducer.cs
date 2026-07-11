using System.Text.Json;

namespace CrabRuntimeProbe.Dashboard.Core;

public static class ChecklistCatalog
{
    private static ChecklistDefinition D(
        string id,
        string group,
        string label,
        string instruction,
        bool natural = true,
        params string[] prerequisites) =>
        new(id, group, label, instruction, natural, prerequisites);

    public static IReadOnlyList<ChecklistDefinition> All { get; } = new[]
    {
        D("session.runtime-probe-loaded", "Session & multiplayer", "RuntimeProbe loaded", "Start Crab Champions and wait for the RuntimeProbe heartbeat.", false),
        D("session.fresh-generation", "Session & multiplayer", "Fresh campaign generation created", "Prepare a fresh campaign in the dashboard.", false),
        D("session.local-playerstate-stable", "Session & multiplayer", "Local PlayerState stable", "Enter a lobby or run and wait several seconds.", false, "session.runtime-probe-loaded"),
        D("session.role-determined", "Session & multiplayer", "Role determined", "Host or join the lobby, then wait for stable role detection.", false, "session.local-playerstate-stable"),
        D("session.host-detected", "Session & multiplayer", "Host detected", "On the host computer, create the lobby.", false, "session.role-determined"),
        D("session.joined-client-detected", "Session & multiplayer", "Joined client detected", "On the joined-client computer, join the host.", false, "session.role-determined"),
        D("session.two-visible-players", "Session & multiplayer", "Two or more visible players", "Keep both players in the same lobby or run.", false, "session.role-determined"),
        D("session.cross-client-correlation", "Session & multiplayer", "Host/client evidence correlation established", "Export both bundles and combine them offline.", false, "session.two-visible-players"),
        D("session.stable-multiplayer-run", "Session & multiplayer", "Stable in-run multiplayer sample", "Play together on one island for at least a minute.", false, "session.two-visible-players"),
        D("lifecycle.island-travel", "Session & multiplayer", "Island travel observed", "Complete an island and enter a portal."),
        D("lifecycle.late-join-reconnect", "Session & multiplayer", "Late join or reconnect observed", "Have the joined client leave and reconnect."),
        D("lifecycle.disconnect", "Session & multiplayer", "Disconnect observed", "Have the joined client leave normally."),
        D("lifecycle.death-respawn", "Session & multiplayer", "Death and respawn observed", "Have one player die, then revive or respawn."),

        D("inventory.pickup.weapon-mod", "Inventory", "Weapon mod pickup observed", "Pick up a weapon mod."),
        D("inventory.pickup.ability-mod", "Inventory", "Ability mod pickup observed", "Pick up an ability mod."),
        D("inventory.pickup.melee-mod", "Inventory", "Melee mod pickup observed", "Pick up a melee mod."),
        D("inventory.pickup.perk", "Inventory", "Perk pickup observed", "Pick up a perk."),
        D("inventory.pickup.relic", "Inventory", "Relic pickup observed", "Pick up a relic."),
        D("inventory.duplicate-acquisition", "Inventory", "Duplicate item acquisition observed", "Pick up a second copy of an item."),
        D("inventory.array-counts", "Inventory", "Inventory array counts observed", "Open the inventory after collecting at least one item.", false),
        D("inventory.first-da-identity", "Inventory", "First-element DA identity observed", "Keep at least one item in each category needed for the staged read.", false, "inventory.array-counts"),
        D("inventory.inventoryinfo-parent", "Inventory", "InventoryInfo parent observed", "Keep an item present while the metadata stage runs.", false, "inventory.first-da-identity"),
        D("inventory.level", "Inventory", "Level observed", "Pick up an item, then a duplicate if possible.", false, "inventory.inventoryinfo-parent"),
        D("inventory.accumulated-buff", "Inventory", "AccumulatedBuff observed", "Use an item with an accumulating effect.", false, "inventory.inventoryinfo-parent"),
        D("inventory.enhancements-shape", "Inventory", "Enhancements shape observed", "Keep an enhanceable item present.", false, "inventory.inventoryinfo-parent"),
        D("inventory.enhancements-values", "Inventory", "Enhancements values observed", "Use an anvil on an item.", false, "inventory.enhancements-shape"),
        D("inventory.capped-iteration", "Inventory", "Capped full inventory iteration", "Collect several items across all five categories.", false, "inventory.first-da-identity"),
        D("inventory.duplicate-semantics", "Inventory", "Duplicate semantics captured", "Pick up a second copy of an item.", false, "inventory.duplicate-acquisition", "inventory.level"),
        D("inventory.slot-index-stability", "Inventory", "Slot/index stability captured", "Pick up, drop, and reorder items across an island transition.", false, "inventory.capped-iteration"),
        D("inventory.joined-client-reads", "Inventory", "Joined-client inventory reads", "Repeat the proven reads on the joined-client computer.", false, "session.joined-client-detected", "inventory.first-da-identity"),
        D("inventory.remote-visibility", "Inventory", "Remote inventory visibility checked", "Keep both players nearby with different items.", false, "session.two-visible-players"),

        D("transaction.server-interact", "Transactions", "ServerInteract observed with pickup", "Interact with a pickup pedestal."),
        D("transaction.server-auto-loot", "Transactions", "ServerAutoLoot observed", "Trigger natural chest auto-loot if available."),
        D("transaction.client-picked-up", "Transactions", "ClientOnPickedUpPickup observed", "Pick up any inventory item."),
        D("transaction.onrep-inventory", "Transactions", "OnRep_Inventory observed", "Have the joined client pick up an item."),
        D("transaction.server-drop", "Transactions", "ServerDropPickup observed", "Drop an item from the inventory."),
        D("transaction.typed-removal", "Transactions", "Typed inventory removal observed", "Drop or replace one item of each relevant type."),
        D("transaction.upgrade-totem", "Transactions", "Upgrade-totem purchase observed", "Purchase an upgrade from an upgrade totem."),
        D("transaction.server-enhancement", "Transactions", "ServerApplyEnhancement observed", "Use an anvil."),
        D("transaction.multicast-enhancement", "Transactions", "MulticastApplyEnhancement observed", "Use an anvil while both players are present."),
        D("transaction.equipment-change", "Transactions", "Equipment change observed", "Replace your weapon, ability, or melee equipment."),
        D("transaction.equipment-rpc", "Transactions", "ServerEquipInventory or ServerSet* observed", "Choose new equipment through normal gameplay."),
        D("transaction.salvage", "Transactions", "Salvage observed", "Salvage a pickup."),
        D("transaction.shop-chest", "Transactions", "Shop/chest interaction observed", "Buy a shop item and open a chest."),

        D("resource.crystal-gain", "Resources & slots", "Crystal gain observed", "Collect crystal drops or a crystal reward."),
        D("resource.crystal-spend", "Resources & slots", "Crystal spending observed", "Buy an item, chest, slot, or totem upgrade."),
        D("resource.onrep-crystals", "Resources & slots", "OnRep_Crystals observed", "Gain and then spend crystals."),
        D("slots.weapon-increment", "Resources & slots", "Weapon-mod slot increment", "Purchase a weapon-mod slot."),
        D("slots.ability-increment", "Resources & slots", "Ability-mod slot increment", "Purchase an ability-mod slot."),
        D("slots.melee-increment", "Resources & slots", "Melee-mod slot increment", "Purchase a melee-mod slot."),
        D("slots.perk-increment", "Resources & slots", "Perk slot increment", "Purchase a perk slot."),
        D("slots.rpc-arguments", "Resources & slots", "Slot increment arguments captured", "Purchase any inventory slot."),
        D("slots.cost-behavior", "Resources & slots", "Cost behavior captured", "Record crystals before and after a slot purchase.", false, "slots.rpc-arguments"),
        D("slots.pre-post-values", "Resources & slots", "Slot pre/post values captured", "Purchase any inventory slot.", false, "slots.rpc-arguments"),

        D("health.damage", "Health", "Damage observed", "Take damage."),
        D("health.healing", "Health", "Healing observed", "Take damage, then heal."),
        D("health.current-change", "Health", "Current health change observed", "Take damage, then heal.", false),
        D("health.current-max-change", "Health", "Current max-health change observed", "Pick up a max-health modifier.", false),
        D("health.base-max-change", "Health", "Base max-health change observed", "Pick up a max-health modifier.", false),
        D("health.multiplier-change", "Health", "Max-health multiplier change observed", "Use a max-health multiplier item.", false),
        D("health.armor", "Health", "Armor state observed", "Gain armor, then take armor damage.", false),
        D("health.death", "Health", "Death observed", "Allow one player to be eliminated."),
        D("health.respawn", "Health", "Respawn observed", "Revive or respawn after elimination."),
        D("health.playerstate-scope", "Health", "PlayerState-scoped health confirmed", "Wait for a stable PlayerState health sample.", false),
        D("health.no-unscoped-crabhc", "Health", "No unscoped CrabHC used", "No action required; this is a safety assertion.", false)
    };
}

public sealed class ChecklistReducer
{
    private static readonly HashSet<string> NonQualifyingKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "hook-registration", "hook_registered", "registration", "function-presence", "objectdump-only"
    };

    private readonly IReadOnlyList<ChecklistDefinition> _definitions;

    public ChecklistReducer(IReadOnlyList<ChecklistDefinition>? definitions = null)
    {
        _definitions = definitions is { Count: > 0 } ? definitions : ChecklistCatalog.All;
    }

    public IReadOnlyList<ChecklistViewItem> Reduce(LiveStatusSnapshot snapshot)
    {
        var definitions = _definitions.Concat(
                snapshot.Checklist.Keys
                    .Where(id => !_definitions.Any(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    .Select(id => new ChecklistDefinition(
                        id,
                        "Discovered / uncatalogued",
                        id,
                        "Review and classify this runtime-discovered checklist candidate.",
                        true,
                        Array.Empty<string>())))
            .ToArray();
        var provisional = new Dictionary<string, ChecklistViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            snapshot.Checklist.TryGetValue(definition.Id, out var evidence);
            provisional[definition.Id] = Create(definition, evidence);
        }

        // Prerequisites affect only rows that have not themselves collected evidence.
        foreach (var definition in definitions.Where(item => item.Prerequisites.Count > 0))
        {
            var current = provisional[definition.Id];
            if (current.State is not ChecklistDisplayState.NotObserved)
            {
                continue;
            }

            if (definition.Prerequisites.Any(id =>
                    !provisional.TryGetValue(id, out var prerequisite) || !prerequisite.IsComplete))
            {
                provisional[definition.Id] = current with
                {
                    State = ChecklistDisplayState.BlockedByPrerequisite,
                    Detail = "Waiting for prerequisite evidence."
                };
            }
        }

        return definitions.Select(definition => provisional[definition.Id]).ToArray();
    }

    public double CompletionPercentage(IReadOnlyList<ChecklistViewItem> items)
    {
        var applicable = items.Where(item => item.State is not ChecklistDisplayState.NotApplicable).ToArray();
        return applicable.Length == 0
            ? 0
            : Math.Round(applicable.Count(item => item.IsComplete) * 100d / applicable.Length, 1);
    }

    private static ChecklistViewItem Create(ChecklistDefinition definition, ChecklistEvidence? evidence)
    {
        if (evidence is null)
        {
            return new ChecklistViewItem(
                definition,
                ChecklistDisplayState.NotObserved,
                0,
                null,
                null,
                string.Empty,
                string.Empty,
                definition.Instruction,
                string.Empty);
        }

        var state = StateFor(definition, evidence);
        return new ChecklistViewItem(
            definition,
            state,
            evidence.ObservationCount,
            evidence.FirstObservedAtUtc,
            evidence.LastObservedAtUtc,
            string.Join(", ", evidence.SourceRoles),
            string.Join(", ", evidence.EvidenceSessions),
            string.IsNullOrWhiteSpace(evidence.NextInstruction) ? definition.Instruction : evidence.NextInstruction,
            evidence.Detail);
    }

    private static ChecklistDisplayState StateFor(ChecklistDefinition definition, ChecklistEvidence evidence)
    {
        if (evidence.CrashSuspect) return ChecklistDisplayState.CrashSuspect;
        if (evidence.DirtyEvidence) return ChecklistDisplayState.DirtyEvidence;

        var reported = Normalize(evidence.ReportedStatus);
        if (reported is "unsupported") return ChecklistDisplayState.Unsupported;
        if (reported is "not-applicable" or "na") return ChecklistDisplayState.NotApplicable;
        if (reported.StartsWith("blocked", StringComparison.Ordinal)) return ChecklistDisplayState.BlockedByPrerequisite;

        var hasNonRegistrationEvidence = evidence.EvidenceKinds.Any(kind => !NonQualifyingKinds.Contains(kind));
        var hasNaturalEvidence = evidence.EvidenceKinds.Any(kind =>
            Normalize(kind) is "natural-call" or "natural-event" or "natural-property-change"
                or "state-transition" or "pre-post" or "natural-observation");
        var qualifies = evidence.ObservationCount > 0
            && (evidence.QualifyingEvidence || hasNonRegistrationEvidence)
            && (!definition.RequiresNaturalEvidence || evidence.QualifyingEvidence || hasNaturalEvidence);

        if (reported is "confirmed" or "complete")
        {
            if (qualifies) return ChecklistDisplayState.Confirmed;
            return evidence.HookRegistered && evidence.ObservationCount == 0
                ? ChecklistDisplayState.InProgress
                : ChecklistDisplayState.Partial;
        }

        if (reported is "partial") return ChecklistDisplayState.Partial;
        if (reported is "in-progress" or "observing") return ChecklistDisplayState.InProgress;
        if (qualifies) return ChecklistDisplayState.Confirmed;
        if (evidence.ObservationCount > 0) return ChecklistDisplayState.Partial;
        if (evidence.HookRegistered) return ChecklistDisplayState.InProgress;
        return ChecklistDisplayState.NotObserved;
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
}

public sealed class ChecklistDefinitionLoader
{
    public async Task<IReadOnlyList<ChecklistDefinition>> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items)
                ? items
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("checklist", out var checklist)
                    ? checklist
                    : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var entries)
                        ? entries
                    : default;
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Checklist definition must contain an items/checklist array.");

        var output = new List<ChecklistDefinition>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var id = Text(element, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var prerequisites = element.TryGetProperty("prerequisites", out var prerequisiteElement)
                                && prerequisiteElement.ValueKind == JsonValueKind.Array
                ? prerequisiteElement.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString() ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .ToArray()
                : Array.Empty<string>();
            output.Add(new ChecklistDefinition(
                id,
                Text(element, "group", Text(element, "section", "Uncategorized")),
                Text(element, "label", id),
                Text(element, "instruction", Text(element, "nextInstruction",
                    Text(element, "nextAction", "Perform the documented gameplay action."))),
                Bool(element, "requiresNaturalEvidence",
                    !Text(element, "completionRule", "qualifying-evidence")
                        .Equals("status-only", StringComparison.OrdinalIgnoreCase)),
                prerequisites));
        }

        if (output.Count == 0) throw new InvalidDataException("Checklist definition contains no items.");
        var duplicate = output.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate checklist id: {duplicate.Key}");
        return output;
    }

    public async Task<IReadOnlyList<ChecklistDefinition>> LoadAuthoritativeOrFallbackAsync(
        DashboardResources resources,
        CancellationToken cancellationToken = default)
    {
        var candidates = new[]
        {
            Path.Combine(resources.CampaignRoot, "crabsync-full-observe.checklist.json"),
            Path.Combine(resources.CampaignRoot, "checklist.crabsync-full-observe.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        return path is null ? ChecklistCatalog.All : await LoadAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool Bool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
