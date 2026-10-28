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
    private bool _lkPaused;               // stop {paused:true}: song kept on screen at _lkPausedPosMs, part stopped
    private int  _lkPausedPosMs;

    // A play is identified by song + T + P: same song with a different T (or P) = seek / resume = a NEW play.
    private static string LinkKey(LinkSong s)
        => s.SongId + "|" + s.StartAtServerMs.ToString(CultureInfo.InvariantCulture) + "|" + s.StartPositionMs.ToString(CultureInfo.InvariantCulture);

    // ---- server messages ----

    private void OnLinkPrepare(LinkSong song)
    {
        // A `prepare` for the song that is RUNNING (stem re-assigned back to us mid-song, or rejoin): it updates the
        // running song's part — with a stem, LinkPartTick late-joins it; it is not the "next" song.
        if (_lkRoomSong != null && _lkRoomSong.SongId == song.SongId)
        {
            if (_lkRoomSong.StemUrl != song.StemUrl && !_lkPartActive)
            {
                _lkRoomSong = _lkRoomSong.WithStem(song.StemUrl);
                _lkRoomSong.InstrumentId = song.InstrumentId;
                _lkRoomSong.ToneId = song.ToneId;
                _lkPartDoneKey = "";
                _lkStartAccumMs = double.MaxValue;
            }
        }
        else _lkPrepSong = song;
        LinkSongEquip(song, fromPrepare: true);   // may hold the prepare ack until a per-song switch is done
        EnsureStem(song.SongId, song.StemUrl, ackPrepare: true);
    }

    // stop {scope:"part"}: only OUR stem of the running song was re-assigned away. Stop the part (StopLinked releases
    // what listeners hold) but keep the song on screen as "resting" (stemUrl null → idle + songId in status). A later
    // prepare/play for the same song with a stemUrl late-joins normally (OnLinkPrepare above / OnLinkPlay).
    private void OnLinkStopPart()
    {
        StopLinkedPart();
        _lkStoppedByPlayer = false;
        _lkPartDoneKey = "";
        _lkPlayAckKey = "";
        if (_lkRoomSong != null) _lkRoomSong = _lkRoomSong.WithStem(null);
    }

    private void OnLinkPlay(LinkSong song)
    {
        string key = LinkKey(song);
        // The SAME song + T + P re-sent (reconnect / rejoin): not a "next play" — keep playing, keep a player stop or a
        // finished part as they are, just refresh title/next/stem url (and re-equip if the song's equip changed).
        if (!_lkPaused && _lkRoomSong != null && LinkKey(_lkRoomSong) == key
            && (_lkPartActive || _lkStoppedByPlayer || _lkPartDoneKey == key))
        {
            _lkRoomSong = song;
            LinkSongEquip(song, fromPrepare: false);
            SendAck("play", song.SongId, true);
            return;
        }
        // New play — including the same song with a different T/P (seek / resume): drop the current part first
        // (StopLinked releases the notes listeners hold; what was already sent ahead still sounds — unavoidable).
        _lkStoppedByPlayer = false;
        _lkPaused = false;
        if (_lkPartActive) StopLinkedPart();

        _lkRoomSong    = song;
        _lkPartDoneKey = "";
        _lkPlayAckKey  = key;
        _lkPlayAckFailed = false;
        if (_lkPrepSong?.SongId == song.SongId) _lkPrepSong = null;
        if (song.StemUrl == null) { AckPlay(true); return; }   // no part this song: rest, stay in sync for the next one
        LinkSongEquip(song, fromPrepare: false);   // its own song → switches right away; the part starts after
        EnsureStem(song.SongId, song.StemUrl, ackPrepare: false);
        _lkStartAccumMs = double.MaxValue;   // try on the next tick
    }

    // stop {scope:"room"}: room-wide stop — or, with paused:true, a PAUSE: stop the part but keep the song and its
    // position on screen ("Paused"); the resume arrives as a `play` for the same song with a new T/P.
    private void OnLinkStop(bool paused = false, int positionMs = 0)
    {
        StopLinkedPart();
        _lkStoppedByPlayer = false;
        _lkPlayAckKey = "";
        if (paused && _lkRoomSong != null)
        {
            _lkPaused = true;
            _lkPausedPosMs = positionMs;
            return;
        }
        _lkPaused = false;
        _lkRoomSong = null;
        _lkPrepSong = null;
    }

    // welcome.paused (reconnect while the room is paused): rebuild the Paused view. The message has no durationMs /
    // stemUrl — keep what we already knew about that song (same songId), else duration 0 until the resume `play`.
    private void OnLinkWelcomePaused(LinkSong p, int positionMs)
    {
        StopLinkedPart();
        var known = _lkRoomSong?.SongId == p.SongId ? _lkRoomSong : _lkPrepSong?.SongId == p.SongId ? _lkPrepSong : null;
        if (known != null) { p.DurationMs = known.DurationMs; p.StemUrl = known.StemUrl; p.InstrumentId = known.InstrumentId; p.ToneId = known.ToneId; }
        _lkRoomSong = p;
        if (_lkPrepSong?.SongId == p.SongId) _lkPrepSong = null;
        _lkStoppedByPlayer = false;
        _lkPlayAckKey = "";
        _lkPaused = true;
        _lkPausedPosMs = positionMs;
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
        _lkStoppedByPlayer = false; _lkPartActive = false; _lkPaused = false;
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
            e.AckPrepare = true;
            TryAckPrepare(songId, e);
            return;
        }
        e = new LinkStem { Url = url, Order = ++_lkStemOrder, AckPrepare = ackPrepare };
        _lkStems[songId] = e;
        TrimStemCache();
        if (url == null) { TryAckPrepare(songId, e); return; }

        e.Loading = true;
        Uri uri;
        try { uri = new Uri(_lkApiBase, url); }   // absolute stemUrl wins; a path resolves against the API origin
        catch { e.Loading = false; e.Error = "download_failed"; TryAckPrepare(songId, e); return; }
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

    // Ack a `prepare` once its stem is settled (parsed / failed / no part) AND no per-song equip switch is still owed
    // for it (protocol batch 1: the ack is held until the switch is done — LinkEquipTick finishes it).
    private void TryAckPrepare(string songId, LinkStem e)
    {
        if (!e.AckPrepare || e.Loading || songId == _lkEquipForSong) return;
        e.AckPrepare = false;
        SendAck("prepare", songId, e.Error == null, e.Error);
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
            TryAckPrepare(r.Id, e);
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
        if (song != null && !_lkPaused && !_lkPartActive && now > 0 && now > song.StartAtServerMs + song.DurationMs + 1500)
            _lkRoomSong = song = null;   // the room's song is over (server schedules the next at end + 2 s)

        LinkPendingEquipTick();   // a per-song switch held for "after the current song" runs once nothing is in progress

        if (song == null || _lkPaused || song.StemUrl == null || _lkPartActive || _lkStoppedByPlayer || LinkEquipBusy) return;
        if (LinkKey(song) == _lkPartDoneKey) return;
        _lkStartAccumMs += dt * 1000.0;
        if (_lkStartAccumMs < 500) return;   // preconditions read Lua — ~2 Hz retry is plenty
        _lkStartAccumMs = 0;
        TryStartLinkedPart(song);
    }

    // True while a room song we have a part in is running (until its end + 1.5 s clears _lkRoomSong) — a per-song
    // equip switch must not run then ("never mid-song"), even if our own stem already ended (shorter stem): the
    // character visibly swapping instruments mid-song would read as a glitch. A PAUSE also counts as in progress
    // (protocol: a held switch runs only when the song really ends — a non-paused room stop, or the next song's play).
    // Resting / player-stopped = free.
    private bool LinkSongInProgress()
        => _lkPartActive
           || (_lkRoomSong != null && (_lkPaused || (_lkRoomSong.StemUrl != null && !_lkStoppedByPlayer)));

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
        long T    = song.StartAtServerMs;                   // song position x sounds at T + x
        int  P    = song.StartPositionMs;                   // this play starts at P (seek / resume; 0 = top)
        long lead = _bandPlayer.NetLookaheadMs + 300;       // the first note must be sendable a full lookahead early
        // Protocol §2 `play`: P sounds at T + P. LATE JOIN when now > T + P − lead: start at now − T + lead instead,
        // base still T. Either way nothing before the start position is played or sent (MidiPlayer.SkipToKeepBase).
        long late = now - T + lead;
        int startPos = now > T + P - lead ? (int)Math.Min(int.MaxValue, Math.Max(late, P)) : P;
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
        _services.Log.Info($"[MaestroLink] part start song={song.SongId} T={T} P={P} now={now} startPos={startPos}ms" +
                           (startPos > P ? " (LATE JOIN)" : $" (count-in {T + P - now}ms)"));
    }
}
