using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Ensemble (合奏) support: play the auto-player locked to the ensemble's shared beat grid, counting in to the next
// measure downbeat so it lands in time with the metronome and other performers. Grid params (bpm/beat/startTs) and
// the current server time are read from C# via EnsemblePatch; the player anchors both its local audio and the
// network send to the grid. Leader/member both work — you just need to be in an ensemble. See Band-Instrument-Playback.md.
public sealed partial class Plugin
{
    private bool   _bandEnsembleSync;
    private bool   _bandEnsembleMatchTempo = false;
    private bool   _ensembleWaiting;    // Play was armed while not yet in an ensemble — hold until one starts
    private int    _ensembleWaitIdx;    // queue index to play once the ensemble goes live
    private double _ensWaitAccumMs;     // throttle for the "in ensemble yet?" poll
    private bool   _bandAutoAcceptEnsemble;   // auto-accept incoming BandEnsemble invites
    private double _autoAcceptHealAccumMs;    // throttle for the self-heal "is the listener still installed?" poll

    // Toggle: install/remove a pure-Lua listener on InvitationRefreshTips that auto-replies TRUE to BandEnsemble invites
    // (same AsyncReplyJoinEnsemble the game's own accept button calls). Preconditions are the game's own: in a team +
    // instrument summoned, else the invite never reaches this client. See grpc_band_ntf_impl.lua / band_vm.lua.
    private void SetAutoAcceptEnsemble(bool v)
    {
        _bandAutoAcceptEnsemble = v;
        _cfg.Set<bool>("auto_accept_ensemble", v); _cfg.Save();
        ApplyAutoAcceptEnsemble();
    }

    private void ApplyAutoAcceptEnsemble()
    {
        if (_bandAutoAcceptEnsemble)
            _services.Lua.DoString("pcall(function() if rawget(_G,'__maestro_ens_h') then return end " +
                "local t={} local h=function(_,info) if info~=nil and info.tipsType==E.InvitationTipsType.BandEnsemble then " +
                "Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM('band') " +
                "local tok=Z.DataMgr.Get('band_data').CancelSource:CreateToken() vm:AsyncReplyJoinEnsemble(true, tok) end)() end end " +
                "rawset(_G,'__maestro_ens_h',h) rawset(_G,'__maestro_ens_t',t) " +
                "Z.EventMgr:Add(Z.ConstValue.InvitationRefreshTips, h, t) end)");
        else
            _services.Lua.DoString("pcall(function() local h=rawget(_G,'__maestro_ens_h') local t=rawget(_G,'__maestro_ens_t') " +
                "if h~=nil then Z.EventMgr:Remove(Z.ConstValue.InvitationRefreshTips, h, t) " +
                "rawset(_G,'__maestro_ens_h',nil) rawset(_G,'__maestro_ens_t',nil) end end)");
    }

    // Called each frame from the playlist tick: self-healing install of the auto-accept listener. SetAutoAcceptEnsemble
    // installs immediately on toggle, but that first attempt can lose a race with Lua-state readiness (DoString no-ops
    // until ILua is Ready) — this is the safety net. About every 2 s, while the toggle is ON, we check the
    // __maestro_ens_h Lua global (absent ⇒ listener NOT installed) and re-install if missing. The game's EventMgr sub is
    // process-lifetime, so a successful install sticks; the install chunk's own _G guard prevents a duplicate Add. When
    // the toggle is OFF we never re-install, so toggling off is not undone by this tick.
    private void AutoAcceptEnsembleTick(float dt)
    {
        if (!_bandAutoAcceptEnsemble) return;
        _autoAcceptHealAccumMs += dt * 1000.0;
        if (_autoAcceptHealAccumMs < 2000) return;   // ~0.5 Hz poll
        _autoAcceptHealAccumMs = 0;
        // The listener is a Lua FUNCTION, not a boolean — ILua can't read it back directly, so park a boolean that
        // reflects its presence and read that. false ⇒ not installed (or Lua not ready yet) ⇒ (re)install.
        _services.Lua.DoString("pcall(function() rawset(_G,'__maestro_ens_installed', rawget(_G,'__maestro_ens_h')~=nil) end)");
        if (!(_services.Lua.TryReadGlobalBool("__maestro_ens_installed", out var installed) && installed)) ApplyAutoAcceptEnsemble();
    }

    // True while in a live ensemble session (reflection read; call sparingly).
    private bool EnsembleActive()
    {
        var r = EnsemblePatch.Read();
        return r != null && r.Value.inEnsemble == 1;
    }

    // Polled each frame while armed; starts playback the moment the ensemble goes live.
    private void EnsembleWaitTick(float dt)
    {
        _ensWaitAccumMs += dt * 1000.0;
        if (_ensWaitAccumMs < 300) return;   // ~3 Hz reflection poll
        _ensWaitAccumMs = 0;
        if (EnsembleActive())
        {
            _ensembleWaiting = false;
            PlayQueueIndex(_ensembleWaitIdx);
        }
    }

    private HudElement BuildEnsembleSection() => new ColumnElement(new HudElement[]
    {
        new SeparatorElement(),
        new TextElement(() => _loc.T("mst.ens.header"), Emphasis: true),
        HelpToggle("ensemble_sync",
            () => _bandEnsembleSync,
            v  => { _bandEnsembleSync = v; _cfg.Set<bool>("ensemble_sync", v); _cfg.Save(); },
            () => _loc.T("mst.ens.sync"),
            () => _loc.T("mst.ens.sync.help"),
            enabled: () => _bandNetPrebuffer),
        HelpToggle("ensemble_match_tempo",
            () => _bandEnsembleMatchTempo,
            v  => { _bandEnsembleMatchTempo = v; _cfg.Set<bool>("ensemble_match_tempo", v); _cfg.Save(); },
            () => _loc.T("mst.ens.tempo"),
            () => _loc.T("mst.ens.tempo.help"),
            enabled: () => _bandNetPrebuffer && _bandEnsembleSync),
        HelpToggle("auto_accept_ensemble",
            () => _bandAutoAcceptEnsemble,
            SetAutoAcceptEnsemble,
            () => _loc.T("mst.ens.autoAccept"),
            () => _loc.T("mst.ens.autoAccept.help")),
        new TextElement(() => _loc.T("mst.ens.footer"),
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
    }, Gap: 6f);

    // Compute the grid-aligned base + count-in offset and tempo-scale, and apply to the player (called just before
    // Start). Tempo-scaling the song to the ensemble BPM + the downbeat base = every note lands on the ensemble beat
    // grid (for steady-tempo songs). Returns true if applied (in an ensemble, buffered on, sync enabled).
    private bool ApplyEnsembleGrid(double songBpm)
    {
        _bandPlayer.EnsembleGrid = false;
        if (!_bandEnsembleSync || !_bandNetPrebuffer) return false;

        var r = EnsemblePatch.Read();
        if (r == null || r.Value.inEnsemble != 1 || r.Value.bpm <= 0 || r.Value.beat <= 0) return false;

        long now = EnsemblePatch.ReadServerTime();
        if (now <= 0) return false;

        double measureMs = r.Value.beat * (60000.0 / r.Value.bpm);
        long   lead      = _bandAheadMs + 300;                                   // downbeat must be far enough out to send note 0 ahead
        long   n         = (long)Math.Ceiling((now + lead - r.Value.startTs) / measureMs);
        if (n < 0) n = 0;
        long   baseMs    = r.Value.startTs + (long)Math.Round(n * measureMs);    // next measure downbeat ≥ now+lead

        _bandPlayer.EnsembleBaseMs      = baseMs;
        _bandPlayer.EnsembleClockOffset = now - baseMs;                          // negative → local count-in
        _bandPlayer.EnsembleGrid        = true;

        // Tempo-scale the song to the ensemble BPM so its beats fall on the grid (skip if the song's BPM is unknown).
        int tempoPct = -1;
        if (_bandEnsembleMatchTempo && songBpm > 0)
        {
            tempoPct = (int)Math.Round(r.Value.bpm / songBpm * 100.0);
            _bandPlayer.TempoPct = Math.Clamp(tempoPct, 25, 400);
        }
        _services.Log.Info($"[Maestro] ensemble grid: ensBpm={r.Value.bpm} beat={r.Value.beat} songBpm={songBpm:F1} tempo%={tempoPct} base={baseMs} count-in={baseMs - now}ms");
        return true;
    }
}
