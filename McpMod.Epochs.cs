using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;

namespace STS2_MCP;

/// <summary>
/// Epoch (Timeline) reveal support.
///
/// Game model: ProgressState.Epochs holds SerializableEpoch {Id, State}. A finished run can
/// obtain epochs (State Obtained / ObtainedNoSlot). SaveManager.GetRevealableEpochs() is the set
/// the player still has to reveal; while it is non-empty and there is no run save,
/// NMainMenu.UpdateTimelineButtonBehavior disables Singleplayer / Multiplayer / Compendium and
/// only the Timeline button stays enabled, and NTimelineScreen keeps its back button disabled.
///
/// Player path (replayed here through the real nodes, one step per call):
///   main menu _timelineButton (NMainMenu.OpenTimelineScreen -> SubmenuStack.PushSubmenuType)
///   -> NEpochSlot (State Obtained) click: NEpochSlot.OnRelease -> RevealEpoch ->
///      SaveManager.RevealEpoch + NTimelineScreen.OpenInspectScreen(playAnimation: true)
///   -> NEpochInspectScreen.UnlockAnimation: epoch.QueueUnlocks() (queues NUnlock*Screen and may
///      UnlockSlot() timeline-expansion epochs) + SaveManager.SaveProgressFile(), then enables
///      its NCloseButton
///   -> NCloseButton click: NEpochInspectScreen.Close -> OpenQueuedScreen() for each queued
///      unlock screen, each closed via its NUnlockConfirmButton (NUnlockTimelineScreen closes itself)
///   -> once nothing is revealable the Timeline back button is enabled -> back to the main menu.
/// </summary>
public static partial class McpMod
{
    private static readonly Regex EpochCharacterRegex =
        new(@"^(IRONCLAD|SILENT|DEFECT|REGENT|NECROBINDER)\d+_EPOCH$", RegexOptions.Compiled);

    private static List<SerializableEpoch> GetRevealableEpochsSafe()
    {
        try
        {
            return SaveManager.Instance?.GetRevealableEpochs()?.ToList() ?? new List<SerializableEpoch>();
        }
        catch
        {
            return new List<SerializableEpoch>();
        }
    }

    private static Dictionary<string, object?> BuildEpochInfo(string id, string? state)
    {
        var info = new Dictionary<string, object?> { ["id"] = id };
        try
        {
            var model = EpochModel.Get(id);
            info["title"] = SafeGetText(() => model.Title);
        }
        catch
        {
            info["title"] = null;
        }
        var match = EpochCharacterRegex.Match(id);
        info["character"] = match.Success ? match.Groups[1].Value : null;
        if (state != null)
        {
            info["state"] = state;
            // Obtained epochs already have a Timeline slot to click; ObtainedNoSlot ones get a slot
            // when the epoch that expands the timeline to them is revealed.
            info["has_slot"] = state == nameof(EpochState.Obtained);
        }
        return info;
    }

    private static List<Dictionary<string, object?>> BuildPendingEpochs()
    {
        return GetRevealableEpochsSafe()
            .Select(e => BuildEpochInfo(e.Id, e.State.ToString()))
            .ToList();
    }

    /// <summary>Mirrors NMainMenu.UpdateTimelineButtonBehavior's lock-out condition.</summary>
    private static bool IsEpochRevealBlocking()
    {
        try
        {
            var save = SaveManager.Instance;
            return save != null && !DebugSettings.DevSkip && save.GetDiscoveredEpochCount() > 0 && !save.HasRunSave;
        }
        catch
        {
            return false;
        }
    }

    private static void AddPendingEpochState(Dictionary<string, object?> result)
    {
        var pending = BuildPendingEpochs();
        result["pending_epochs"] = pending;
        result["epoch_reveal_blocking"] = IsEpochRevealBlocking();
        if (pending.Count > 0)
        {
            bool hasRunSave = false;
            try { hasRunSave = SaveManager.Instance?.HasRunSave ?? false; } catch { }
            result["epoch_reveal_available"] = !hasRunSave;
        }
    }

    private static List<NEpochSlot> GetTimelineSlots(NTimelineScreen timeline)
    {
        return FindAll<NEpochSlot>(timeline)
            .Where(s => IsLiveNode(s) && !s.IsQueuedForDeletion() && s.model != null)
            .ToList();
    }

    private static List<NUnlockScreen> GetTimelineUnlockScreens(NTimelineScreen timeline)
    {
        return FindAll<NUnlockScreen>(timeline).Where(IsLiveNode).ToList();
    }

    private static NEpochInspectScreen? GetOpenInspectScreen(NTimelineScreen timeline)
    {
        var inspect = GetInstanceFieldValue(timeline, "_inspectScreen") as NEpochInspectScreen
            ?? FindFirst<NEpochInspectScreen>(timeline);
        return inspect != null && IsNodeVisible(inspect) ? inspect : null;
    }

    private static bool IsTimelineInputBlocked(NTimelineScreen timeline)
    {
        var blocker = GetInstanceFieldValue(timeline, "_inputBlocker") as Control;
        return blocker != null && IsNodeVisible(blocker);
    }

    private static string GetTimelinePhase(NTimelineScreen timeline, out Dictionary<string, object?> detail)
    {
        detail = new Dictionary<string, object?>();

        var tutorial = FindFirst<NTimelineTutorial>(timeline);
        if (tutorial != null && IsNodeVisible(tutorial))
        {
            var ack = GetInstanceFieldValue(tutorial, "_acknowledgeButton") as NClickableControl;
            detail["button_enabled"] = ack != null && ack.IsEnabled && IsNodeVisible(ack);
            return "tutorial";
        }

        var unlockScreens = GetTimelineUnlockScreens(timeline);
        if (unlockScreens.Count > 0)
        {
            var screen = unlockScreens[0];
            var confirm = GetInstanceFieldValue(screen, "_unlockConfirmButton") as NClickableControl;
            detail["unlock_screen_type"] = screen.GetType().Name;
            detail["button_enabled"] = confirm != null && confirm.IsEnabled && IsNodeVisible(confirm) && !screen.IsQueuedForDeletion();
            return "unlock_screen";
        }

        var inspect = GetOpenInspectScreen(timeline);
        if (inspect != null)
        {
            var close = GetInstanceFieldValue(inspect, "_closeButton") as NClickableControl;
            var epoch = GetInstanceFieldValue(inspect, "_epoch") as EpochModel;
            detail["epoch_id"] = epoch?.Id;
            // NCloseButton.OnRelease sets MouseFilter=Ignore and calls Close(); the button stays
            // IsEnabled until the fade-out tween ends, so treat Ignore as "already closing".
            detail["button_enabled"] = close != null && close.IsEnabled && IsNodeVisible(close) &&
                                       close.MouseFilter != Control.MouseFilterEnum.Ignore;
            return "inspect";
        }

        bool queued = false;
        try { queued = timeline.IsScreenQueued(); } catch { }
        detail["unlock_screen_queued"] = queued;

        if (IsTimelineInputBlocked(timeline))
            return "busy";
        return "ready";
    }

    private static void AddTimelineRevealState(Dictionary<string, object?> result, NTimelineScreen timeline)
    {
        AddPendingEpochState(result);

        var phase = GetTimelinePhase(timeline, out var detail);
        result["timeline_phase"] = phase;
        foreach (var kv in detail)
            result["timeline_" + kv.Key] = kv.Value;

        var revealable = new List<Dictionary<string, object?>>();
        foreach (var slot in GetTimelineSlots(timeline))
        {
            if (slot.State != EpochSlotState.Obtained)
                continue;
            var info = BuildEpochInfo(slot.model.Id, null);
            info["spawned"] = slot.HasSpawned;
            revealable.Add(info);
        }
        result["revealable_slots"] = revealable;

        var back = GetInstanceFieldValue(timeline, "_backButton") as NClickableControl;
        result["back_enabled"] = back != null && back.IsEnabled && IsNodeVisible(back);
    }

    private static Dictionary<string, object?> RevealStep(string step, string message, bool done = false, string? epochId = null)
    {
        var result = new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["step"] = step,
            ["message"] = message,
            ["done"] = done,
            ["pending_epochs"] = BuildPendingEpochs()
        };
        if (epochId != null)
            result["epoch_id"] = epochId;
        if (!done)
            result["hint"] = "Call reveal_epoch again (after ~0.5s) until done is true.";
        return result;
    }

    /// <summary>
    /// One step of the player's Timeline reveal flow. Idempotent: call repeatedly until done=true.
    /// Every step goes through the game's own UI nodes (ForceClick -> OnRelease/Released), so the
    /// save is written by SaveManager.RevealEpoch / SaveProgressFile exactly as for a player.
    /// </summary>
    private static Dictionary<string, object?> ExecuteRevealEpoch(string? epochId)
    {
        if (RunManager.Instance.IsInProgress)
            return Error("reveal_epoch only works from the main menu (a run is in progress)");

        var tree = Godot.Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null)
            return Error("Scene tree not available");

        var pending = GetRevealableEpochsSafe();
        epochId = string.IsNullOrWhiteSpace(epochId) ? null : epochId.Trim();

        if (epochId != null)
        {
            var known = SaveManager.Instance?.Progress?.Epochs
                .FirstOrDefault(e => string.Equals(e.Id, epochId, StringComparison.OrdinalIgnoreCase));
            if (known == null)
                return Error($"Unknown epoch '{epochId}'. Pending: {string.Join(", ", pending.Select(e => e.Id))}");
            epochId = known.Id;
            bool isPending = pending.Any(e => e.Id == epochId);
            if (!isPending && known.State != EpochState.Revealed)
                return Error($"Epoch '{epochId}' is not revealable (state {known.State}). Pending: {string.Join(", ", pending.Select(e => e.Id))}");
            if (isPending && known.State == EpochState.ObtainedNoSlot)
            {
                var withSlot = pending.Where(e => e.State == EpochState.Obtained).Select(e => e.Id).ToList();
                return Error($"Epoch '{epochId}' has no Timeline slot yet; it appears after revealing an earlier epoch. " +
                             $"Reveal one of [{string.Join(", ", withSlot)}] first, or call reveal_epoch without epoch_id.");
            }
        }

        var timeline = FindFirst<NTimelineScreen>(tree.Root);
        bool timelineOpen = timeline != null && IsNodeVisible(timeline);

        if (!timelineOpen)
        {
            if (pending.Count == 0)
                return RevealStep("none", "No epochs are waiting to be revealed", done: true);

            if (IsAnyFtueVisible(tree.Root) || FindVisibleVerticalPopup(tree.Root) != null)
                return Error("A popup is open; dismiss it with menu_select first");

            var mainMenu = FindFirst<NMainMenu>(tree.Root);
            if (mainMenu == null)
                return Error("Not on the main menu");

            bool hasRunSave = false;
            try { hasRunSave = SaveManager.Instance?.HasRunSave ?? false; } catch { }
            if (hasRunSave)
                return Error("A run save exists; the game disables the Timeline until that run is continued or abandoned (menu_select 'continue' or 'abandon_run')");

            var button = GetInstanceFieldValue(mainMenu, "_timelineButton") as NClickableControl;
            if (button == null)
                return Error("Could not find the main menu Timeline button");
            if (!IsNodeVisible(button))
                return Error("Timeline button is not visible; return to the top-level main menu first (menu_select 'back')");
            if (!button.IsEnabled)
                return Error("Timeline button is disabled");

            button.ForceClick();
            return RevealStep("open_timeline", "Opened the Timeline", epochId: epochId);
        }

        var phase = GetTimelinePhase(timeline!, out var detail);
        bool buttonEnabled = detail.TryGetValue("button_enabled", out var be) && be is true;

        switch (phase)
        {
            case "tutorial":
            {
                if (!buttonEnabled)
                    return RevealStep("wait", "Timeline tutorial is animating");
                var tutorial = FindFirst<NTimelineTutorial>(timeline!)!;
                ((NClickableControl)GetInstanceFieldValue(tutorial, "_acknowledgeButton")!).ForceClick();
                return RevealStep("tutorial", "Acknowledged the Timeline tutorial");
            }
            case "unlock_screen":
            {
                var type = detail["unlock_screen_type"] as string;
                if (!buttonEnabled)
                    return RevealStep("wait", $"{type} is animating");
                var screen = GetTimelineUnlockScreens(timeline!)[0];
                ((NClickableControl)GetInstanceFieldValue(screen, "_unlockConfirmButton")!).ForceClick();
                return RevealStep("confirm_unlock", $"Confirmed {type}");
            }
            case "inspect":
            {
                var shownId = detail["epoch_id"] as string;
                if (!buttonEnabled)
                    return RevealStep("wait", "Epoch reveal animation is playing", epochId: shownId);
                var inspect = GetOpenInspectScreen(timeline!)!;
                ((NClickableControl)GetInstanceFieldValue(inspect, "_closeButton")!).ForceClick();
                return RevealStep("close_inspect", "Closed the epoch inspect screen", epochId: shownId);
            }
        }

        // Nothing modal is showing. A queued unlock screen that nobody opened would leave the
        // input blocker up forever (EnableInput is a no-op while the queue is non-empty).
        if (detail.TryGetValue("unlock_screen_queued", out var q) && q is true)
        {
            timeline!.OpenQueuedScreen();
            return RevealStep("open_queued_unlock", "Opened a queued unlock screen");
        }

        if (phase == "busy")
            return RevealStep("wait", "Timeline is animating");

        var obtainedSlots = GetTimelineSlots(timeline!).Where(s => s.State == EpochSlotState.Obtained).ToList();
        if (pending.Count > 0 || obtainedSlots.Count > 0)
        {
            NEpochSlot? slot = null;
            if (epochId != null)
                slot = obtainedSlots.FirstOrDefault(s => s.model.Id == epochId);
            slot ??= obtainedSlots.FirstOrDefault(s => pending.Any(p => p.Id == s.model.Id))
                     ?? obtainedSlots.FirstOrDefault();

            if (slot == null)
                return Error($"Epochs are pending ({string.Join(", ", pending.Select(e => e.Id))}) but the Timeline shows no revealable slot");
            if (!slot.HasSpawned)
                return RevealStep("wait", "Epoch slot is still spawning", epochId: slot.model.Id);

            var id = slot.model.Id;
            slot.ForceClick();
            return RevealStep("reveal", $"Revealed {id}", epochId: id);
        }

        var back = GetInstanceFieldValue(timeline!, "_backButton") as NClickableControl;
        if (back == null || !back.IsEnabled || !IsNodeVisible(back))
            return RevealStep("wait", "Waiting for the Timeline back button");
        back.ForceClick();
        return RevealStep("close_timeline", "All epochs revealed; closing the Timeline");
    }
}
