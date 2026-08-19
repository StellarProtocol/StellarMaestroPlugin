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
    private int  _plIdx;            // active playlist
    private int  _queueSel  = -1;   // selected queue row (for remove/move); also the play cursor
    private int  _nowPlaying = -1;  // queue index currently playing (-1 = none)
    private bool _autoAdvance = true;
    private int  _loopMode;         // 0 = none, 1 = all, 2 = one
    private bool _shuffle;
    private int  _gapSec;           // silence between songs on auto-advance (seconds)
    private double _gapRemainMs = -1;  // >=0 while counting down the gap; -1 = idle
    private readonly Random _rng = new();

    private PlaylistDto Active => _playlists[Math.Clamp(_plIdx, 0, _playlists.Count - 1)];

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
        try { _cfg.Set<string>("playlists", System.Text.Json.JsonSerializer.Serialize(_playlists)); } catch { }
        _cfg.Set<int>("active_playlist", _plIdx);
        _cfg.Save();
    }

    // ---- playlist ops ----
    private void NewPlaylist()
    {
        _playlists.Add(new PlaylistDto { Name = $"Playlist {_playlists.Count + 1}" });
        _plIdx = _playlists.Count - 1;
        _queueSel = -1; _nowPlaying = -1; _queueScrollReset = true;
        SavePlaylists();
    }

    private void DeleteActivePlaylist()
    {
        if (_playlists.Count <= 1) { Active.Songs.Clear(); Active.Name = "Playlist 1"; }
        else { _playlists.RemoveAt(_plIdx); _plIdx = Math.Clamp(_plIdx, 0, _playlists.Count - 1); }
        _queueSel = -1; _nowPlaying = -1; _queueScrollReset = true;
        SavePlaylists();
    }

    private void RenameActivePlaylist(string name)
    {
        Active.Name = string.IsNullOrWhiteSpace(name) ? Active.Name : name.Trim();
        SavePlaylists();
    }

    private void SelectPlaylist(int delta)
    {
        int n = _playlists.Count;
        _plIdx = (_plIdx + delta % n + n) % n;
        _queueSel = -1; _nowPlaying = -1; _queueScrollReset = true;
        SavePlaylists();
    }

    // ---- queue ops ----
    private void AddToActive(string filename)
    {
        Active.Songs.Add(filename);
        _bandMidiStatus = $"added {System.IO.Path.GetFileNameWithoutExtension(filename)}";
        SavePlaylists();
    }

    private void RemoveFromQueue()
    {
        int i = _queueSel;
        if (i < 0 || i >= Active.Songs.Count) return;
        Active.Songs.RemoveAt(i);
        if (_nowPlaying == i) _nowPlaying = -1; else if (_nowPlaying > i) _nowPlaying--;
        _queueSel = Active.Songs.Count == 0 ? -1 : Math.Min(i, Active.Songs.Count - 1);
        SavePlaylists();
    }

    private void MoveQueue(int delta)
    {
        int i = _queueSel, j = i + delta;
        if (i < 0 || i >= Active.Songs.Count || j < 0 || j >= Active.Songs.Count) return;
        (Active.Songs[i], Active.Songs[j]) = (Active.Songs[j], Active.Songs[i]);
        if (_nowPlaying == i) _nowPlaying = j; else if (_nowPlaying == j) _nowPlaying = i;
        _queueSel = j;
        SavePlaylists();
    }

    private void ClearQueue()
    {
        Active.Songs.Clear();
        _nowPlaying = -1; _queueSel = -1; _queueScrollReset = true;
        SavePlaylists();
    }

    // ---- mode toggles ----
    private void SetAutoAdvance(bool v) { _autoAdvance = v; _cfg.Set<bool>("auto_advance", v); _cfg.Save(); }
    private void SetShuffle(bool v)     { _shuffle = v;     _cfg.Set<bool>("shuffle", v);      _cfg.Save(); }
    private void CycleLoop()            { _loopMode = (_loopMode + 1) % 3; _cfg.Set<int>("loop_mode", _loopMode); _cfg.Save(); }
    private void SetGap(int v)          { _gapSec = Math.Clamp(v, 0, 15); _cfg.Set<int>("gap_sec", _gapSec); _cfg.Save(); }
    private string LoopName() => _loopMode == 1 ? _loc.T("mst.loop.all") : _loopMode == 2 ? _loc.T("mst.loop.one") : _loc.T("mst.loop.off");

    // ---- playback ----
    private void PlayQueueIndex(int i)
    {
        _gapRemainMs = -1;   // cancel any pending between-song gap
        var songs = Active.Songs;
        if (i < 0 || i >= songs.Count) { _bandMidiStatus = _loc.T("mst.status.playlistEmpty"); return; }
        if (!IsBandViewOpen()) { _bandMidiStatus = _loc.T("mst.status.openFreePlay"); return; }

        // Ensemble sync armed but not in an ensemble yet → hold; EnsembleWaitTick starts us when it goes live.
        if (_bandEnsembleSync && _bandNetPrebuffer && !EnsembleActive())
        {
            _ensembleWaitIdx = i;
            _ensembleWaiting = true;
            _bandMidiStatus  = _loc.T("mst.status.waitEnsemble");
            return;
        }
        _ensembleWaiting = false;

        var path = System.IO.Path.Combine(BandMidiDir(), songs[i]);
        if (!System.IO.File.Exists(path)) { _bandMidiStatus = $"missing file: {songs[i]}"; return; }

        var song = MidiParser.TryParseFile(path, out var err);
        if (song == null) { _bandMidiStatus = $"parse failed: {err}"; return; }

        _bandPlayer.HoldOffsetMs   = _bandHoldMs;
        _bandPlayer.ForceSustain   = _bandForceSustain;
        _bandPlayer.TempoPct       = _bandTempoPct;
        _bandPlayer.MaxPolyphony   = _bandMaxPoly;
        _bandPlayer.PreBufferedNet = _bandNetPrebuffer;
        ApplyEnsembleGrid(song.Bpm);   // if syncing: anchor base + count-in + tempo-scale to the ensemble; else normal
        _bandPlayer.Load(song);
        _bandPlayer.Start(_bandTranspose);

        _nowPlaying = i; _queueSel = i;
        string pedal = song.PedalCount > 0 ? $", pedal x{song.PedalCount}" : "";
        _bandSongInfo   = $"{song.Name}: {song.NoteCount} notes, {song.DurationMs / 1000.0:F1}s, range {NoteName(song.MinNote)}–{NoteName(song.MaxNote)}{pedal}";
        _bandMidiStatus = _loc.T("mst.status.playing");
    }

    private void PlayNext(bool auto)
    {
        int n = Active.Songs.Count;
        if (n == 0) return;
        int next;
        if (_shuffle && n > 1)
        {
            do { next = _rng.Next(n); } while (next == _nowPlaying);
        }
        else
        {
            next = _nowPlaying + 1;
            if (next >= n)
            {
                if (auto && _loopMode != 1) return;   // end of playlist (auto-advance only wraps on Loop=All)
                next = 0;
            }
        }
        PlayQueueIndex(next);
    }

    private void PlayPrev()
    {
        int n = Active.Songs.Count;
        if (n == 0) return;
        int prev = _nowPlaying <= 0 ? n - 1 : _nowPlaying - 1;
        PlayQueueIndex(prev);
    }

    // Clicking a queue row selects it (for reorder/remove and the ▶ Play target) — it does not start playback.
    private void SelectQueue(int i)
    {
        if (i >= 0 && i < Active.Songs.Count) _queueSel = i;
    }

    // Called each frame (from Plugin.cs): advance the queue when a song finishes, after an optional silent gap.
    private void PlaylistTick(float dt)
    {
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
        if (!_bandPlayer.ConsumeCompleted()) return;
        if (_loopMode != 2 && !_autoAdvance) return;   // stop at end (not looping-one, not auto-advancing)
        if (_gapSec > 0) { _gapRemainMs = _gapSec * 1000.0; _bandMidiStatus = $"next in {_gapSec}s…"; return; }
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
        if (_loopMode == 2) PlayQueueIndex(_nowPlaying);   // loop-one → replay
        else PlayNext(auto: true);
    }

    // ---- queue list UI ----
    private const int QueuePoolSize = 10;
    private int  _queueWindowFirst;
    private bool _queueScrollReset;

    private HudElement[] BuildQueuePool()
    {
        var pool = new HudElement[QueuePoolSize];
        for (int s = 0; s < QueuePoolSize; s++)
        {
            int slot = s;
            pool[s] = new SelectableElement(
                new TextElement(() => QueueSlotLabel(slot), NoWrap: true),   // single line; viewport RectMask2D hard-clips long names
                OnClick:  () => SelectQueue(_queueWindowFirst + slot),
                Selected: () => { int i = _queueWindowFirst + slot; return i < Active.Songs.Count && i == _queueSel; });
        }
        return pool;
    }

    private string QueueSlotLabel(int slot)
    {
        int i = _queueWindowFirst + slot;
        var songs = Active.Songs;
        if (i < 0 || i >= songs.Count) return "";
        string name = System.IO.Path.GetFileNameWithoutExtension(songs[i]);
        return (i == _nowPlaying ? "▶ " : $"{i + 1}. ") + name;
    }

    private HudElement BuildPlaylistSection() => new ColumnElement(new HudElement[]
    {
        new SeparatorElement(),
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => "◀", OnClick: () => SelectPlaylist(-1)), Width: 34f),
            new CellElement(new TextElement(() => $"{Active.Name}  ({_plIdx + 1}/{_playlists.Count})", Emphasis: true), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => "▶", OnClick: () => SelectPlaylist(+1)), Width: 34f),
            new CellElement(new ButtonElement(Label: () => "+ " + _loc.T("mst.pl.new"), OnClick: NewPlaylist),          Width: 64f),
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.pl.del"),   OnClick: DeleteActivePlaylist), Width: 48f),
        }, Gap: 4f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.pl.name"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 42f),
            new CellElement(new InputElement(
                Get:    () => Active.Name,
                Submit: RenameActivePlaylist), Weight: 1f),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(
                () => Active.Songs.Count == 0 ? _loc.T("mst.pl.queueEmpty") : _loc.TFormat("mst.pl.queueCount", Active.Songs.Count),
                Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => "+ " + _loc.T("mst.pl.library"), OnClick: OpenLibrary), Width: 96f),
        }, Gap: 6f),
        new VirtualListElement(
            Count:     () => Active.Songs.Count,
            RowHeight: 22f,
            Pool:      BuildQueuePool(),
            OnWindow:  first => _queueWindowFirst = first,
            Height:    154f)
        { ResetScroll = () => { var r = _queueScrollReset; _queueScrollReset = false; return r; } },
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.pl.remove"), OnClick: RemoveFromQueue), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => "▲",      OnClick: () => MoveQueue(-1)), Width: 40f),
            new CellElement(new ButtonElement(Label: () => "▼",      OnClick: () => MoveQueue(+1)), Width: 40f),
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.pl.clear"),  OnClick: ClearQueue), Weight: 1f),
        }, Gap: 4f),
        new RowElement(new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: () => _autoAdvance, Set: SetAutoAdvance),
            new CellElement(new TextElement(() => _loc.T("mst.pl.autoAdvance")), Width: 96f),
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.pl.loop") + ": " + LoopName(), OnClick: CycleLoop), Weight: 1f),
            new ToggleElement(Label: () => "", Get: () => _shuffle, Set: SetShuffle),
            new CellElement(new TextElement(() => _loc.T("mst.pl.shuffle")), Width: 60f),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.pl.gap"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 128f),
            new CellElement(new SliderElement(
                Get: () => _gapSec,
                Set: v  => SetGap((int)System.MathF.Round(v)),
                Min: 0f, Max: 15f), Weight: 1f),
            new CellElement(new TextElement(() => _gapSec == 0 ? _loc.T("mst.off") : $"{_gapSec}s"), Width: 48f),
        }, Gap: 6f),
    }, Gap: 6f);
}
