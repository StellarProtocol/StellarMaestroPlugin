using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;

namespace Stellar.Maestro;

// Maestro Link — songs (prepare / play / stop), the part lifecycle, and the linked settings overrides.
//
// Part lifecycle: the room's running song (_lkRoomSong, from `play` or `welcome.song`) is the TRUTH; our part is just
// "are we currently performing it". LinkPartTick (re)starts the part whenever it should be running and isn't — that one
// path covers a normal start, a late join, a reconnect, the band view being reopened, and the rejoin after a re-equip.
// A part that ended naturally (or was stopped by the player) is latched so it never restarts within the same song.
public sealed partial class Plugin
{
    private sealed class LinkStem
    {
        public string? Url;          // null = no part in this song (resting)
        public bool Loading;
        public MidiSong? Song;
        public string? Error;        // ack error code: download_failed / bad_midi
        public bool AckPrepare;      // a `prepare` is waiting for this download's result
        public long Order;
    }

    private static readonly HttpClient LinkHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const int LinkStemMaxBytes = 2 * 1024 * 1024;   // server caps stems at 1 MB; refuse anything absurd
    private const int LinkStemCacheMax = 4;

    private readonly Dictionary<string, LinkStem> _lkStems = new();
    private readonly ConcurrentQueue<(string Id, string Url, MidiSong? Song, string? Error)> _lkStemResults = new();
    private long _lkStemOrder;

    private LinkSong? _lkRoomSong;        // the room's running song (cleared on stop / after it ends)
    private LinkSong? _lkPrepSong;        // last `prepare` (the upcoming song, shown before its `play`)
    private bool   _lkStoppedByPlayer;    // "Stop my part" — stays silent until the next `play`
    private bool   _lkPartActive;         // our player is performing _lkPartKey
    private string _lkPartKey = "", _lkPartDoneKey = "";
    private string _lkPlayAckKey = "";    // `play` (songId|T) whose outcome hasn't been acked yet
    private bool   _lkPlayAckFailed;      // that play was acked failed — ack ok if it later starts
    private double _lkStartAccumMs;

    private LinkSettings _lkSet = new();
    private int  _lkTranspose;
    private bool _lkMonitorTouched;

    private static string LinkKey(LinkSong s) => s.SongId + "|" + s.StartAtServerMs.ToString(CultureInfo.InvariantCulture);

    // ---- server messages ----

    private void OnLinkPrepare(LinkSong song)
    {
        _lkPrepSong = song;
        EnsureStem(song.SongId, song.StemUrl, ackPrepare: true);
    }

    private void OnLinkPlay(LinkSong song)
    {
        string key = LinkKey(song);
        // The SAME song + T re-sent (reconnect / rejoin): not a "next play" — keep playing, keep a player stop or a
        // finished part as they are, just refresh title/next/stem url.
        if (_lkRoomSong != null && LinkKey(_lkRoomSong) == key
            && (_lkPartActive || _lkStoppedByPlayer || _lkPartDoneKey == key))
        {
            _lkRoomSong = song;
            SendAck("play", song.SongId, true);
            return;
        }
        _lkStoppedByPlayer = false;
        if (_lkPartActive) StopLinkedPart();

        _lkRoomSong    = song;
        _lkPartDoneKey = "";
        _lkPlayAckKey  = key;
        _lkPlayAckFailed = false;
        if (_lkPrepSong?.SongId == song.SongId) _lkPrepSong = null;
        if (song.StemUrl == null) { AckPlay(true); return; }   // no part this song: rest, stay in sync for the next one
        EnsureStem(song.SongId, song.StemUrl, ackPrepare: false);
        _lkStartAccumMs = double.MaxValue;   // try on the next tick
    }

    private void OnLinkStop()
    {
        StopLinkedPart();
        _lkRoomSong = null;
        _lkPrepSong = null;
        _lkStoppedByPlayer = false;
        _lkPlayAckKey = "";
    }

    // "Stop my part": client-local; reported as stopped_by_player until the next `play`.
    private void StopMyPart()
    {
        if (!_lkPartActive) return;
        StopLinkedPart();
        _lkStoppedByPlayer = true;
    }

    private void StopLinkedPart()
    {
        if (!_lkPartActive) return;
        _lkPartActive = false;
        _bandPlayer.StopLinked();
        _bandPlayer.ConsumeCompleted();
    }

    private void LinkResetSongState()
    {
        _lkRoomSong = null; _lkPrepSong = null;
        _lkStoppedByPlayer = false; _lkPartActive = false;
        _lkPartKey = ""; _lkPartDoneKey = ""; _lkPlayAckKey = "";
        _lkStems.Clear();
        while (_lkStemResults.TryDequeue(out _)) { }
    }

    private void AckPlay(bool ok, string? error = null)
    {
        if (_lkPlayAckKey.Length == 0 || _lkRoomSong == null) return;
        if (!ok && _lkPlayAckFailed) return;   // already told the server; don't repeat every retry
        SendAck("play", _lkRoomSong.SongId, ok, error);
        if (ok) _lkPlayAckKey = ""; else _lkPlayAckFailed = true;
    }

    // ---- stems (download + parse off the main thread) ----

    private void EnsureStem(string songId, string? url, bool ackPrepare)
    {
        if (_lkStems.TryGetValue(songId, out var e) && e.Url == url && e.Error == null)
        {
            if (!ackPrepare) return;
            if (e.Loading) e.AckPrepare = true; else SendAck("prepare", songId, true);
            return;
        }
        e = new LinkStem { Url = url, Order = ++_lkStemOrder, AckPrepare = ackPrepare };
        _lkStems[songId] = e;
        TrimStemCache();
        if (url == null) { if (ackPrepare) SendAck("prepare", songId, true); e.AckPrepare = false; return; }

        e.Loading = true;
        Uri uri;
        try { uri = new Uri(_lkApiBase, url); }
        catch { e.Loading = false; e.Error = "download_failed"; if (ackPrepare) SendAck("prepare", songId, false, e.Error); return; }
        string title = (_lkRoomSong?.SongId == songId ? _lkRoomSong.Title : _lkPrepSong?.Title) ?? songId;
        Task.Run(async () =>
        {
            byte[] bytes;
            try { bytes = await LinkHttp.GetByteArrayAsync(uri).ConfigureAwait(false); }
            catch { _lkStemResults.Enqueue((songId, url, null, "download_failed")); return; }
            if (bytes.Length < 14 || bytes.Length > LinkStemMaxBytes || bytes[0] != 'M' || bytes[1] != 'T' || bytes[2] != 'h' || bytes[3] != 'd')
            { _lkStemResults.Enqueue((songId, url, null, "bad_midi")); return; }
            try { _lkStemResults.Enqueue((songId, url, MidiParser.Parse(bytes, title), null)); }   // pure C#, no game access
            catch { _lkStemResults.Enqueue((songId, url, null, "bad_midi")); }
        });
    }

    private void TrimStemCache()
    {
        while (_lkStems.Count > LinkStemCacheMax)
        {
            string? oldest = null; long o = long.MaxValue;
            foreach (var kv in _lkStems)
                if (kv.Value.Order < o && kv.Key != _lkRoomSong?.SongId && kv.Key != _lkPrepSong?.SongId) { o = kv.Value.Order; oldest = kv.Key; }
            if (oldest == null) return;
            _lkStems.Remove(oldest);
        }
    }

    private void DrainStemResults()
    {
        while (_lkStemResults.TryDequeue(out var r))
        {
            if (!_lkStems.TryGetValue(r.Id, out var e) || e.Url != r.Url) continue;   // superseded
            e.Loading = false; e.Song = r.Song; e.Error = r.Error;
            if (r.Error != null) _services.Log.Warning($"[MaestroLink] stem {r.Id}: {r.Error}");
            if (e.AckPrepare) { SendAck("prepare", r.Id, r.Error == null, r.Error); e.AckPrepare = false; }
        }
    }

    // ---- part tick ----

    private void LinkPartTick(float dt)
    {
        if (!IsLinked) return;
        if (_lkPartActive && !_bandPlayer.IsPlaying)   // our part ended on its own
        {
            _bandPlayer.ConsumeCompleted();
            _lkPartActive  = false;
            _lkPartDoneKey = _lkPartKey;
        }
        long now = _services.CombatSnapshot.ServerNowMs;
        var song = _lkRoomSong;
        if (song != null && !_lkPartActive && now > 0 && now > song.StartAtServerMs + song.DurationMs + 1500)
            _lkRoomSong = song = null;   // the room's song is over (server schedules the next at end + 2 s)

        if (!_lkPartActive) LinkPendingEquipTick();

        if (song == null || song.StemUrl == null || _lkPartActive || _lkStoppedByPlayer || LinkEquipBusy) return;
        if (LinkKey(song) == _lkPartDoneKey) return;
        _lkStartAccumMs += dt * 1000.0;
        if (_lkStartAccumMs < 500) return;   // preconditions read Lua — ~2 Hz retry is plenty
        _lkStartAccumMs = 0;
        TryStartLinkedPart(song);
    }

    private void TryStartLinkedPart(LinkSong song)
    {
        if (!_lkStems.TryGetValue(song.SongId, out var stem) || stem.Url != song.StemUrl)
        { EnsureStem(song.SongId, song.StemUrl, ackPrepare: false); return; }
        if (stem.Loading) return;
        if (stem.Song == null) { AckPlay(false, stem.Error ?? "download_failed"); _lkPartDoneKey = LinkKey(song); return; }

        if (_services.CombatSnapshot.ServerNowMs <= 0) { AckPlay(false, "not_synced"); return; }
        if (ReadPlayerInstrumentId() <= 0) { AckPlay(false, "no_instrument"); return; }
        if (!IsBandViewOpen()) { AckPlay(false, "band_view_closed"); return; }

        ApplyLinkPlayerSettings();
        _bandPlayer.Load(stem.Song);
        long now  = _services.CombatSnapshot.ServerNowMs;   // read AFTER Load (Load/Stop can take real time)
        long T    = song.StartAtServerMs;
        long lead = _bandPlayer.NetLookaheadMs + 300;       // note 0 must be sendable a full lookahead early
        // LATE JOIN (protocol §2 `play`): too close / past T → start at song position now − T + lead, base still T.
        int startPos = now > T - lead ? (int)Math.Min(int.MaxValue, now - T + lead) : 0;
        if (startPos >= _bandPlayer.DurationMs)
        {
            _lkPartDoneKey = LinkKey(song);   // nothing of our part is left to play
            AckPlay(true);
            return;
        }
        _bandPlayer.StartLinked(T, now, startPos, _lkTranspose);
        _lkPartActive = true;
        _lkPartKey    = LinkKey(song);
        AckPlay(true);
        LinkDiagRow($"START\t{now}\t{song.SongId}\t{T}\t{startPos}");
        _services.Log.Info($"[MaestroLink] part start song={song.SongId} T={T} now={now} startPos={startPos}ms" +
                           (startPos > 0 ? " (LATE JOIN)" : $" (count-in {T - now}ms)"));
    }

    // ---- settings ----

    // Live/network/instant classes → MidiPlayer only (never the Plugin fields or config). Setters that rebuild the
    // timeline are only touched on an actual change — with KeepBase on, the rebuild keeps T (MidiPlayer.Link.cs).
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

        p.MuteLocal = s.MuteLocal ?? false;
        if (s.MonitorVolume is int mv) { ApplyMonitorVolumeLive(Math.Clamp(mv, 0, 100)); _lkMonitorTouched = true; }
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
        if (_lkMonitorTouched)
        {
            _lkMonitorTouched = false;
            // Re-apply the GAME's own persisted ear-return value (the room's value was applied live only, never saved).
            _services.Lua.DoString(
                "pcall(function() local v=Z.LocalUserDataMgr.GetFloatByLua(E.LocalUserDataType.Device, '" + MonitorVolKey + "', 0) " +
                "local svc=Z.DIServiceMgr.InstrumentService " +
                "if svc~=nil then svc:SetInstrumentVolume(Panda.ZGame.EInstrumentVolumeType.IntToEnum(" + MonitorVolChannel + "), v) end end)");
        }
    }

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

    private string LinkStateName()
    {
        if (LinkEquipBusy) return "equipping";
        if (_lkStoppedByPlayer) return "stopped_by_player";
        if (_lkPartActive)
            return _services.CombatSnapshot.ServerNowMs < (_lkRoomSong?.StartAtServerMs ?? 0) ? "countin" : "playing";
        var s = _lkRoomSong ?? _lkPrepSong;
        if (s == null || s.StemUrl == null) return "idle";   // resting = idle with songId set
        if (!_lkStems.TryGetValue(s.SongId, out var e)) return "idle";
        if (e.Loading) return "preparing";
        if (e.Song == null) return "idle";
        return s == _lkRoomSong && LinkKey(s) == _lkPartDoneKey ? "idle" : "ready";
    }

    private string? LinkStatusSongId() => (_lkRoomSong ?? _lkPrepSong)?.SongId;

    private int LinkPositionMs()
    {
        var s = _lkRoomSong;
        long now = _services.CombatSnapshot.ServerNowMs;
        if (s == null || now <= 0) return 0;
        return (int)Math.Clamp(now - s.StartAtServerMs, 0, Math.Max(0, s.DurationMs));
    }
}
