using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    /// <summary>
    /// Adds readiness signals to a battle dict:
    ///  - is_action_queue_empty: RunManager.ActionQueueSet.IsEmpty (no queued/executing/paused game actions)
    ///  - is_action_running: ActionExecutor.IsRunning (an action is mid-execution)
    ///  - player_actions_disabled: CombatManager.PlayerActionsDisabled (set when end turn is pressed,
    ///    cleared at the start of the next player turn)
    ///  - player_phase: local player's PlayerTurnPhase (None/Start/AutoPrePlay/Play/AutoPostPlay/End)
    ///  - ready_for_input: the local player can play a card / potion / end turn right now. The hand is
    ///    fully dealt (phase Play is only entered after the start-of-turn draw and auto-pre-play hooks),
    ///    the turn loop has handed control to the player (synchronizer in PlayPhase), no action is queued
    ///    or running, and no in-hand card selection is open.
    /// </summary>
    private static void AddBattleReadiness(Dictionary<string, object?> battle, Player? me)
    {
        var manager = CombatManager.Instance;
        var run = RunManager.Instance;

        bool queueEmpty = true;
        try { queueEmpty = run.ActionQueueSet?.IsEmpty ?? true; } catch { }
        bool actionRunning = false;
        try { actionRunning = run.ActionExecutor?.IsRunning ?? false; } catch { }
        bool actionsDisabled = manager.PlayerActionsDisabled;
        var phase = me?.PlayerCombatState?.Phase ?? PlayerTurnPhase.None;
        bool syncPlayPhase = false;
        try { syncPlayPhase = run.ActionQueueSynchronizer?.CombatState == ActionSynchronizerCombatState.PlayPhase; } catch { }
        bool inHandSelection = NPlayerHand.Instance?.IsInCardSelection ?? false;

        battle["is_action_queue_empty"] = queueEmpty;
        battle["is_action_running"] = actionRunning;
        battle["player_actions_disabled"] = actionsDisabled;
        battle["player_phase"] = phase.ToString();
        battle["ready_for_input"] = manager.IsInProgress
            && IsPlayPhase(me)
            && syncPlayPhase
            && !actionsDisabled
            && queueEmpty
            && !actionRunning
            && !inHandSelection
            && !run.IsPaused;
    }

    private static void AddHandLimits(Dictionary<string, object?> state, int handCount)
    {
        state["hand_size"] = handCount;
        state["max_hand_size"] = CardPile.MaxCardsInHand;
        state["hand_full"] = handCount >= CardPile.MaxCardsInHand;
    }

    private static List<Dictionary<string, object?>> BuildDeckList(Player player)
    {
        var list = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var card in player.Deck.Cards)
        {
            string? cost = null;
            try { cost = GetCostDisplay(card); } catch { }
            string? starCost = null;
            try { starCost = GetStarCostDisplay(card); } catch { }

            var entry = new Dictionary<string, object?>
            {
                ["index"] = index++,
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["rarity"] = card.Rarity.ToString(),
                ["cost"] = cost,
                ["star_cost"] = starCost,
                ["is_upgraded"] = card.IsUpgraded,
                ["enchantment"] = null,
                ["affliction"] = null
            };

            var enchant = card.Enchantment;
            if (enchant != null)
            {
                entry["enchantment"] = new Dictionary<string, object?>
                {
                    ["id"] = enchant.Id.Entry,
                    ["name"] = SafeGetText(() => enchant.Title),
                    ["amount"] = enchant.Amount
                };
            }

            var affliction = card.Affliction;
            if (affliction != null)
            {
                entry["affliction"] = new Dictionary<string, object?>
                {
                    ["id"] = affliction.Id.Entry,
                    ["name"] = SafeGetText(() => affliction.Title)
                };
            }

            list.Add(entry);
        }
        return list;
    }

    /// <summary>
    /// Adds per-node markers from MapPoint.Quests - models that tagged the node, e.g. the Fur Coat
    /// relic (every enemy in that fight starts at 1 HP) or the Spoils Map card (buried treasure).
    /// Always sets "markers" (possibly empty); sets "enemies_one_hp" when Fur Coat marked the node.
    /// </summary>
    private static void AddMapPointMarkers(Dictionary<string, object?> node, MapPoint? pt)
    {
        var markers = new List<Dictionary<string, object?>>();
        bool furCoat = false;
        if (pt != null)
        {
            foreach (var quest in pt.Quests)
            {
                if (quest == null) continue;
                string? name = quest switch
                {
                    RelicModel r => SafeGetText(() => r.Title),
                    CardModel c => SafeGetText(() => c.Title),
                    _ => null
                };
                string source = quest switch
                {
                    RelicModel => "relic",
                    CardModel => "card",
                    _ => quest.GetType().Name
                };
                string? effect = quest switch
                {
                    FurCoat => "enemies_one_hp",
                    SpoilsMap => "buried_treasure",
                    _ => null
                };
                if (quest is FurCoat) furCoat = true;
                markers.Add(new Dictionary<string, object?>
                {
                    ["id"] = quest.Id.Entry,
                    ["name"] = name,
                    ["source"] = source,
                    ["effect"] = effect
                });
            }
        }
        node["markers"] = markers;
        if (furCoat)
            node["enemies_one_hp"] = true;
    }
}
