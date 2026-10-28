using System;

namespace Stellar.Maestro;

// Maestro Link — re-equip (instrumentId / toneId / playRange are the "re-equip" timing class, protocol §3).
//
// Port of StellarExperimentPlugin's Instrument Setup recipe (KB Band-Instrument-Playback.md → "Equipping FROM CODE" +
// "SET TONE BEFORE EQUIP"; player-confirmed 2026-10-02). The tone is latched into every client's InstrumentPlayer at
// EQUIP time from the replicated AttrInstrumentSettings(1201), and Exit resets it to clean — so the strict order is
// Exit → set tone → equip, as ONE Lua coroutine so each RPC is awaited before the next fires.
// Equip goes through the game's own BandVM:OpenBandPerformanceView (NOT a bare AsyncEnterInstrumentState): an
// instrument that lands while the band view is closed is auto-exited by band_service.onPlayerInstrumentChanged. The
// view's OnRefresh sends the Enter itself with band_data.CurPlayRange — which is how playRange is applied: we set
// CurPlayRange (in memory only; the game loads it from LocalUserData at init and we never write that) right before
// opening the view, stash the player's own value in __ml_prevRange, and restore it on Disconnect.
// Progress comes back through ONE string sentinel (__ml_eq) read with ILua.ReadGlobalString — no Lua→C# callback.
public sealed partial class Plugin
{
    private readonly record struct LinkEquipTarget(int Inst, int Tone, string Range);

    private const long LinkEquipTimeoutMs = 25_000;   // Lua's own waits: exit ≤ 3.3 s + equip 15 s

    private bool   _lkEquipBusy;
    private bool   _lkEquipAck;                 // ack `settings` when this run finishes (not for welcome / per-song runs)
    private long   _lkEquipStartMs, _lkEquipPollMs;
    private LinkEquipTarget  _lkEquipTarget;
    private LinkEquipTarget? _lkPendingEquip;   // held until no song is in progress (LinkSongInProgress)
    private bool   _lkPendingAck;               // the pending run acks `settings`
    private bool   _lkPendingAfterSong;         // pending waits for the current song to end (else: just for the busy run)
    private LinkEquipTarget _lkEquipForSongTarget;
    private LinkEquipTarget? _lkLastEquipAttempt;
    private string _lkEquipForSong = "";        // `prepare` whose ack waits for a per-song switch (TryAckPrepare)

    private bool LinkEquipBusy => _lkEquipBusy;

    // Instrument default tone (BandMusicalInstrumentsTable.DefaultMusicalTimbre) — toneId 0 means "instrument default".
    private static int DefaultTone(int inst) => inst switch
    {
        10002 => 1000001,   // Electric Guitar → Clean
        10003 => 1000006,   // Drum Kit → Acoustic
        10004 => 1000004,   // Electric Bass → Clean
        _     => 1000000,   // Piano → Acoustic
    };

    private static int RangeIndex(string r) => r == "union" ? 1 : r == "team" ? 2 : 0;
    private static string RangeLua(string r) => r == "union" ? "Union" : r == "team" ? "Team" : "All";

    private LinkEquipTarget TargetFor(int inst, int? tone)
        => new(inst, tone is int t && t != 0 ? t : DefaultTone(inst), (_lkSet.PlayRange ?? "all").ToLowerInvariant());

    // The song that decides the equip right now: the running song if we have a part in it, else the prepared next
    // song (when nothing runs). Its RESOLVED instrument/tone (batch 1) wins over the player's defaults in `settings`.
    private LinkSong? LinkEquipSong()
    {
        if (_lkRoomSong != null) return _lkRoomSong.StemUrl != null && _lkRoomSong.InstrumentId != null ? _lkRoomSong : null;
        return _lkPrepSong is { StemUrl: not null, InstrumentId: not null } p ? p : null;
    }

    private void OnLinkSettings(LinkSettings s, string apply, bool fromWelcome = false)
    {
        _lkSet = s;   // each `settings` carries the full PlayerSettings
        ApplyLinkPlayerSettings();   // instant / live / network classes take effect right away

        // instrumentId/toneId are the player's DEFAULTS (protocol §3); a song with a resolved equip overrides them.
        var song = LinkEquipSong();
        var want = song != null ? TargetFor(song.InstrumentId!.Value, song.ToneId) : TargetFor(s.InstrumentId ?? 10001, s.ToneId);
        bool need = want != _lkLastEquipAttempt && !EquippedMatches(want);
        if (!need)
        {
            if (!fromWelcome) SendAck("settings", null, true);
            return;
        }
        // The server only sends apply:"now" (it holds "next" itself); a "next" still waits for the song to end.
        if (apply == "next" && LinkSongInProgress())
        {
            _lkPendingEquip = want; _lkPendingAck = true; _lkPendingAfterSong = true;
            _services.Log.Info($"[MaestroLink] re-equip queued for next song: {want}");
            return;
        }
        _lkPendingEquip = null;
        StartLinkEquip(want, ack: !fromWelcome);
    }

    // Per-song equip switch (protocol batch 1): `prepare`/`play` carry the RESOLVED instrument/tone for that song.
    //   • for the running song (rejoin / re-assigned back / play) → switch now, the part starts after;
    //   • for the NEXT song while the current one is in progress → only after it ends — never mid-song;
    //   • nothing in progress → switch now.
    // A `prepare` that needs a switch has its ack held until the switch finishes (ok, or equip_failed).
    private void LinkSongEquip(LinkSong song, bool fromPrepare)
    {
        if (song.InstrumentId is not int inst || song.StemUrl == null) return;   // no part → nothing to equip
        var want = TargetFor(inst, song.ToneId);
        if (_lkEquipBusy && _lkEquipTarget == want) { if (fromPrepare) { _lkEquipForSong = song.SongId; _lkEquipForSongTarget = want; } return; }
        if (!_lkEquipBusy && EquippedMatches(want)) { if (_lkPendingEquip != null && fromPrepare) _lkPendingEquip = null; return; }

        if (fromPrepare) { _lkEquipForSong = song.SongId; _lkEquipForSongTarget = want; }
        bool forRunning = _lkRoomSong != null && _lkRoomSong.SongId == song.SongId;
        if (!forRunning && LinkSongInProgress())
        {
            _lkPendingEquip = want; _lkPendingAck = false; _lkPendingAfterSong = true;
            _services.Log.Info($"[MaestroLink] switch to {want} after the current song (for {song.SongId})");
            return;
        }
        _lkPendingEquip = null;
        StartLinkEquip(want, ack: false);
    }

    // Live check against the game (instrument id + tone + the CurPlayRange the next equip would use). Tone 0 = not
    // readable → don't force an equip on that alone.
    private bool EquippedMatches(LinkEquipTarget w)
    {
        RefreshLinkLiveState();
        _services.Lua.DoString("pcall(function() rawset(_G,'__ml_range',nil) rawset(_G,'__ml_range', Z.DataMgr.Get('band_data').CurPlayRange) end)");
        bool rangeOk = !_services.Lua.TryReadGlobalNumber("__ml_range", out double r) || (int)r == RangeIndex(w.Range);
        return _lkInstId == w.Inst && (_lkToneId == 0 || _lkToneId == w.Tone) && rangeOk;
    }

    private void LinkPendingEquipTick()
    {
        if (_lkPendingEquip is not { } p || _lkEquipBusy || (_lkPendingAfterSong && LinkSongInProgress())) return;
        _lkPendingEquip = null;
        StartLinkEquip(p, ack: _lkPendingAck);
    }

    private void StartLinkEquip(LinkEquipTarget w, bool ack)
    {
        if (_lkEquipBusy) { _lkPendingEquip = w; _lkPendingAck = ack; _lkPendingAfterSong = false; return; }   // finish the running one first, then this
        StopLinkedPart();   // apply:"now" — our part drops; LinkPartTick rejoins the running song in sync afterwards
        _lkEquipBusy = true;
        _lkEquipAck = ack;
        _lkEquipTarget = w;
        _lkLastEquipAttempt = w;
        _lkEquipStartMs = Environment.TickCount64;
        _lkEquipPollMs = 0;
        _services.Log.Info($"[MaestroLink] equip start inst={w.Inst} tone={w.Tone} range={w.Range}");

        _services.Lua.DoString(
            "rawset(_G,'__ml_eq','start') pcall(function() " +
            "local T=" + w.Tone + " local IID=" + w.Inst + " " +
            "local vm=Z.VMMgr.GetVM('band') if vm==nil then rawset(_G,'__ml_eq','fail:init:no band vm') return end " +
            "local svc=Z.DIServiceMgr.InstrumentService local bd=Z.DataMgr.Get('band_data') " +
            "local key=Z.IsPCUI and 'band_performance_main_pc' or 'band_performance_main' " +
            "if rawget(_G,'__ml_prevRange')==nil then rawset(_G,'__ml_prevRange', bd.CurPlayRange) end " +
            "local step='init' local function st(s) step=s rawset(_G,'__ml_eq',s) end " +
            "Z.CoroUtil.create_coro_xpcall(function() " +
              "if svc:GetPlayerInstrumentId()~=0 then st('exit') " +
                "vm:AsyncExitInstrumentState(bd.CancelSource:CreateToken()) " +
                "local t=0 while (svc:GetPlayerInstrumentId()~=0 or Z.UIMgr:IsActive(key)) and t<3 do coroutine.wait(0.1) t=t+0.1 end " +
                "coroutine.wait(0.3) end " +
              "st('set') local ent=Z.EntityMgr.PlayerEnt " +
              "if ent~=nil then pcall(function() svc:EntityInstrumentSetTone(ent, T, true) end) end " +
              "vm:AsyncSetInstrumentSettings({[E.EInstrumentSetting.Tone]=T}, bd.CancelSource:CreateToken()) " +
              "bd.CurPlayRange=E.EInstrumentPlayRange." + RangeLua(w.Range) + " " +
              "st('equip') vm:OpenBandPerformanceView(E.BandPerformanceType.Normal, IID, 0) " +
              "local t=0 while svc:GetPlayerInstrumentId()~=IID and t<15 do coroutine.wait(0.2) t=t+0.2 end " +
              "if svc:GetPlayerInstrumentId()==IID then st('done') " +
              "else rawset(_G,'__ml_eq','fail:equip:not equipped after 15s') end " +
            "end, function(err) rawset(_G,'__ml_eq','fail:'..step..':'..tostring(err)) end)() end)");
    }

    // ~4 Hz sentinel poll while a run is in flight; idle = no Lua traffic.
    private void LinkEquipTick()
    {
        if (!_lkEquipBusy) return;
        long now = Environment.TickCount64;
        if (now - _lkEquipPollMs < 250) return;
        _lkEquipPollMs = now;

        var s = _services.Lua.ReadGlobalString("__ml_eq") ?? "";
        bool done = s == "done";
        bool fail = s.StartsWith("fail:", StringComparison.Ordinal) || (!done && now - _lkEquipStartMs > LinkEquipTimeoutMs);
        if (!done && !fail) return;

        _lkEquipBusy = false;
        _services.Log.Info($"[MaestroLink] equip {(done ? "done" : "FAILED")} ({s}) target={_lkEquipTarget}");
        if (_lkEquipAck) SendAck("settings", null, done, done ? null : "equip_failed");
        if (_lkEquipForSong.Length > 0 && _lkEquipTarget == _lkEquipForSongTarget) FinishSongEquip(done);
        _lkWarnAccumMs = 1000;    // refresh instrument/tone/band-view on the next tick
        _lkStartAccumMs = 1000;   // and rejoin the running song right away (late-join path)
    }

    // The per-song switch a `prepare` was waiting on has finished: release its ack — ok (once the stem is settled), or
    // equip_failed (the server then rests this part for that song with stop {scope:"part"}).
    private void FinishSongEquip(bool done)
    {
        string songId = _lkEquipForSong;
        _lkEquipForSong = "";
        if (!_lkStems.TryGetValue(songId, out var e)) { SendAck("prepare", songId, done, done ? null : "equip_failed"); return; }
        if (done) { TryAckPrepare(songId, e); return; }
        if (!e.AckPrepare) return;
        e.AckPrepare = false;
        SendAck("prepare", songId, false, "equip_failed");
    }

    // Disconnect: abandon a run in flight (the coroutine finishes harmlessly on its own) and give the player's own
    // listen range back to band_data.
    private void CancelLinkEquip()
    {
        _lkEquipBusy = false;
        _lkPendingEquip = null;
        _lkLastEquipAttempt = null;
        _lkEquipForSong = "";
        _services.Lua.DoString("pcall(function() local p=rawget(_G,'__ml_prevRange') if p~=nil then " +
                               "Z.DataMgr.Get('band_data').CurPlayRange=p rawset(_G,'__ml_prevRange',nil) end end)");
    }

    // ---- display names (status view Part row) ----

    private string LinkInstrumentName(int inst) => inst switch
    {
        10001 => _loc.T("mst.link.inst.piano"),
        10002 => _loc.T("mst.link.inst.guitar"),
        10003 => _loc.T("mst.link.inst.drums"),
        10004 => _loc.T("mst.link.inst.bass"),
        _     => "—",
    };

    private string LinkToneName(int tone) => tone switch
    {
        1000000 or 1000006 => _loc.T("mst.link.tone.acoustic"),
        1000001 or 1000004 => _loc.T("mst.link.tone.clean"),
        1000002 or 1000005 => _loc.T("mst.link.tone.overdrive"),
        1000003            => _loc.T("mst.link.tone.distortion"),
        _                  => "",
    };
}
