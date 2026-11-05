using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The Settings window: playback tuning, sound, Network Sync and Ensemble options, and their setters.
public sealed partial class Plugin
{
    // The Settings window — playback tuning + the buffered-sync toggle and its Network Settings button.
    private HudElement BuildSettingsRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("mst.settings.header"), Emphasis: true),
        SliderRow("transpose", () => _loc.T("mst.set.transpose"),
            new SliderElement(Get: () => _bandTranspose, Set: v => SetTranspose((int)System.MathF.Round(v)), Min: -48f, Max: 48f),
            () => $"{(_bandTranspose >= 0 ? "+" : "")}{_bandTranspose}",
            () => SetTranspose(0),
            () => _loc.T("mst.set.transpose.help")),
        SliderRow("hold_ms", () => _loc.T("mst.set.hold"),
            new SliderElement(Get: () => _bandHoldMs, Set: v => SetHold((int)System.MathF.Round(v)), Min: -500f, Max: 2000f),
            () => $"{(_bandHoldMs >= 0 ? "+" : "")}{_bandHoldMs}",
            () => SetHold(0),
            () => _loc.T("mst.set.hold.help")),
        SliderRow("tempo_pct", () => _loc.T("mst.set.tempo"),
            new SliderElement(Get: () => _bandTempoPct, Set: v => SetTempo((int)System.MathF.Round(v)), Min: 25f, Max: 400f),
            () => $"{_bandTempoPct}%",
            () => SetTempo(100),
            () => _loc.T("mst.set.tempo.help")),
        SliderRow("max_poly", () => _loc.T("mst.set.maxpoly"),
            new SliderElement(Get: () => _bandMaxPoly, Set: v => SetMaxPoly((int)System.MathF.Round(v)), Min: 0f, Max: 16f),
            () => _bandMaxPoly == 0 ? _loc.T("mst.off") : _bandMaxPoly.ToString(),
            () => SetMaxPoly(0),
            () => _loc.T("mst.set.maxpoly.help")),
        SliderRow("restrike_gap", () => _loc.T("mst.set.restrike"),
            new SliderElement(Get: () => _bandRestrikeGapMs, Set: v => SetRestrikeGap((int)System.MathF.Round(v)), Min: 0f, Max: 100f),
            () => _bandRestrikeGapMs == 0 ? _loc.T("mst.off") : $"{_bandRestrikeGapMs}ms",
            () => SetRestrikeGap(33),   // reset to the default (matches Plugin.cs config-load default + help text)
            () => _loc.T("mst.set.restrike.help")),
        BuildMonitorVolumeRow(),
        HelpToggle("force_sustain",
            () => _bandForceSustain,
            SetForceSustain,
            () => _loc.T("mst.set.forceSustain"),
            () => _loc.T("mst.set.forceSustain.help")),
        HelpToggle("apply_tone",
            () => _bandApplyTone,
            OnToggleApplyTone,
            () => _loc.T("mst.set.applyTone"),
            () => _loc.T("mst.set.applyTone.help")),
        HelpToggle("relay_tone",
            () => _bandRelayTone,
            OnToggleRelayTone,
            () => _loc.T("mst.set.relayTone"),
            () => _loc.T("mst.set.relayTone.help"),
            // Only meaningful in buffered Network Sync mode (it rewrites the buffered Tone record) with tone applied.
            enabled: () => _bandNetPrebuffer && _bandApplyTone),
        HelpToggle("net_prebuffer",
            () => _bandNetPrebuffer,
            SetNetPrebuffer,
            () => _loc.T("mst.set.netSync"),
            () => _loc.T("mst.set.netSync.help")),
        HelpToggle("mute_local",
            () => _bandMuteLocal,
            SetMuteLocal,
            () => _loc.T("mst.set.muteLocal"),
            () => _bandNetPrebuffer
                ? _loc.T("mst.set.muteLocal.help.on")
                : _loc.T("mst.set.muteLocal.help.off"),
            enabled: () => _bandNetPrebuffer),
        HelpToggle("show_keyviz",
            () => _bandShowKeyViz,
            v  => { _bandShowKeyViz = v; _bandPlayer.ShowKeyViz = v; _cfg.Set<bool>("show_keyviz", v); _cfg.Save(); },
            () => _loc.T("mst.set.keyviz"),
            () => _loc.T("mst.set.keyviz.help"),
            enabled: () => _bandNetPrebuffer),
        new ButtonElement(Label: () => _loc.T("mst.btn.netSettings"), OnClick: () => _networkWindow.SetVisible(true),
                          Enabled: () => _bandNetPrebuffer, Width: 320f),
        BuildEnsembleSection(),
    }, Gap: 8f);

    private void SetTranspose(int v)
    {
        _bandTranspose = System.Math.Clamp(v, -48, 48);
        _bandPlayer.Transpose = _bandTranspose;   // live in both modes (buffered rebuilds the timeline)
    }

    private void SetHold(int v)
    {
        _bandHoldMs = System.Math.Clamp(v, -500, 2000);
        _bandPlayer.HoldOffsetMs = _bandHoldMs;   // live — applies to notes pressed from now on
        _cfg.Set<int>("hold_ms", _bandHoldMs);
        _cfg.Save();
    }

    private void SetTempo(int pct)
    {
        _bandTempoPct = System.Math.Clamp(pct, 25, 400);
        _bandPlayer.TempoPct = _bandTempoPct;   // live — takes effect immediately
        _cfg.Set<int>("tempo_pct", _bandTempoPct);
        _cfg.Save();
    }

    private void SetMaxPoly(int v)
    {
        _bandMaxPoly = System.Math.Clamp(v, 0, 16);
        _bandPlayer.MaxPolyphony = _bandMaxPoly;   // live (buffered rebuilds the timeline)
        _cfg.Set<int>("max_poly", _bandMaxPoly);
        _cfg.Save();
    }

    private void SetRestrikeGap(int v)
    {
        _bandRestrikeGapMs = System.Math.Clamp(v, 0, 100);
        _bandPlayer.RestrikeGapMs = _bandRestrikeGapMs;   // live in buffered mode (rebuilds the timeline)
        _cfg.Set<int>("restrike_gap_ms", _bandRestrikeGapMs);
        _cfg.Save();
    }

    // Named (not inline lambdas) so the dev-only sweep command poller (Plugin.SweepCmd.cs) goes through the exact UI path.
    private void SetForceSustain(bool v) { _bandForceSustain = v; _bandPlayer.ForceSustain = v; _cfg.Set<bool>("force_sustain", v); _cfg.Save(); }
    private void SetNetPrebuffer(bool v) { _bandNetPrebuffer = v; _cfg.Set<bool>("net_prebuffer", v); _cfg.Save(); }   // applied to the player at next Play
    private void SetMuteLocal(bool v)    { _bandMuteLocal = v; _bandPlayer.MuteLocal = v; _cfg.Set<bool>("mute_local", v); _cfg.Save(); }

}
