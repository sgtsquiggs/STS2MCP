using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2_MCP;

/// <summary>
/// Auto-enables Instant Mode (PrefsSave.FastMode = FastModeType.Instant).
///
/// The game persists fast_mode in prefs.save, but NGame's boot sequence deliberately downgrades
/// Instant to Fast in release builds right after InitPrefsData, so Instant never survives a restart.
/// The mod re-applies it once per loaded PrefsSave object (game boot, profile switch), after the
/// main menu exists, i.e. after the boot downgrade. A manual untick later in the session is respected
/// until the next prefs load.
///
/// Opt-out: "auto_instant_mode": false in STS2_MCP.conf, or env STS2_MCP_AUTO_INSTANT_MODE=0.
/// </summary>
public static partial class McpMod
{
    private const string AutoInstantEnvVar = "STS2_MCP_AUTO_INSTANT_MODE";

    private static bool _autoInstantMode = true;
    private static PrefsSave? _autoInstantAppliedTo;

    private static void LoadAutoInstantModeSetting()
    {
        bool enabled = true;
        try
        {
            string? modDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string? configPath = modDir == null ? null : Path.Combine(modDir, ConfigFileName);
            if (configPath != null && File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("auto_instant_mode", out var elem)
                    && elem.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    enabled = elem.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to read auto_instant_mode from config: {ex.Message}");
        }

        string? env = System.Environment.GetEnvironmentVariable(AutoInstantEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && TryParseOnOff(env, out bool envValue))
            enabled = envValue;

        _autoInstantMode = enabled;
        GD.Print($"[STS2 MCP] auto_instant_mode = {enabled}");
    }

    private static bool TryParseOnOff(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "on": case "yes": case "instant":
                value = true; return true;
            case "0": case "false": case "off": case "no":
                value = false; return true;
            default:
                value = false; return false;
        }
    }

    private static PrefsSave? TryGetPrefs()
    {
        try { return SaveManager.Instance?.PrefsSave; }
        catch { return null; }
    }

    private static void MaintainAutoInstantMode()
    {
        if (!_autoInstantMode) return;
        var prefs = TryGetPrefs();
        if (prefs == null || ReferenceEquals(prefs, _autoInstantAppliedTo)) return;

        // Wait until boot finished (the boot downgrade runs before the main menu is launched).
        bool ready;
        try { ready = NGame.Instance?.MainMenu != null || RunManager.Instance.IsInProgress; }
        catch { ready = false; }
        if (!ready) return;

        _autoInstantAppliedTo = prefs;
        if (prefs.FastMode == FastModeType.Instant) return;
        var previous = prefs.FastMode;
        ApplyInstantMode(true);
        GD.Print($"[STS2 MCP] Auto-enabled Instant Mode (was {previous})");
    }

    /// <summary>
    /// Same writes as the settings UI's Instant Mode tickbox (OnTick / OnUntick patches) plus the
    /// SavePrefsFile the settings screen does when it closes. Off falls back to Fast, like the tickbox.
    /// </summary>
    private static void ApplyInstantMode(bool on)
    {
        var prefs = TryGetPrefs() ?? throw new InvalidOperationException("Prefs are not loaded yet");
        prefs.FastMode = on ? FastModeType.Instant : FastModeType.Fast;

        try
        {
            if (_instantModeTickbox != null && GodotObject.IsInstanceValid(_instantModeTickbox))
                _instantModeTickbox.IsTicked = on;
            if (on && _originalFastModeTickbox != null && GodotObject.IsInstanceValid(_originalFastModeTickbox)
                && !_originalFastModeTickbox.IsTicked)
                _originalFastModeTickbox.IsTicked = true;
        }
        catch { /* settings screen not open / freed */ }

        try { SaveManager.Instance.SavePrefsFile(); }
        catch (Exception ex) { GD.PrintErr($"[STS2 MCP] SavePrefsFile failed: {ex.Message}"); }
    }

    internal static Dictionary<string, object?> BuildSettingsInfo()
    {
        var prefs = TryGetPrefs();
        return new Dictionary<string, object?>
        {
            ["instant_mode"] = prefs != null ? prefs.FastMode == FastModeType.Instant : null,
            ["fast_mode"] = prefs?.FastMode.ToString().ToLowerInvariant(),
            ["auto_instant_mode"] = _autoInstantMode
        };
    }

    private static Dictionary<string, object?> WithSettings(Dictionary<string, object?> state)
    {
        try { state["settings"] = BuildSettingsInfo(); }
        catch { }
        return state;
    }

    /// <summary>Action: {"action":"set_setting","setting":"instant_mode","value":"on"|"off"|true|false}.</summary>
    private static Dictionary<string, object?> ExecuteSetSetting(Dictionary<string, JsonElement> parsed)
    {
        string setting = parsed.TryGetValue("setting", out var sElem) && sElem.ValueKind == JsonValueKind.String
            ? sElem.GetString() ?? "" : "";
        if (setting != "instant_mode")
            return Error($"Unknown setting '{setting}'. Supported: instant_mode");

        bool on;
        if (!parsed.TryGetValue("value", out var vElem))
            return Error("Missing 'value' (on/off)");
        if (vElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
            on = vElem.GetBoolean();
        else if (vElem.ValueKind == JsonValueKind.String && TryParseOnOff(vElem.GetString() ?? "", out bool parsedOn))
            on = parsedOn;
        else
            return Error("Invalid 'value'; use on/off or true/false");

        if (TryGetPrefs() == null)
            return Error("Prefs are not loaded yet (game still booting)");

        ApplyInstantMode(on);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Instant Mode {(on ? "on" : "off")}",
            ["settings"] = BuildSettingsInfo()
        };
    }
}
