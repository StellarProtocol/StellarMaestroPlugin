using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The Musician auto-player UI + orchestration. Drives the game's own note engine via Lua injection:
// Z.UIMgr:GetView("band_performance_main_pc"):PressKeyByKeyCount(midiNote). See Knowledge Base\Band-Instrument-Playback.md.
//
// Precondition to hear anything: summon a free-play instrument in-game first (the band_performance_main_pc view
// must be open, in Normal mode).
public sealed partial class Plugin
{
    private static readonly string[] _noteNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static string NoteName(int midi)
        => midi < 0 || midi > 127 ? midi.ToString() : $"{_noteNames[midi % 12]}{midi / 12 - 1}";

    private HudElement BuildBandRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => "MIDI Auto-Player", Emphasis: true),

        BuildPlaylistSection(),

        new SeparatorElement(),
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => "◀◀", OnClick: PlayPrev), Width: 46f),
            new CellElement(new ButtonElement(Label: PlayPauseLabel, OnClick: PlayPause), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => "▶▶", OnClick: () => PlayNext(false)), Width: 46f),
            new CellElement(new ButtonElement(Label: () => "■ Stop", OnClick: StopPlayback), Width: 80f),
        }, Gap: 4f),
        new RowElement(new HudElement[]
        {
            new CellElement(new SliderElement(Get: ProgressFrac, Set: SeekFrac, Min: 0f, Max: 1f), Weight: 1f),
            new CellElement(new TextElement(() => $"{Mmss(_bandPlayer.PositionMs)} / {Mmss(_bandPlayer.DurationMs)}"), Width: 92f),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => "Song", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 48f),
            new TextElement(() => (_bandPlayer.IsPlaying || _bandPlayer.IsPaused) ? _bandSongInfo : "—"),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => "Play", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 48f),
            new TextElement(() => _ensembleWaiting
                ? "waiting for ensemble…"
                : _bandPlayer.CountInRemainMs > 0
                ? $"count-in {_bandPlayer.CountInRemainMs / 1000.0:F1}s…"
                : (_bandPlayer.IsPlaying
                    ? $"notes {_bandPlayer.SentNotes}/{_bandPlayer.SongNotes}"
                    : (_bandPlayer.SentNotes > 0 ? $"done — sent {_bandPlayer.SentNotes}/{_bandPlayer.SongNotes} notes" : "stopped"))),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => "Status", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 48f),
            new TextElement(() => _bandMidiStatus, Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        }, Gap: 6f),

        new SeparatorElement(),
        new ButtonElement(Label: () => "🎧 Midi Preview (Local)", OnClick: () => _previewWindow.SetVisible(true), Width: 320f),
        new ButtonElement(Label: () => "Settings", OnClick: () => _settingsWindow.SetVisible(true), Width: 320f),
    }, Gap: 8f);

    // The Library window — browse the midi/ folder and click a song to add it to the queue.
    private HudElement BuildLibraryRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => "MIDI Library", Emphasis: true),
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => "Rescan folder", OnClick: RescanMidiFolder), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => "Locate",        OnClick: OpenMidiFolder),   Weight: 1f),
        }, Gap: 4f),
        new SeparatorElement(),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => "Search", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 56f),
            new CellElement(new InputElement(Get: () => _librarySearch, Submit: SetLibrarySearch, OnChange: SetLibrarySearch), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => "Clear", OnClick: () => SetLibrarySearch("")), Width: 56f),
        }, Gap: 6f),
        new TextElement(() => _bandSongs.Length == 0
                ? "No songs — Rescan/Locate the folder"
                : _songView.Length == _bandSongs.Length
                    ? $"Library ({_bandSongs.Length}) — click to add to queue"
                    : $"Library ({_songView.Length} of {_bandSongs.Length}) — click to add to queue",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new VirtualListElement(
            Count:     () => _songView.Length,
            RowHeight: 22f,
            Pool:      BuildSongPool(),
            OnWindow:  first => _songWindowFirst = first,
            Height:    240f)
        { ResetScroll = () => { var r = _songScrollReset; _songScrollReset = false; return r; } },
    }, Gap: 8f);

    // The Settings window — playback tuning + the buffered-sync toggle and its Network Settings button.
    private HudElement BuildSettingsRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => "Settings", Emphasis: true),
        SliderRow("transpose", () => "Transpose",
            new SliderElement(Get: () => _bandTranspose, Set: v => SetTranspose((int)System.MathF.Round(v)), Min: -48f, Max: 48f),
            () => $"{(_bandTranspose >= 0 ? "+" : "")}{_bandTranspose}",
            () => SetTranspose(0),
            () => "Shifts every note up or down in semitones (12 = one octave). Use it to move a song into the "
                + "summoned instrument's playable range."),
        SliderRow("hold_ms", () => "Note hold",
            new SliderElement(Get: () => _bandHoldMs, Set: v => SetHold((int)System.MathF.Round(v)), Min: -500f, Max: 2000f),
            () => $"{(_bandHoldMs >= 0 ? "+" : "")}{_bandHoldMs}",
            () => SetHold(0),
            () => "Adds to or subtracts from how long each note is held, in milliseconds. Positive makes notes ring "
                + "longer (more sustain); negative cuts them shorter. 0 = each note's exact written length."),
        SliderRow("tempo_pct", () => "Tempo %",
            new SliderElement(Get: () => _bandTempoPct, Set: v => SetTempo((int)System.MathF.Round(v)), Min: 25f, Max: 400f),
            () => $"{_bandTempoPct}%",
            () => SetTempo(100),
            () => "Playback speed as a percent of the song's own tempo. 100 = original; higher is faster, lower is "
                + "slower. In an ensemble the tempo is set by the ensemble instead."),
        SliderRow("max_poly", () => "Max notes",
            new SliderElement(Get: () => _bandMaxPoly, Set: v => SetMaxPoly((int)System.MathF.Round(v)), Min: 0f, Max: 16f),
            () => _bandMaxPoly == 0 ? "off" : _bandMaxPoly.ToString(),
            () => SetMaxPoly(0),
            () => "Caps how many notes may sound at once. When exceeded, the oldest (or the lowest within a chord) "
                + "notes are dropped. off = no cap — lower it if dense passages overwhelm the instrument."),
        SliderRow("restrike_gap", () => "Restrike gap",
            new SliderElement(Get: () => _bandRestrikeGapMs, Set: v => SetRestrikeGap((int)System.MathF.Round(v)), Min: 0f, Max: 100f),
            () => _bandRestrikeGapMs == 0 ? "off" : $"{_bandRestrikeGapMs}ms",
            () => SetRestrikeGap(0),
            () => "When the same pitch repeats, it is released this many ms before being struck again — including "
                + "back-to-back notes — so each repeat registers as a new note. off = release exactly at the re-press, "
                + "which makes fast same-pitch runs drop at the LISTENER in Network Sync mode.\n\n"
                + "Default 16 ms (inaudible). Raise it if dense same-pitch runs still drop for listeners."),
        BuildMonitorVolumeRow(),
        HelpToggle("force_sustain",
            () => _bandForceSustain,
            v  => { _bandForceSustain = v; _bandPlayer.ForceSustain = v; _cfg.Set<bool>("force_sustain", v); _cfg.Save(); },
            () => "Force sustain",
            () => "Holds the sustain pedal down for the whole song, so each note rings until the next note on the same pitch.\n\n"
                + "Best for piano, guitar and bass. Leave it off for instruments that should re-articulate every note."),
        HelpToggle("apply_tone",
            () => _bandApplyTone,
            OnToggleApplyTone,
            () => "Apply tone / technique from MIDI instrument",
            () => "Reads the GM instrument (program-change) on each MIDI track and applies the matching in-game tone/technique, "
                + "switching live as the program changes. Off = every note plays Clean / Sustained.\n\n"
                + "Set the track's MIDI instrument to get each effect:\n"
                + "Guitar —\n"
                + "  • Overdriven Guitar → Overdrive\n"
                + "  • Distortion Guitar → Distortion\n"
                + "  • Electric Guitar (muted) → Muffled\n"
                + "  • Guitar Harmonics → Harmonics\n"
                + "  • any other guitar → Clean\n"
                + "Bass —\n"
                + "  • Slap Bass 1 / 2 → Slap\n"
                + "  • Synth Bass 1 / 2 → Overdrive\n"
                + "  • any other bass → Clean\n"
                + "Piano / drums / other → no effect (Clean / Sustained).\n\n"
                + "The effect is clamped to what the summoned instrument supports (e.g. a guitar has no Slap; on a bass, "
                + "Distortion falls back to Overdrive).\n\n"
                + "Note: only ONE channel is read — the dominant one (the channel with the most notes). Program-changes on "
                + "any other channel are ignored, so keep each stem to a single instrument channel for its tone to apply."),
        HelpToggle("net_prebuffer",
            () => _bandNetPrebuffer,
            v  => { _bandNetPrebuffer = v; _cfg.Set<bool>("net_prebuffer", v); _cfg.Save(); },
            () => "Network Sync Mode",
            () => "Builds the entire note timeline up front and plays it on a steady real-time clock instead of firing notes live.\n\n"
                + "Strongly recommended: it removes the per-note timing jitter other players hear, and it is required for "
                + "Mute local, the on-screen key animation, and every ensemble sync option below."),
        HelpToggle("mute_local",
            () => _bandMuteLocal,
            v  => { _bandMuteLocal = v; _bandPlayer.MuteLocal = v; _cfg.Set<bool>("mute_local", v); _cfg.Save(); },
            () => "Mute my local sound",
            () => _bandNetPrebuffer
                ? "Silences the instrument on your client only — everyone else still hears every note you play.\n\n"
                    + "Use it to perform in a group without the sound in your own ears."
                : "Silences the instrument on your client only.\n\nRequires Network Sync mode — enable it first.",
            enabled: () => _bandNetPrebuffer),
        HelpToggle("show_keyviz",
            () => _bandShowKeyViz,
            v  => { _bandShowKeyViz = v; _bandPlayer.ShowKeyViz = v; _cfg.Set<bool>("show_keyviz", v); _cfg.Save(); },
            () => "Show on-screen key animation",
            () => "Shows the on-screen key / note animation as the auto-player performs, so you can see what it is pressing in real time.\n\n"
                + "Requires Network Sync mode.",
            enabled: () => _bandNetPrebuffer),
        new ButtonElement(Label: () => "Network Settings", OnClick: () => _networkWindow.SetVisible(true),
                          Enabled: () => _bandNetPrebuffer, Width: 320f),
        BuildEnsembleSection(),
    }, Gap: 8f);

    // Playback progress as a 0..1 fraction; seeking scales it back to ms. Duration is in the player's current
    // position timebase (song-ms for local, real-ms for buffered), matching PositionMs.
    //
    // Debounced seek: the framework calls SeekFrac continuously while the slider is dragged, so we stash the drag
    // target and only perform the actual Seek once input has settled (SeekDebounceMs of no movement). Until then
    // ProgressFrac returns the drag position so the thumb follows the finger without a per-frame seek.
    private const long SeekDebounceMs = 180;
    private bool  _seekPending;
    private float _pendingSeekFrac;
    private long  _lastSeekInputMs;

    private float ProgressFrac()
    {
        if (_seekPending)
        {
            if (Environment.TickCount64 - _lastSeekInputMs >= SeekDebounceMs)
            {
                _seekPending = false;
                int dp = _bandPlayer.DurationMs;
                if (dp > 0) _bandPlayer.Seek((int)(_pendingSeekFrac * dp));
            }
            else
            {
                return System.Math.Clamp(_pendingSeekFrac, 0f, 1f);   // follow the drag while it's still moving
            }
        }
        int d = _bandPlayer.DurationMs;
        return d > 0 ? System.Math.Clamp((float)_bandPlayer.PositionMs / d, 0f, 1f) : 0f;
    }

    private void SeekFrac(float frac)
    {
        _pendingSeekFrac = frac;
        _lastSeekInputMs = Environment.TickCount64;
        _seekPending     = true;
    }

    private static string Mmss(int ms)
    {
        if (ms < 0) ms = 0;
        int s = ms / 1000;
        return $"{s / 60}:{s % 60:D2}";
    }

    private IWindowControl _settingsWindow = null!;
    private IWindowControl _libraryWindow  = null!;
    private IWindowControl _previewWindow  = null!;
    private string   _bandMidiStatus = "ready";     // transient status line (added / queued / errors / rescans)
    private string   _bandSongInfo   = "";          // now-playing song info — shown on the Song line only while playing
    private int      _bandTranspose;
    private int      _bandHoldMs;
    private bool     _bandForceSustain;
    private int      _bandTempoPct = 100;
    private int      _bandMaxPoly;   // 0 = unlimited
    private bool     _bandNetPrebuffer;
    private int      _bandRestrikeGapMs;
    private bool     _bandMuteLocal;
    private bool     _bandShowKeyViz = true;
    private string[] _bandSongs = Array.Empty<string>();

    private static string BandMidiDir()
    {
        string root = AppContext.BaseDirectory;   // game_mini (the running exe's dir) under BepInEx
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
        {
            try
            {
                var dll = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (!string.IsNullOrEmpty(dll))
                    root = System.IO.Path.GetFullPath(System.IO.Path.Combine(dll, "..", "..", "..")); // …/stellar/plugins/maestro → game_mini
            }
            catch { }
        }
        return System.IO.Path.Combine(root, "midi");
    }

    private void RescanMidiFolder()
    {
        try
        {
            var dir = BandMidiDir();
            System.IO.Directory.CreateDirectory(dir);
            _bandSongs = System.IO.Directory.EnumerateFiles(dir, "*.*", System.IO.SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var e = System.IO.Path.GetExtension(f);
                    return e.Equals(".mid", StringComparison.OrdinalIgnoreCase) || e.Equals(".midi", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ApplyLibraryFilter();
            _bandMidiStatus = _bandSongs.Length == 0 ? $"no .mid/.midi files in {dir}" : $"{_bandSongs.Length} file(s) found";
            _services.Log.Info($"[Maestro] midi dir={dir} files={_bandSongs.Length}");
        }
        catch (Exception ex) { _bandMidiStatus = $"scan error: {ex.Message}"; }
    }

    // Virtualized song list: a small fixed pool of rows scrolls over the full song list. The framework calls
    // OnWindow(first) before polling, so each pool slot maps to logical index (first + slot) — no blank rows,
    // and the scrollbar is sized to the true count.
    private const int SongPoolSize = 12;   // ≈ visible rows (176/22 ≈ 8) + margin
    private int      _songWindowFirst;
    private bool     _songScrollReset;
    private string   _librarySearch = "";
    private string[] _songView = Array.Empty<string>();   // _bandSongs filtered by the search box

    // Recompute the visible song list from the search text. Called on rescan and whenever the search changes.
    private void ApplyLibraryFilter()
    {
        var q = _librarySearch.Trim();
        _songView = string.IsNullOrEmpty(q)
            ? _bandSongs
            : _bandSongs.Where(f => System.IO.Path.GetFileNameWithoutExtension(f)
                    .Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        _songScrollReset = true;
    }

    private void SetLibrarySearch(string s)
    {
        _librarySearch = s ?? "";
        ApplyLibraryFilter();
    }

    private HudElement[] BuildSongPool()
    {
        var pool = new HudElement[SongPoolSize];
        for (int s = 0; s < SongPoolSize; s++)
        {
            int slot = s;
            pool[s] = new SelectableElement(
                new TextElement(() => SongSlotLabel(slot), NoWrap: true),   // single line; viewport RectMask2D hard-clips long names
                OnClick: () => AddLibraryToQueue(_songWindowFirst + slot));
        }
        return pool;
    }

    private string SongSlotLabel(int slot)
    {
        int i = _songWindowFirst + slot;
        return i >= 0 && i < _songView.Length ? System.IO.Path.GetFileNameWithoutExtension(_songView[i]) : "";
    }

    // Clicking a library row appends that file to the active playlist's queue (stores the bare filename).
    private void AddLibraryToQueue(int libIdx)
    {
        if (libIdx < 0 || libIdx >= _songView.Length) return;
        AddToActive(System.IO.Path.GetFileName(_songView[libIdx]));
    }

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

    // True only while the PC free-play performance view is open (instrument summoned). Checked at click time
    // (a per-frame Enabled poll would run Lua every frame).
    private bool IsBandViewOpen()
    {
        EnsureLuaState();
        CallLua("rawset(_G,'__bviewopen', nil) pcall(function() if Z.UIMgr:GetView('band_performance_main_pc') ~= nil then rawset(_G,'__bviewopen', true) end end)");
        return ReadLuaRaw("__bviewopen") != null;
    }

    // A song is "selected to switch" when the highlighted queue row isn't the one currently playing.
    private bool QueueSelectDiffers() => _queueSel >= 0 && _queueSel != _nowPlaying;

    private string PlayPauseLabel()
    {
        if (_bandPlayer.IsPlaying) return QueueSelectDiffers() ? "▶ Play" : "▮▮ Pause";
        if (_bandPlayer.IsPaused)  return QueueSelectDiffers() ? "▶ Play" : "▶ Resume";
        return "▶ Play";
    }

    private void PlayPause()
    {
        bool switchSel = QueueSelectDiffers();
        if (_bandPlayer.IsPlaying && !switchSel) { _bandPlayer.Pause(); return; }   // pause current (needs nothing)
        if (!IsBandViewOpen()) { _bandMidiStatus = "open Free Play (summon an instrument) first"; return; }
        if (switchSel) { PlayQueueIndex(_queueSel); return; }                       // play the selected song
        if (_bandPlayer.IsPaused) { _bandPlayer.Resume(); return; }

        if (Active.Songs.Count == 0) { _bandMidiStatus = "queue is empty — add songs from the library"; return; }
        PlayQueueIndex(_queueSel >= 0 ? _queueSel : (_nowPlaying >= 0 ? _nowPlaying : 0));
    }

    private void StopPlayback() { _gapRemainMs = -1; _ensembleWaiting = false; _bandPlayer.StopReset(); }

    // Rescans the midi/ folder, then opens the Library window — so newly-dropped files show up without a manual rescan.
    private void OpenLibrary()
    {
        RescanMidiFolder();
        _libraryWindow.SetVisible(true);
    }

    // Opens the midi/ folder in the OS file explorer (creating it first if missing) so the user can drop files in.
    private void OpenMidiFolder()
    {
        try
        {
            var dir = BandMidiDir();
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            _bandMidiStatus = $"opened {dir}";
        }
        catch (Exception ex) { _bandMidiStatus = $"open failed: {ex.Message}"; }
    }
}
