using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The Settings window: playback tuning, sound, Network Sync and Ensemble options, and their setters.
public sealed partial class Plugin
{
    // The Settings window (house style, approved design 2026-11-06; the old Network window is merged in as NETWORK SYNC):
    // four panels of rows that each end in "?", then a full-width Close. Network / Ensemble rows grey out exactly as
    // before (they need Network Sync); the three network sliders, which used to sit behind the disabled "Network
    // Settings" button, now grey out the same way.
    private HudElement BuildSettingsRoot()
    {
        HudElement body = new ColumnElement(new HudElement[]
        {
            BuildPlaybackPanel(),
            BuildSoundPanel(),
            BuildNetSyncPanel(),
            BuildEnsemblePanel(),   // Plugin.Ensemble.cs
        }, Gap: 8f);
        // Scroll only when the panels are taller than the screen. The toolkit has no "max height" for an auto-height
        // window, so this is decided once, here, from the computed panel height vs the canvas height (a resolution
        // change after startup is not re-checked). When it fits, no scroll viewport (and no scrollbar lane) at all.
        float canvasH = _services.Framework.CanvasHeight > 1f ? _services.Framework.CanvasHeight : _services.Framework.ScreenHeight;
        float room    = canvasH - SettingsChromeH;
        if (room > 200f && SettingsBodyHeight > room)
            body = new ScrollElement(body, Height: room) { FixedHeight = true };
        return new ColumnElement(new HudElement[]
        {
            body,
            NeutralButton(() => _loc.T("mst.close"), () => _settingsWindow.SetVisible(false)),
        }, Gap: 8f);
    }

    // Body height from the row build, not measured: a row = the 28 px "?" pill; a panel = 2×8 padding + the bold-12
    // caption row (~17) + rows × (28 + 6 gap); Ensemble adds its two-line footer (~6 + 40). Chrome = title bar, body
    // padding, the Close pill and a margin to the screen edges.
    private const float SettingsRowH      = 28f + 6f;
    private const float SettingsPanelBase = 16f + 17f;
    private const float SettingsBodyHeight = 4 * SettingsPanelBase + (6 + 3 + 7 + 3) * SettingsRowH + 46f + 3 * 8f;
    private const float SettingsChromeH   = 180f;

    private HudElement BuildPlaybackPanel() => SectionPanel(() => _loc.T("mst.cap.playback"), null,
        SliderRow("transpose", () => _loc.T("mst.set.transpose"),
            RoundSlider(() => _bandTranspose, v => SetTranspose((int)MathF.Round(v)), -48f, 48f),
            () => $"{(_bandTranspose >= 0 ? "+" : "")}{_bandTranspose}",
            () => SetTranspose(0),
            () => _loc.T("mst.set.transpose.help")),
        SliderRow("tempo_pct", () => _loc.T("mst.set.tempo"),
            RoundSlider(() => _bandTempoPct, v => SetTempo((int)MathF.Round(v)), 25f, 400f),
            () => $"{_bandTempoPct}%",
            () => SetTempo(100),
            () => _loc.T("mst.set.tempo.help")),
        SliderRow("hold_ms", () => _loc.T("mst.set.hold"),
            RoundSlider(() => _bandHoldMs, v => SetHold((int)MathF.Round(v)), -500f, 2000f),
            () => $"{(_bandHoldMs >= 0 ? "+" : "")}{_bandHoldMs}",
            () => SetHold(0),
            () => _loc.T("mst.set.hold.help")),
        SliderRow("max_poly", () => _loc.T("mst.set.maxpoly"),
            RoundSlider(() => _bandMaxPoly, v => SetMaxPoly((int)MathF.Round(v)), 0f, 16f),
            () => _bandMaxPoly == 0 ? _loc.T("mst.off") : _bandMaxPoly.ToString(),
            () => SetMaxPoly(0),
            () => _loc.T("mst.set.maxpoly.help")),
        SliderRow("restrike_gap", () => _loc.T("mst.set.restrike"),
            RoundSlider(() => _bandRestrikeGapMs, v => SetRestrikeGap((int)MathF.Round(v)), 0f, 100f),
            () => _bandRestrikeGapMs == 0 ? _loc.T("mst.off") : $"{_bandRestrikeGapMs}ms",
            () => SetRestrikeGap(33),   // reset to the default (matches Plugin.cs config-load default + help text)
            () => _loc.T("mst.set.restrike.help")),
        SliderRow("gap_sec", () => _loc.T("mst.pl.gap"),   // moved here from the queue
            RoundSlider(() => _gapSec, v => SetGap((int)MathF.Round(v)), 0f, 15f),
            () => _gapSec == 0 ? _loc.T("mst.off") : $"{_gapSec}s",
            () => SetGap(0),
            () => _loc.T("mst.pl.gap.help")));

    private HudElement BuildSoundPanel() => SectionPanel(() => _loc.T("mst.cap.sound"), null,
        BuildMonitorVolumeRow(),   // Plugin.Volume.cs
        HelpToggle("force_sustain",
            () => _bandForceSustain,
            SetForceSustain,
            () => _loc.T("mst.set.forceSustain"),
            () => _loc.T("mst.set.forceSustain.help")),
        HelpToggle("apply_tone",
            () => _bandApplyTone,
            OnToggleApplyTone,
            () => _loc.T("mst.set.applyTone"),
            () => _loc.T("mst.set.applyTone.help")));

    // NETWORK SYNC: caption hint = a green "On" / muted "Off" (a trailing coloured text; the plain hint is always muted).
    private HudElement BuildNetSyncPanel() => SectionPanelWithTrailing(() => _loc.T("mst.cap.netSync"), null,
        new TextElement(() => _loc.T(_bandNetPrebuffer ? "mst.state.on" : "mst.state.off"),
            Color: () => _bandNetPrebuffer ? StatusGreen : MutedColor(), NoWrap: true),
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
        HelpToggle("relay_tone",
            () => _bandRelayTone,
            OnToggleRelayTone,
            () => _loc.T("mst.set.relayTone"),
            () => _loc.T("mst.set.relayTone.help"),
            // Only meaningful in buffered Network Sync mode (it rewrites the buffered Tone record) with tone applied.
            enabled: () => _bandNetPrebuffer && _bandApplyTone),
        SliderRow("net_ahead", () => _loc.T("mst.net.lookahead"),
            RoundSlider(() => _bandAheadMs, v => SetAhead((int)MathF.Round(v)), 100f, 1500f, NetSyncOn),
            () => $"{_bandAheadMs}ms",
            () => SetAhead(400),
            () => _loc.T("mst.net.lookahead.desc"),
            NetSyncOn),
        SliderRow("net_batch", () => _loc.T("mst.net.sendEvery"),
            RoundSlider(() => _bandBatchMs, v => SetBatch((int)MathF.Round(v)), 100f, 400f, NetSyncOn),
            () => $"{_bandBatchMs}ms",
            () => SetBatch(100),
            () => _loc.T("mst.net.sendEvery.desc"),
            NetSyncOn),
        SliderRow("tone_lead", () => _loc.T("mst.net.tonelead"),
            RoundSlider(() => _bandToneLeadMs, v => SetToneLead((int)MathF.Round(v)), 0f, 500f, NetSyncOn),
            () => $"{_bandToneLeadMs}ms",
            () => SetToneLead(120),
            () => _loc.T("mst.net.tonelead.desc"),
            NetSyncOn));

    private bool NetSyncOn() => _bandNetPrebuffer;

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
