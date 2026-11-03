using System;

namespace Stellar.Maestro;

// Maestro Link — equipping (protocol §7 "live equip model": the owner sees and controls what each character REALLY
// holds; songs always say explicitly what to hold). Three sources, nothing else ever equips:
//   • `equip {instrumentId, toneId, playRange}` — owner's "equip now": runs immediately (also mid-song: the part stops,
//     the character re-equips and late-joins the running song on the new equip; also while paused);
//   • the concrete equip carried by `prepare` / `play` / `welcome.song` for a song we have a part in — switched
//     between songs, never mid-song (LinkSongEquip);
//   • `settings.instrumentId/toneId` are pre-fill defaults for the web only → NEVER equip from them. `playRange` is
//     stored and used at the next equip.
//
// Recipe = port of StellarExperimentPlugin's Instrument Setup (KB Band-Instrument-Playback.md → "Equipping FROM CODE" +
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
    private bool   _lkEquipAckEquip;            // this run answers an `equip` message (ack of:"equip")
    private long   _lkEquipStartMs, _lkEquipPollMs;
    private LinkEquipTarget  _lkEquipTarget;
    private LinkEquipTarget? _lkPendingEquip;   // queued behind a busy run, or held until the current song ends
    private bool   _lkPendingAckEquip;
    private bool   _lkPendingAfterSong;         // held until no song is in progress (LinkSongInProgress)
    private int    _lkEquippedInst, _lkEquippedTone;   // our last SUCCESSFUL equip (KnownTone); 0 = none / unknown
    private string _lkEquipForSong = "";        // `prepare` whose ack waits for a per-song switch (TryAckPrepare)
    private LinkEquipTarget _lkEquipForSongTarget;

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

    // RESOLVED target (toneId 0 = the instrument's default tone) — compare these, never raw ids.
    private LinkEquipTarget TargetFor(int inst, int? tone)
        => new(inst, tone is int t && t != 0 ? t : DefaultTone(inst), (_lkSet.PlayRange ?? "all").ToLowerInvariant());

    // `settings`: store + apply the playback classes. instrumentId/toneId are web pre-fill defaults (§7.2) — never an
    // equip; playRange is picked up by the next equip (TargetFor).
    private void OnLinkSettings(LinkSettings s, bool fromWelcome = false)
    {
        _lkSet = s;   // each `settings` carries the full PlayerSettings
        ApplyLinkPlayerSettings();
        if (!fromWelcome) SendAck("settings", null, true);
    }

    // `equip` (§7.3): re-equip NOW, one-off. If our part runs, it stops; LinkPartTick late-joins on the new equip. The
    // running song's equip is updated too (the server stores the same override for rejoin / welcome.song / play).
    private void OnLinkEquip(int inst, int? tone, string? range)
    {
        if (!string.IsNullOrEmpty(range)) _lkSet.PlayRange = range;
        var want = TargetFor(inst, tone);
        if (_lkRoomSong is { StemUrl: not null } rs) { rs.InstrumentId = want.Inst; rs.ToneId = want.Tone; }
        if (_lkEquipBusy && _lkEquipTarget == want) { LogEquipSkip("busy", want); _lkEquipAckEquip = true; return; }
        if (!_lkEquipBusy && EquippedMatches(want)) { LogEquipSkip("matches", want); SendAck("equip", null, true); return; }
        _lkPendingEquip = null;
        StartLinkEquip(want, ackEquip: true);
    }

    // Per-song equip (§7.4): `prepare`/`play`/`welcome.song` carry the concrete equip for a song we have a part in.
    //   • for the running song (rejoin / re-assigned back / override) → switch now, the part rejoins in sync after;
    //   • for the NEXT song while the current one is in progress → only after it ends — never mid-song;
    //   • nothing in progress → switch now.
    // A `prepare` that needs a switch has its ack held until the switch finishes (ok, or equip_failed).
    private void LinkSongEquip(LinkSong song, bool fromPrepare)
    {
        if (song.InstrumentId is not int inst || song.StemUrl == null) return;   // no part → nothing to equip
        var want = TargetFor(inst, song.ToneId);
        if (_lkEquipBusy && _lkEquipTarget == want)
        {
            if (fromPrepare) { _lkEquipForSong = song.SongId; _lkEquipForSongTarget = want; }
            LogEquipSkip("busy", want);
            return;
        }
        if (!_lkEquipBusy && EquippedMatches(want))
        {
            if (_lkPendingEquip != null && fromPrepare) _lkPendingEquip = null;
            LogEquipSkip("matches", want);
            return;
        }

        if (fromPrepare) { _lkEquipForSong = song.SongId; _lkEquipForSongTarget = want; }
        bool forRunning = _lkRoomSong != null && _lkRoomSong.SongId == song.SongId;
        if (!forRunning && LinkSongInProgress())
        {
            _lkPendingEquip = want; _lkPendingAckEquip = false; _lkPendingAfterSong = true;
            LogEquipSkip("pending-after-song", want);
            return;
        }
        _lkPendingEquip = null;
        StartLinkEquip(want, ackEquip: false);
    }

    // The tone currently on the character as far as we can KNOW it: the tone of our last successful equip, while the
    // character still holds that instrument. 0 = unknown (status reports null; an equip target then re-equips).
    //
    // ⚠️ Do NOT decide from EntityInstrumentGetTone(ent, true): Maestro's "apply tone from MIDI" (ON while linked) sets
    // the LOCAL tone per the stem's program at play time (EntityInstrumentSetTone(…, true) — MidiPlayer.Effect.cs), so
    // the read-back is the MIDI-driven tone (Clean for most stems), not the equip tone everyone hears. That made a
    // switch to Clean look "already equipped" and get skipped (fixed f682567).
    private int KnownTone() => _lkEquippedInst != 0 && _lkInstId == _lkEquippedInst ? _lkEquippedTone : 0;

    // Live check (instrument id + KNOWN tone + the CurPlayRange the next equip would use). Unknown tone → no match:
    // re-equipping once is cheap; skipping a needed switch is the bug above.
    private bool EquippedMatches(LinkEquipTarget w)
    {
        RefreshLinkLiveState();
        _services.Lua.DoString("pcall(function() rawset(_G,'__ml_range',nil) rawset(_G,'__ml_range', Z.DataMgr.Get('band_data').CurPlayRange) end)");
        _lkLiveRange = _services.Lua.TryReadGlobalNumber("__ml_range", out double r) ? (int)r : -1;
        bool rangeOk = _lkLiveRange < 0 || _lkLiveRange == RangeIndex(w.Range);
        int tone = KnownTone();
        return _lkInstId == w.Inst && tone != 0 && tone == w.Tone && rangeOk;
    }

    private int _lkLiveRange = -1;

    private void LogEquipSkip(string reason, LinkEquipTarget want)
        => _services.Log.Info($"[MaestroLink] equip skip reason={reason} want={want.Inst}/{want.Tone}/{want.Range} " +
                              $"live={_lkInstId}/{KnownTone()}(srv {_lkServerTone})/{_lkLiveRange}");

    private void LinkPendingEquipTick()
    {
        if (_lkPendingEquip is not { } p || _lkEquipBusy || (_lkPendingAfterSong && LinkSongInProgress())) return;
        _lkPendingEquip = null;
        StartLinkEquip(p, ackEquip: _lkPendingAckEquip);
    }

    private void StartLinkEquip(LinkEquipTarget w, bool ackEquip)
    {
        if (_lkEquipBusy)   // finish the running one first, then this
        { _lkPendingEquip = w; _lkPendingAckEquip = ackEquip; _lkPendingAfterSong = false; return; }
        StopLinkedPart();   // our part drops; LinkPartTick rejoins the running song in sync afterwards
        _lkEquipBusy = true;
        _lkEquipAckEquip = ackEquip;
        _lkEquipTarget = w;
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
        if (done) { _lkEquippedInst = _lkEquipTarget.Inst; _lkEquippedTone = _lkEquipTarget.Tone; }
        else      { _lkEquippedInst = 0; _lkEquippedTone = 0; }   // a failed run may have left anything equipped
        if (_lkEquipAckEquip) SendAck("equip", null, done, done ? null : "equip_failed");
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
        _lkEquippedInst = 0; _lkEquippedTone = 0;
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
