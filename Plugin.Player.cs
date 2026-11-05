using System;
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
        new TextElement(() => _loc.T("mst.player.header"), Emphasis: true),

        BuildPlaylistSection(),

        new SeparatorElement(),
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => "◀◀", OnClick: PlayPrev), Width: 46f),
            new CellElement(new ButtonElement(Label: PlayPauseLabel, OnClick: PlayPause), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => "▶▶", OnClick: () => PlayNext(false)), Width: 46f),
            new CellElement(new ButtonElement(Label: () => "■ " + _loc.T("mst.stop"), OnClick: StopPlayback), Width: 80f),
        }, Gap: 4f),
        new RowElement(new HudElement[]
        {
            new CellElement(new SliderElement(Get: ProgressFrac, Set: SeekFrac, Min: 0f, Max: 1f), Weight: 1f),
            new CellElement(new TextElement(() => $"{Mmss(_bandPlayer.PositionMs)} / {Mmss(_bandPlayer.DurationMs)}"), Width: 92f),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.song"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 48f),
            new TextElement(() => (_bandPlayer.IsPlaying || _bandPlayer.IsPaused) ? _bandSongInfo : "—"),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.playLabel"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 48f),
            new TextElement(() => _ensembleWaiting
                ? _loc.T("mst.play.waitEnsemble")
                : _bandPlayer.CountInRemainMs > 0
                ? _loc.TFormat("mst.play.countIn", (_bandPlayer.CountInRemainMs / 1000.0).ToString("F1"))
                : (_bandPlayer.IsPlaying
                    ? _loc.TFormat("mst.play.notes", _bandPlayer.SentNotes, _bandPlayer.SongNotes)
                    : (_bandPlayer.SentNotes > 0 ? _loc.TFormat("mst.play.done", _bandPlayer.SentNotes, _bandPlayer.SongNotes) : _loc.T("mst.play.stopped")))),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.status"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 48f),
            new TextElement(() => _bandMidiStatus, Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        }, Gap: 6f),

        new SeparatorElement(),
        new ButtonElement(Label: () => "🎧 " + _loc.T("mst.btn.preview"), OnClick: () => _previewWindow.SetVisible(true), Width: 320f),
        new ButtonElement(Label: () => _loc.T("mst.btn.settings"), OnClick: () => _settingsWindow.SetVisible(true), Width: 320f),
        new ButtonElement(Label: () => _loc.T("mst.btn.link"), OnClick: OpenLinkWindow, Width: 320f),
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

    // True only while the PC free-play performance view is open (instrument summoned). Checked at click time
    // (a per-frame Enabled poll would run Lua every frame).
    private bool IsBandViewOpen()
    {
        _services.Lua.DoString("pcall(function() rawset(_G,'__bviewopen', nil) if Z.UIMgr:GetView('band_performance_main_pc') ~= nil then rawset(_G,'__bviewopen', true) end end)");
        return _services.Lua.TryReadGlobalBool("__bviewopen", out var open) && open;
    }

    // A song is "selected to switch" when the highlighted queue row isn't the one currently playing.
    private bool QueueSelectDiffers() => _queueSel >= 0 && _queueSel != _nowPlaying;

    private string PlayPauseLabel()
    {
        if (_bandPlayer.IsPlaying) return QueueSelectDiffers() ? "▶ " + _loc.T("mst.play") : "▮▮ " + _loc.T("mst.pause");
        if (_bandPlayer.IsPaused)  return QueueSelectDiffers() ? "▶ " + _loc.T("mst.play") : "▶ " + _loc.T("mst.resume");
        return "▶ " + _loc.T("mst.play");
    }

    private void PlayPause()
    {
        bool switchSel = QueueSelectDiffers();
        if (_bandPlayer.IsPlaying && !switchSel) { _bandPlayer.Pause(); return; }   // pause current (needs nothing)
        if (!IsBandViewOpen()) { _bandMidiStatus = _loc.T("mst.status.openFreePlay"); return; }
        if (switchSel) { PlayQueueIndex(_queueSel); return; }                       // play the selected song
        if (_bandPlayer.IsPaused) { _bandPlayer.Resume(); return; }

        if (Active.Songs.Count == 0) { _bandMidiStatus = _loc.T("mst.status.queueEmpty"); return; }
        PlayQueueIndex(_queueSel >= 0 ? _queueSel : (_nowPlaying >= 0 ? _nowPlaying : 0));
    }

    private void StopPlayback() { _gapRemainMs = -1; _ensembleWaiting = false; _bandPlayer.StopReset(); }

}
