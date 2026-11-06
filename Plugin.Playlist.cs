using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Curated named playlists + a Now-Playing queue with auto-advance, loop (none/all/one) and shuffle.
// Playlists persist to config as JSON; each stores song filenames resolved against the midi/ folder at play time.
public sealed partial class Plugin
{
    private sealed class PlaylistDto
    {
        public string Name { get; set; } = "";
        public List<string> Songs { get; set; } = new();
    }

    private List<PlaylistDto> _playlists = new();
    private int  _plIdx;            // the VIEWED playlist (queue dropdown); persisted as "active_playlist"
    private int  _queueSel  = -1;   // selected row in the VIEWED playlist (remove / move / the Play target)
    // Playback is tracked separately from the view: _playingList is the playlist the playing song came from (an object
    // reference, so it survives renames and other playlists being added / deleted) and _nowPlaying its index there
    // (-1 = none). Switching the dropdown never stops or changes playback; auto next / loop / shuffle / ◀◀ ▶▶ /
    // "Next:" all walk the PLAYING list. Playback moves to another playlist only on an explicit Play from it.
    private PlaylistDto? _playingList;
    private int  _nowPlaying = -1;
    private bool _autoAdvance = true;
    private int  _loopMode;         // 0 = none, 1 = all, 2 = one
    private bool _shuffle;
    private int  _gapSec;           // silence between songs on auto-advance (seconds)
    private double _gapRemainMs = -1;  // >=0 while counting down the gap; -1 = idle
    private readonly Random _rng = new();

    private PlaylistDto Active => _playlists[Math.Clamp(_plIdx, 0, _playlists.Count - 1)];

    private bool ViewingPlaying => _playingList != null && ReferenceEquals(Active, _playingList);
    private List<string> PlayingSongs => (_playingList ?? Active).Songs;
    // The playing row as seen in the VIEWED list (-1 when another playlist is playing) — for the ▶ marker / selection.
    private int NowPlayingInView => QueueCursor.MarkerInView(_nowPlaying, ViewingPlaying);

    // ---- persistence ----
    private void LoadPlaylists()
    {
        try
        {
            var json = _cfg.Get<string>("playlists", "");
            if (!string.IsNullOrEmpty(json))
                _playlists = System.Text.Json.JsonSerializer.Deserialize<List<PlaylistDto>>(json) ?? new();
        }
        catch { _playlists = new(); }
        if (_playlists.Count == 0) _playlists.Add(new PlaylistDto { Name = "Playlist 1" });
        _plIdx       = Math.Clamp(_cfg.Get<int>("active_playlist", 0), 0, _playlists.Count - 1);
        _autoAdvance = _cfg.Get<bool>("auto_advance", true);
        _loopMode    = Math.Clamp(_cfg.Get<int>("loop_mode", 0), 0, 2);
        _shuffle     = _cfg.Get<bool>("shuffle", false);
        _gapSec      = Math.Clamp(_cfg.Get<int>("gap_sec", 0), 0, 15);
    }

    private void SavePlaylists()
    {
        _playlistNames = null;   // a playlist was added / removed / renamed → rebuild the dropdown's option list
        try { _cfg.Set<string>("playlists", System.Text.Json.JsonSerializer.Serialize(_playlists)); } catch { }
        _cfg.Set<int>("active_playlist", _plIdx);
        _cfg.Save();
    }

    // ---- playlist ops ----
    private void NewPlaylist()
    {
        _playlists.Add(new PlaylistDto { Name = $"Playlist {_playlists.Count + 1}" });
        _plIdx = _playlists.Count - 1;
        _queueSel = -1; _queueScrollReset = true;   // only the VIEW changes; playback continues
        SavePlaylists();
    }

    // Deleting the PLAYING playlist stops playback (the confirmation already warned); any other leaves it alone.
    private void DeleteActivePlaylist()
    {
        if (ViewingPlaying)
        {
            StopPlayback();
            _playingList = null; _nowPlaying = -1;
        }
        if (ShuffleListIs(Active)) ClearShuffle();
        if (_playlists.Count <= 1) { Active.Songs.Clear(); Active.Name = "Playlist 1"; }
        else { _playlists.RemoveAt(_plIdx); _plIdx = Math.Clamp(_plIdx, 0, _playlists.Count - 1); }
        _queueSel = -1; _queueScrollReset = true;
        SavePlaylists();
    }

    private void RenameActivePlaylist(string name)
    {
        Active.Name = string.IsNullOrWhiteSpace(name) ? Active.Name : name.Trim();
        SavePlaylists();
    }

    private void SelectPlaylistIndex(int i)
    {
        if (i < 0 || i >= _playlists.Count || i == _plIdx) return;
        _plIdx = i;
        _queueSel = -1; _queueScrollReset = true;   // only the VIEW changes; playback continues untouched
        SavePlaylists();
    }

    // ---- queue ops ----
    private void AddToActive(string filename)
    {
        Active.Songs.Add(filename);
        ShuffleOnAdded(Active, Active.Songs.Count - 1);   // joins the not-yet-played part of a running shuffle round
        SetBandStatus(_loc.TFormat("mst.status.added", System.IO.Path.GetFileNameWithoutExtension(filename)));
        SavePlaylists();
    }

    // A row's ✕: removes that entry (not necessarily the selected one); the selection and the playing marker follow
    // their songs.
    private void RemoveFromQueue(int i)
    {
        if (i < 0 || i >= Active.Songs.Count) return;
        Active.Songs.RemoveAt(i);
        ShuffleOnRemoved(Active, i);
        _nowPlaying = QueueCursor.AfterRemove(_nowPlaying, i, ViewingPlaying);   // only if this IS the playing list
        if (_queueSel == i) _queueSel = Active.Songs.Count == 0 ? -1 : Math.Min(i, Active.Songs.Count - 1);
        else if (_queueSel > i) _queueSel--;
        SavePlaylists();
    }

    // Drag-reorder of the viewed playlist (VirtualListElement.OnReorder: remove-then-insert ITEM indices, never a no-op).
    // The selection follows its song (the dragged one stays selected); the playing cursor follows only when this IS the
    // playing playlist.
    private void ReorderQueue(int from, int to)
    {
        var songs = Active.Songs;
        if (from < 0 || from >= songs.Count || to < 0 || to >= songs.Count || from == to) return;
        var x = songs[from];
        songs.RemoveAt(from);
        songs.Insert(to, x);
        ShuffleOnMoved(Active, from, to);   // the shuffle ORDER follows the songs, so a reorder doesn't change it
        _nowPlaying = QueueCursor.AfterMove(_nowPlaying, from, to, ViewingPlaying);
        _queueSel   = QueueCursor.AfterMove(_queueSel, from, to, sameList: true);
        SavePlaylists();
    }

    // ---- mode toggles ----
    private void ToggleAutoAdvance()    => SetAutoAdvance(!_autoAdvance);
    private void ToggleShuffle()        => SetShuffle(!_shuffle);
    private void SetAutoAdvance(bool v) { _autoAdvance = v; _cfg.Set<bool>("auto_advance", v); _cfg.Save(); }
    private void SetShuffle(bool v)     { _shuffle = v;     _cfg.Set<bool>("shuffle", v);      _cfg.Save(); OnShuffleChanged(); }
    private void CycleLoop()            { _loopMode = (_loopMode + 1) % 3; _cfg.Set<int>("loop_mode", _loopMode); _cfg.Save(); }
    private void SetGap(int v)          { _gapSec = Math.Clamp(v, 0, 15); _cfg.Set<int>("gap_sec", _gapSec); _cfg.Save(); }

    // ---- playback ----
    // An explicit play of row i of the VIEWED playlist (Play button, sweep): that playlist becomes the playing one.
    private void PlayQueueIndex(int i) => PlayFrom(Active, i);

    // Play song i of `list` (the viewed list for an explicit play, the playing list for next / prev / loop / auto).
    // With shuffle on, an explicit play (re)builds the shuffle order starting from this song; next / prev / loop pass
    // keepOrder so they walk the existing order (Plugin.Playlist.Shuffle.cs).
    private void PlayFrom(PlaylistDto list, int i, bool keepOrder = false)
    {
        _gapRemainMs = -1;   // cancel any pending between-song gap
        var songs = list.Songs;
        if (i < 0 || i >= songs.Count) { SetBandStatus(_loc.T("mst.status.playlistEmpty"), true); return; }
        if (!IsBandViewOpen()) { SetBandStatus(_loc.T("mst.status.openFreePlay"), true); return; }

        // Ensemble sync armed but not in an ensemble yet → hold; EnsembleWaitTick starts us when it goes live.
        if (_bandEnsembleSync && _bandNetPrebuffer && !EnsembleActive())
        {
            _ensembleWaitIdx  = i;
            _ensembleWaitList = list;
            _ensembleWaiting  = true;
            return;
        }
        _ensembleWaiting = false;

        var path = System.IO.Path.Combine(BandMidiDir(), songs[i]);
        if (!System.IO.File.Exists(path)) { SetBandStatus(_loc.TFormat("mst.status.missingFile", songs[i]), true); return; }

        var song = MidiParser.TryParseFile(path, out var err);
        if (song == null)
        {
            _services.Log.Warning($"[Maestro] parse failed for {songs[i]}: {err}");
            SetBandStatus(_loc.TFormat("mst.status.parseFailed", System.IO.Path.GetFileNameWithoutExtension(songs[i])), true);
            return;
        }

        _bandPlayer.HoldOffsetMs   = _bandHoldMs;
        _bandPlayer.ForceSustain   = _bandForceSustain;
        _bandPlayer.TempoPct       = _bandTempoPct;
        _bandPlayer.MaxPolyphony   = _bandMaxPoly;
        _bandPlayer.PreBufferedNet = _bandNetPrebuffer;
        ApplyEnsembleGrid(song.Bpm);   // if syncing: anchor base + count-in + tempo-scale to the ensemble; else normal
        _bandPlayer.Load(song);
        _bandPlayer.Start(_bandTranspose);
        LinkDiagPlainStart(songs[i]);   // dev-only START/CFG rows (no-op unless the diag folder is present)

        _playingList = list; _nowPlaying = i;
        if (_shuffle && !keepOrder) RebuildShuffle(list, i);
        if (ReferenceEquals(list, Active)) _queueSel = i;   // the selection belongs to the viewed list only
        string pedal = song.PedalCount > 0 ? $", pedal x{song.PedalCount}" : "";
        _bandSongTitle  = System.IO.Path.GetFileNameWithoutExtension(songs[i]);
        _bandMidiStatus = ""; _bandStatusAtMs = -1;   // a fresh start clears any old message (the header says Playing)
        _services.Log.Info($"[Maestro] playing {song.Name}: {song.NoteCount} notes, {song.DurationMs / 1000.0:F1}s, range {NoteName(song.MinNote)}–{NoteName(song.MaxNote)}{pedal}");
    }

    // ▶▶ / auto next: within the PLAYING playlist (the viewed one if nothing has played yet).
    private void PlayNext(bool auto)
    {
        var list = _playingList ?? Active;
        int n = list.Songs.Count;
        if (n == 0) return;
        if (_shuffle && n > 1)
        {
            int s = ShuffleNext(list, auto);   // next in the shuffle order (-1 = round over, Loop Off)
            if (s >= 0) PlayFrom(list, s, keepOrder: true);
            return;
        }
        int next = _nowPlaying + 1;
        if (next >= n)
        {
            if (auto && _loopMode != 1) return;   // end of playlist (auto-advance only wraps on Loop=All)
            next = 0;
        }
        PlayFrom(list, next);
    }

    private void PlayPrev()
    {
        var list = _playingList ?? Active;
        if (_shuffle && list.Songs.Count > 1)
        {
            int s = ShufflePrev(list);   // previous in the shuffle order; at its start the current song restarts
            if (s >= 0) PlayFrom(list, s, keepOrder: true);
            return;
        }
        int prev = QueueCursor.Prev(_nowPlaying, list.Songs.Count);
        if (prev >= 0) PlayFrom(list, prev);
    }

    // Clicking a queue row selects it (for reorder/remove and the ▶ Play target) — it does not start playback.
    private void SelectQueue(int i)
    {
        if (i >= 0 && i < Active.Songs.Count) _queueSel = i;
    }

    // Called each frame (from Plugin.cs): advance the queue when a song finishes, after an optional silent gap.
    private void PlaylistTick(float dt)
    {
        _durParseBudget = DurParsesPerTick;   // song-length cache: at most this many MIDI parses per frame (Plugin.Library.cs)
        ManageUpdateRate();            // hold 120 Hz while a song plays; release when idle (before early-returns)
        TipRepositionTick();           // re-assert the help tooltip's anchored position for a few frames after opening
        AutoAcceptEnsembleTick(dt);    // self-healing: (re)install the auto-accept listener while enabled if it's missing
        if (_ensembleWaiting) { EnsembleWaitTick(dt); return; }   // armed → wait for the ensemble to go live
        if (_gapRemainMs >= 0)   // counting down the between-song silence
        {
            _gapRemainMs -= dt * 1000.0;
            if (_gapRemainMs <= 0) { _gapRemainMs = -1; AdvanceAfterGap(); }
            return;
        }
        if (_lkPhase != LinkPhase.Off) return;   // linked: the ROOM runs the queue (Plugin.Link.Song.cs owns the player)
        if (!_bandPlayer.ConsumeCompleted()) return;
        if (_loopMode != 2 && !_autoAdvance) return;   // stop at end (not looping-one, not auto-advancing)
        if (_gapSec > 0) { _gapRemainMs = _gapSec * 1000.0; return; }   // the status line counts it down (MainLineKind.NextIn)
        AdvanceAfterGap();
    }

    // Self-managed update rate: hold 120 Hz while a song plays (tight note timing), release when idle.
    // Safe no-op unless the user granted Maestro rate-control permission (Settings → Performance).
    private void ManageUpdateRate()
    {
        bool active = _bandPlayer.IsPlaying || _previewSynth.IsPlaying;
        if (active && _rateScope == null) _rateScope = _services.Framework.RequestUpdateRate(120);
        else if (!active && _rateScope != null) { _rateScope.Dispose(); _rateScope = null; }
    }

    private void AdvanceAfterGap()
    {
        if (_loopMode == 2) PlayFrom(_playingList ?? Active, _nowPlaying, keepOrder: true);   // loop-one → replay (in the playing list)
        else PlayNext(auto: true);
    }
}
