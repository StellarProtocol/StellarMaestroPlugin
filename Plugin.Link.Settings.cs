using System;
using System.Globalization;

namespace Stellar.Maestro;

// Maestro Link — linked settings overrides + the status/display helpers derived from the song state.
//
// Overrides go to the MidiPlayer only — never the Plugin fields or the config file — so Disconnect can hand back the
// player's own values (RestoreLocalPlayerSettings). Field classes per protocol §3:
//   • "plugin default" (null) → Maestro's built-in default;
//   • "player's own" (null; muteLocal / monitorVolume, batch 1) → the player's own saved Maestro value, no override.
public sealed partial class Plugin
{
    // Live/network/instant classes → MidiPlayer only. Setters that rebuild the timeline are only touched on an actual
    // change — with KeepBase on, the rebuild keeps T (MidiPlayer.Link.cs).
    private void ApplyLinkPlayerSettings()
    {
        var s = _lkSet;
        var p = _bandPlayer;
        p.KeepBase       = true;
        p.PreBufferedNet = true;   // forced while linked (protocol §3)
        if (p.TempoPct != 100) p.TempoPct = 100;   // tempo is room-wide, fixed at 100 % in v1

        int tr = Math.Clamp(s.Transpose ?? 0, -24, 24);
        if (tr != _lkTranspose || p.Transpose != tr) { _lkTranspose = tr; p.Transpose = tr; }
        int hold = s.HoldMs ?? 0;                          if (p.HoldOffsetMs != hold) p.HoldOffsetMs = hold;
        int poly = Math.Clamp(s.MaxPoly ?? 16, 1, 16);     if (p.MaxPolyphony != poly) p.MaxPolyphony = poly;
        int gap  = Math.Clamp(s.RestrikeGapMs ?? 16, 0, 100); if (p.RestrikeGapMs != gap) p.RestrikeGapMs = gap;
        bool sus = s.ForceSustain ?? false;                if (p.ForceSustain != sus) p.ForceSustain = sus;

        p.NetLookaheadMs       = s.NetAheadMs ?? 1500;
        p.NetBatchMs           = s.NetBatchMs ?? 100;
        p.ToneLeadMs           = s.ToneLeadMs ?? 120;
        p.RelayToneToListeners = s.RelayTone ?? false;
        bool tone = s.ApplyTone ?? true;                   if (p.ApplyToneTechnique != tone) p.ApplyToneTechnique = tone;

        // Personal listening settings: null = the player's own (their saved Maestro mute; the game's own saved
        // ear-return volume, which Maestro's monitor slider writes through to).
        p.MuteLocal = s.MuteLocal ?? _bandMuteLocal;
        if (s.MonitorVolume is int mv) { ApplyMonitorVolumeLive(Math.Clamp(mv, 0, 100)); _lkMonitorTouched = true; }
        else if (_lkMonitorTouched) { _lkMonitorTouched = false; RestoreOwnMonitorVolume(); }
    }

    // Disconnect: hand the player back exactly as the user's own settings have it.
    private void RestoreLocalPlayerSettings()
    {
        var p = _bandPlayer;
        p.KeepBase    = false;
        p.EnsembleGrid = false;
        p.HoldOffsetMs = _bandHoldMs;
        p.ForceSustain = _bandForceSustain;
        p.TempoPct     = _bandTempoPct;
        p.MaxPolyphony = _bandMaxPoly;
        p.PreBufferedNet = _bandNetPrebuffer;
        p.RestrikeGapMs  = _bandRestrikeGapMs;
        p.MuteLocal      = _bandMuteLocal;
        p.ApplyToneTechnique   = _bandApplyTone;
        p.RelayToneToListeners = _bandRelayTone;
        p.NetLookaheadMs = _bandAheadMs;
        p.NetBatchMs     = _bandBatchMs;
        p.ToneLeadMs     = _bandToneLeadMs;
        p.Transpose      = _bandTranspose;
        _lkSet = new LinkSettings();
        _lkTranspose = 0;
        if (_lkMonitorTouched) { _lkMonitorTouched = false; RestoreOwnMonitorVolume(); }
    }

    // Re-apply the GAME's own persisted ear-return value (a room value is applied live only, never saved).
    private void RestoreOwnMonitorVolume()
        => _services.Lua.DoString(
            "pcall(function() local v=Z.LocalUserDataMgr.GetFloatByLua(E.LocalUserDataType.Device, '" + MonitorVolKey + "', 0) " +
            "local svc=Z.DIServiceMgr.InstrumentService " +
            "if svc~=nil then svc:SetInstrumentVolume(Panda.ZGame.EInstrumentVolumeType.IntToEnum(" + MonitorVolChannel + "), v) end end)");

    // Live-only monitor volume: same call as ApplyMonitorVolume minus the LocalUserDataMgr write, so the room's value
    // never lands in the player's saved game setting.
    private void ApplyMonitorVolumeLive(int v)
        => _services.Lua.DoString(
            "pcall(function() local svc=Z.DIServiceMgr.InstrumentService " +
            "if svc~=nil then svc:SetInstrumentVolume(Panda.ZGame.EInstrumentVolumeType.IntToEnum(" + MonitorVolChannel + "), " +
            v.ToString(CultureInfo.InvariantCulture) + ") end end)");

    // ---- status helpers ----

    private bool LinkResting
    {
        get
        {
            var s = _lkRoomSong ?? _lkPrepSong;
            return s != null && s.StemUrl == null;
        }
    }

    // Server instant at which this play's first position (P) sounds.
    private static long LinkStartInstant(LinkSong s) => s.StartAtServerMs + s.StartPositionMs;

    private string LinkStateName()
    {
        if (LinkEquipBusy) return "equipping";
        if (_lkStoppedByPlayer) return "stopped_by_player";
        if (_lkPartActive)
            return _lkRoomSong != null && _services.CombatSnapshot.ServerNowMs < LinkStartInstant(_lkRoomSong) ? "countin" : "playing";
        if (_lkPaused) return "idle";   // paused = idle with songId + the paused position
        var s = _lkRoomSong ?? _lkPrepSong;
        if (s == null || s.StemUrl == null) return "idle";   // resting = idle with songId set
        if (!_lkStems.TryGetValue(s.SongId, out var e)) return "idle";
        if (e.Loading) return "preparing";
        if (e.Song == null) return "idle";
        return s == _lkRoomSong && LinkKey(s) == _lkPartDoneKey ? "idle" : "ready";
    }

    private string? LinkStatusSongId() => (_lkRoomSong ?? _lkPrepSong)?.SongId;

    // Room song position: the paused position while paused; P during a count-in; else serverNow − T.
    private int LinkPositionMs()
    {
        var s = _lkRoomSong;
        if (s == null) return 0;
        if (_lkPaused) return Math.Clamp(_lkPausedPosMs, 0, Math.Max(0, s.DurationMs));
        long now = _services.CombatSnapshot.ServerNowMs;
        if (now <= 0) return s.StartPositionMs;
        return (int)Math.Clamp(Math.Max(now - s.StartAtServerMs, s.StartPositionMs), 0, Math.Max(0, s.DurationMs));
    }
}
