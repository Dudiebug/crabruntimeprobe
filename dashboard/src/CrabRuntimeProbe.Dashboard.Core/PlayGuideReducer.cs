namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class PlayGuideReducer
{
    private sealed record CategoryDefinition(string Id, string Name, int Order);
    private sealed record ActionDefinition(
        string Id,
        string CategoryId,
        int Order,
        string Title,
        string Instruction,
        bool Automatic = false,
        IReadOnlyList<SubtaskDefinition>? Subtasks = null);
    private sealed record SubtaskDefinition(string ChecklistId, string Label);

    private enum SignalState
    {
        ToDo,
        InProgress,
        Done,
        Waiting,
        Retry,
        Missing
    }

    private static readonly IReadOnlyList<CategoryDefinition> Categories = new[]
    {
        new CategoryDefinition("ready", "Get ready together", 1),
        new CategoryDefinition("pickups", "Pickups and inventory", 2),
        new CategoryDefinition("anvil", "Anvil and equipment", 3),
        new CategoryDefinition("shops", "Shops, chests, and totems", 4),
        new CategoryDefinition("crystals", "Crystals and inventory slots", 5),
        new CategoryDefinition("health", "Health and respawning", 6),
        new CategoryDefinition("world", "Islands, portals, and saves", 7),
        new CategoryDefinition("multiplayer", "Multiplayer and reconnect checks", 8),
        new CategoryDefinition("automatic", "Watching automatically", 9)
    };

    private static readonly IReadOnlyList<ActionDefinition> Actions = new[]
    {
        A("fresh-run", "ready", 1, "Start a fresh run",
            "Prepare on both computers, launch Crab Champions, and begin a fresh run before collecting anything."),
        A("same-lobby", "ready", 2, "Play together in the same lobby",
            "One player creates the lobby and the other joins. Stay together until both guides settle."),

        A("power-ups", "pickups", 1, "Pick up every power-up type",
            "Between both players, pick up one of each type. These chips update automatically.", false,
            S("inventory-weapon-mod-pickup", "Weapon mod"),
            S("inventory-ability-mod-pickup", "Ability mod"),
            S("inventory-melee-mod-pickup", "Melee mod"),
            S("inventory-perk-pickup", "Perk"),
            S("inventory-relic-pickup", "Relic")),
        A("duplicate", "pickups", 2, "Pick up a duplicate item",
            "Pick up a second copy and, if possible, collect items of different rarities."),
        A("drop-salvage", "pickups", 3, "Drop and salvage items",
            "Drop an item, salvage an offered pickup, and remove different item types when practical."),
        A("inventory-watch", "pickups", 4, "Carry a few items while we check them",
            "Keep items in all five categories on both players and continue playing together.", true),

        A("anvil-use", "anvil", 1, "Use an anvil",
            "Upgrade an item while both players are nearby, then keep the upgraded item."),
        A("equipment", "anvil", 2, "Equip or replace an item",
            "Replace a weapon, ability, melee item, or other inventory item through normal play."),

        A("chest", "shops", 1, "Open a chest",
            "Open a chest with both players present and let all of its pickups settle."),
        A("shop-reroll", "shops", 2, "Buy from a shop and reroll",
            "Buy a shop item, then reroll a shop or reward choice when the run offers one."),
        A("upgrade-totem", "shops", 3, "Buy an upgrade from a totem",
            "Purchase an upgrade from a totem and wait for the upgrade to finish."),

        A("earn-crystals", "crystals", 1, "Earn crystals",
            "Collect crystals from combat, a drop, or an island reward."),
        A("spend-crystals", "crystals", 2, "Spend crystals",
            "Buy something while the joining player is present, then wait for the display to update."),
        A("buy-slots", "crystals", 3, "Buy each slot type",
            "Buy weapon mod, ability mod, melee mod, and perk slots, then keep playing through travel and reconnecting.", false,
            S("slot-weapon-increment", "Weapon mod slot"),
            S("slot-ability-increment", "Ability mod slot"),
            S("slot-melee-increment", "Melee mod slot"),
            S("slot-perk-increment", "Perk slot")),

        A("damage-heal", "health", 1, "Take damage, then heal and regenerate",
            "Take ordinary combat damage, heal, and let natural regeneration trigger if available."),
        A("defenses", "health", 2, "Test armor, shields, and max health",
            "Use these only if the run naturally offers them. Missing shield support can resolve safely."),
        A("death-respawn", "health", 3, "Die, revive if supported, and respawn",
            "Have one player die. Revive them if supported, then confirm a normal respawn."),
        A("out-of-bounds", "health", 4, "Test out-of-bounds recovery safely",
            "Use a safe recovery area only—never risk the run—to trigger normal out-of-bounds recovery."),

        A("island-reward", "world", 1, "Complete an island and select a reward",
            "Finish an island and choose the next reward with both players present."),
        A("portal", "world", 2, "Enter a portal",
            "Choose and enter a portal, then wait for both players to load the next island."),
        A("save-restore", "world", 3, "Save and restore a run",
            "Continue or restore a run normally, then compare what remains after travel or reconnecting."),

        A("reconnect", "multiplayer", 1, "Disconnect and reconnect",
            "After a stable sample, the joining player leaves normally and reconnects once."),
        A("compare-results", "multiplayer", 2, "Finish together and compare results",
            "Finish and save on both computers. Test host migration only if the game naturally supports it."),

        A("safety", "automatic", 1, "Safety checks",
            "No action is required. The guide keeps unsafe paths disabled while you play.", true),
        A("other-automatic", "automatic", 2, "Other tasks we’re watching automatically",
            "Keep playing normally. These background checks cover game follow-up and remaining evidence coverage.", true)
    };

    private static readonly IReadOnlyDictionary<string, string> ChecklistToAction = BuildChecklistMap();
    private static readonly IReadOnlyDictionary<string, string> LegacyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["transaction.server-interact"] = "transaction-server-interact",
            ["health.damage"] = "health-damage"
        };

    public IReadOnlyCollection<string> CanonicalChecklistIds => ChecklistToAction.Keys.ToArray();
    public int ActionCount => Actions.Count;
    public int CategoryCount => Categories.Count;

    public IReadOnlyList<PlayGuideCategory> Reduce(
        IReadOnlyList<ChecklistViewItem> checklist,
        CampaignRole selectedRole,
        EvidenceCleanliness cleanliness = EvidenceCleanliness.Clean)
    {
        var grouped = checklist
            .GroupBy(item => Canonicalize(item.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ChecklistViewItem>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var projected = Actions
            .OrderBy(item => Categories.Single(category => category.Id == item.CategoryId).Order)
            .ThenBy(item => item.Order)
            .Select(definition => ProjectAction(definition, grouped, selectedRole, cleanliness))
            .ToList();

        var unmapped = checklist
            .Where(item => !ChecklistToAction.ContainsKey(Canonicalize(item.Id)))
            .OrderBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unmapped.Length > 0)
        {
            var index = projected.FindIndex(item => item.Id == "other-automatic");
            var current = projected[index];
            var extras = unmapped.Select((item, itemIndex) => new PlayGuideSubtask(
                SafeUnmappedLabel(item, itemIndex + 1),
                cleanliness is EvidenceCleanliness.Dirty or EvidenceCleanliness.CrashSuspect
                    ? PlayGuideDisplayState.Retry
                    : ToDisplayState(SignalFor(new[] { item })))).ToArray();
            var state = cleanliness is EvidenceCleanliness.Dirty or EvidenceCleanliness.CrashSuspect
                ? PlayGuideDisplayState.Retry
                : AggregateActionState(
                    current.LinkedChecklistIds.Select(id => SignalForId(id, grouped))
                        .Concat(extras.Select(item => ToSignalState(item.State))),
                    automatic: true);
            projected[index] = current with
            {
                Instruction = $"{unmapped.Length} additional automatic check{(unmapped.Length == 1 ? " was" : "s were")} added. Keep playing normally; nothing is hidden.",
                State = state,
                Subtasks = current.Subtasks.Concat(extras).ToArray()
            };
        }

        return Categories.OrderBy(category => category.Order).Select(category =>
        {
            var actions = projected.Where(action => action.CategoryId == category.Id).ToArray();
            var completed = actions.Count(action => action.IsDone);
            var percentage = actions.Length == 0 ? 0 : Math.Round(completed * 100d / actions.Length);
            var next = actions.FirstOrDefault(action => !action.IsDone);
            var recommendation = next is null
                ? "All tasks in this section are done. Keep playing normally."
                : $"Next: {next.Title} — {next.Instruction}";
            return new PlayGuideCategory(
                category.Id, category.Name, completed, actions.Length, percentage, recommendation, actions);
        }).ToArray();
    }

    public static bool MatchesFilter(PlayGuideAction action, PlayGuideFilter filter) => filter switch
    {
        PlayGuideFilter.Completed => action.IsDone,
        PlayGuideFilter.All => true,
        _ => !action.IsDone
    };

    private static PlayGuideAction ProjectAction(
        ActionDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<ChecklistViewItem>> grouped,
        CampaignRole selectedRole,
        EvidenceCleanliness cleanliness)
    {
        var linkedIds = ChecklistToAction
            .Where(pair => pair.Value == definition.Id)
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiredIds = RoleAdjusted(linkedIds, selectedRole).ToArray();
        var signals = requiredIds.Select(id => SignalForId(id, grouped)).ToArray();
        var mappingWarning = signals.Any(state => state == SignalState.Missing);
        var state = cleanliness is EvidenceCleanliness.Dirty or EvidenceCleanliness.CrashSuspect
            ? PlayGuideDisplayState.Retry
            : AggregateActionState(signals, definition.Automatic);
        var subtasks = (definition.Subtasks ?? Array.Empty<SubtaskDefinition>())
            .Select(subtask => new PlayGuideSubtask(
                subtask.Label,
                cleanliness is EvidenceCleanliness.Dirty or EvidenceCleanliness.CrashSuspect
                    ? PlayGuideDisplayState.Retry
                    : ToDisplayState(SignalForId(subtask.ChecklistId, grouped))))
            .ToArray();
        var instruction = mappingWarning
            ? "A required campaign check is missing. Restart the dashboard, then open Advanced if RETRY remains."
            : definition.Instruction;
        return new PlayGuideAction(
            definition.Id,
            definition.CategoryId,
            definition.Title,
            instruction,
            state,
            requiredIds,
            subtasks,
            definition.Automatic,
            mappingWarning);
    }

    private static IEnumerable<string> RoleAdjusted(IEnumerable<string> ids, CampaignRole role)
    {
        foreach (var id in ids)
        {
            if (role == CampaignRole.Host && id == "session-joined-client-detected") continue;
            if (role == CampaignRole.JoinedClient && id == "session-host-detected") continue;
            yield return id;
        }
    }

    private static SignalState SignalForId(
        string id,
        IReadOnlyDictionary<string, IReadOnlyList<ChecklistViewItem>> grouped) =>
        grouped.TryGetValue(id, out var items) ? SignalFor(items) : SignalState.Missing;

    private static SignalState SignalFor(IEnumerable<ChecklistViewItem> items)
    {
        var states = items.Select(item => item.State).ToArray();
        if (states.Any(state => state is ChecklistDisplayState.DirtyEvidence or ChecklistDisplayState.CrashSuspect))
            return SignalState.Retry;
        if (states.Any(state => state is ChecklistDisplayState.Confirmed
                or ChecklistDisplayState.Unsupported or ChecklistDisplayState.NotApplicable))
            return SignalState.Done;
        if (states.Any(state => state is ChecklistDisplayState.InProgress or ChecklistDisplayState.Partial))
            return SignalState.InProgress;
        if (states.Any(state => state == ChecklistDisplayState.BlockedByPrerequisite))
            return SignalState.Waiting;
        return SignalState.ToDo;
    }

    private static PlayGuideDisplayState AggregateActionState(
        IEnumerable<SignalState> input,
        bool automatic)
    {
        var states = input.ToArray();
        if (states.Length == 0 || states.Any(state => state is SignalState.Retry or SignalState.Missing))
            return PlayGuideDisplayState.Retry;
        if (states.All(state => state == SignalState.Done)) return PlayGuideDisplayState.Done;
        if (states.Any(state => state is SignalState.InProgress or SignalState.Done))
            return PlayGuideDisplayState.InProgress;
        if (states.Any(state => state == SignalState.Waiting) || automatic)
            return PlayGuideDisplayState.Waiting;
        return PlayGuideDisplayState.ToDo;
    }

    private static PlayGuideDisplayState ToDisplayState(SignalState state) => state switch
    {
        SignalState.Done => PlayGuideDisplayState.Done,
        SignalState.InProgress => PlayGuideDisplayState.InProgress,
        SignalState.Waiting => PlayGuideDisplayState.Waiting,
        SignalState.Retry or SignalState.Missing => PlayGuideDisplayState.Retry,
        _ => PlayGuideDisplayState.ToDo
    };

    private static SignalState ToSignalState(PlayGuideDisplayState state) => state switch
    {
        PlayGuideDisplayState.Done => SignalState.Done,
        PlayGuideDisplayState.InProgress => SignalState.InProgress,
        PlayGuideDisplayState.Waiting => SignalState.Waiting,
        PlayGuideDisplayState.Retry => SignalState.Retry,
        _ => SignalState.ToDo
    };

    private static string Canonicalize(string id) => LegacyAliases.TryGetValue(id, out var canonical)
        ? canonical
        : id;

    private static string SafeUnmappedLabel(ChecklistViewItem item, int index)
    {
        var text = $"{item.Label} {item.Instruction}";
        var technical = new[]
        {
            "playerstate", "gamestate", "onrep", "rpc", "server", "multicast", "canonical",
            "terminal disposition", "uobject", "crabhc", "crabps", "dataasset", "hook", "coverage",
            "property", "uncatalogued"
        };
        return item.Label.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
               || technical.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? $"Additional automatic check {index}"
            : item.Label;
    }

    private static ActionDefinition A(
        string id,
        string categoryId,
        int order,
        string title,
        string instruction,
        bool automatic = false,
        params SubtaskDefinition[] subtasks) =>
        new(id, categoryId, order, title, instruction, automatic, subtasks);

    private static SubtaskDefinition S(string checklistId, string label) => new(checklistId, label);

    private static IReadOnlyDictionary<string, string> BuildChecklistMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string actionId, params string[] ids)
        {
            foreach (var id in ids)
            {
                if (!map.TryAdd(id, actionId))
                    throw new InvalidOperationException($"Duplicate Play Guide checklist mapping: {id}");
            }
        }

        Add("fresh-run", "session-runtimeprobe-loaded", "session-fresh-generation", "session-local-playerstate-stable", "inventory-starting-defaults");
        Add("same-lobby", "session-role-determined", "session-host-detected", "session-joined-client-detected", "session-two-visible-players", "session-stable-multiplayer-sample");

        Add("power-ups", "inventory-weapon-mod-pickup", "inventory-ability-mod-pickup", "inventory-melee-mod-pickup", "inventory-perk-pickup", "inventory-relic-pickup");
        Add("duplicate", "inventory-duplicate-acquisition", "inventory-level", "inventory-duplicate-semantics", "inventory-rarity-cooldown-stack");
        Add("drop-salvage", "inventory-order-index-stability", "transaction-drop", "transaction-typed-removal", "transaction-salvage");
        Add("inventory-watch", "inventory-array-counts", "inventory-first-da-identity", "inventory-info-parent", "inventory-accumulated-buff", "inventory-capped-iteration", "inventory-joined-client-reads", "inventory-remote-visibility", "transaction-server-interact", "transaction-client-picked-up", "transaction-onrep-inventory", "transaction-pickup-ownership");

        Add("anvil-use", "inventory-enhancements-shape", "inventory-enhancements-values", "transaction-anvil", "transaction-server-apply-enhancement", "transaction-multicast-enhancement");
        Add("equipment", "transaction-replacement", "transaction-equipment-change", "transaction-official-equipment-rpc");

        Add("chest", "transaction-server-autoloot", "transaction-multi-pickup-replication", "world-chest");
        Add("shop-reroll", "transaction-reroll", "world-shop");
        Add("upgrade-totem", "transaction-upgrade-totem");

        Add("earn-crystals", "resource-crystal-gain", "resource-crystal-drop-reward");
        Add("spend-crystals", "resource-crystal-spend", "resource-onrep-crystals");
        Add("buy-slots", "slot-weapon-increment", "slot-ability-increment", "slot-melee-increment", "slot-perk-increment", "slot-increment-arguments", "slot-cost-behavior", "slot-pre-post", "slot-locked-usable-max", "slot-persistence");

        Add("damage-heal", "health-damage", "health-healing", "health-current-change", "health-regeneration", "health-playerstate-scoped");
        Add("defenses", "health-current-max-change", "health-base-max-change", "health-max-multiplier-change", "health-armor", "health-shield");
        Add("death-respawn", "session-death-respawn", "health-elimination-death", "health-revival", "health-respawn");
        Add("out-of-bounds", "health-out-of-bounds");

        Add("island-reward", "world-island-completion", "world-reward-selection");
        Add("portal", "session-island-travel", "world-portal");
        Add("save-restore", "world-save-restore", "world-persistence");

        Add("reconnect", "session-late-join-reconnect", "session-disconnect", "session-join-leave");
        Add("compare-results", "session-host-client-correlation", "session-host-migration", "ownership-playerstate-gamestate");

        Add("safety", "resource-crystal-range-overflow", "health-no-unscoped-hc", "policy-keys-excluded", "policy-unsafe-paths-rejected");
        Add("other-automatic", "world-ui-follow-up", "official-apply-candidates-observed",
            "coverage-inventory-enhancements", "coverage-inventory-metadata", "coverage-inventory-slots",
            "coverage-crystals-economy", "coverage-health-armor", "coverage-pickups-transactions",
            "coverage-equipment-starting", "coverage-shops-chests-totems", "coverage-portal-island-lifecycle",
            "coverage-save-persistence", "coverage-multiplayer-ownership", "coverage-replication-rpc-events",
            "coverage-ui-follow-up", "coverage-weapons-abilities-melee", "coverage-inventory-items",
            "coverage-player-runtime-state");
        return map;
    }
}
