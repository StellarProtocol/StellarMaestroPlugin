using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Frame-accurate playback engine for the Musician auto-player.
//
// Drives a parsed MidiSong through the game's own note engine. Each frame (IFramework.Update) it advances a
// millisecond clock, collects every note event now due, and emits them as ONE batched Lua call — re-fetching
// the live band view inside the chunk so exiting Free-Play mid-song stops cleanly. In Normal free-play mode
// the view's sync component broadcasts each note, so nearby players hear the performance too.
//
// Timing is frame-granular (~16ms at 60fps). See Knowledge Base\Band-Instrument-Playback.md.
public sealed partial class MidiPlayer : IDisposable
{
    private readonly IPluginServices     _services;
    private readonly Func<string, string?> _callLua;

    // Every note is held for at least this long, so notes shorter than a frame are still audible.
    private const double MinHoldMs = 25.0;

    private MidiSong? _song;
    private int    _transpose;                 // semitone offset applied to every note before sending
    private double _holdOffsetMs;              // added to every note's release time: + = legato/sustain, − = staccato
    private double _speed = 1.0;               // playback-clock multiplier: 1.0 = original tempo, 2.0 = twice as fast
    private bool   _playing;
    private bool   _paused;
    private bool   _completed;                   // latched when a song ends naturally (for playlist auto-advance)
    private bool   _subscribed;
    private double _elapsedMs;
    private double _startAtFrac = -1;            // 0..1 position the next Start begins from (set by seeking while stopped); -1 = none
    private readonly Stopwatch _clock = new();   // wall clock since Play — immune to frame-delta clamping/hitches
    private double _lastClockMs;                 // previous _clock reading, for real-delta accumulation (B1 live tempo)
    private int    _cursor;                     // next event index to fire
    private int    _sentNotes;                  // note-ons actually sent this playback (diagnostics)
    private double[] _offForOn = Array.Empty<double>(); // per on-event index → its matched note-off time in ms (−1 = none)
    private readonly Dictionary<int, double> _held = new(); // post-transpose keyCount → scheduled release time (ms)
    private readonly List<int>    _heldOrder = new();       // press order, for polyphony voice-stealing (oldest first)
    private readonly Dictionary<int, int> _heldOn = new();  // keyCount → note-on TimeMs, for same-time (chord) detection
    private readonly List<int>    _toRelease = new();       // scratch: keys due for release this frame
    private int    _maxPoly;                     // 0 = unlimited; else cap simultaneous notes (steal the oldest)
    private bool   _pedalDown;                   // current sustain-pedal state (so we can lift it on stop)
    private bool   _vmDeclaredThisFrame;         // whether the frame chunk has already declared `local vm`
    private readonly StringBuilder _sb = new();

    public bool    IsPlaying  => _playing;
    public bool    IsPaused   => _paused;
    // In pre-buffer (B2) mode the clock is _realMs and the timeline bakes in tempo, so report the B2 duration
    // (falling back to the song ms length before the timeline has been built, e.g. while stopped).
    public int     DurationMs => _netMode ? (_b2DurationMs > 0 ? _b2DurationMs : (_song?.DurationMs ?? 0)) : (_song?.DurationMs ?? 0);
    public int     PositionMs => _netMode ? (int)_realMs  : (int)_elapsedMs;
    public int     SentNotes  => _sentNotes;
    public int     SongNotes  => _song?.NoteCount ?? 0;

    // Live-adjustable. In local mode these take effect immediately (read per note / per frame); in buffered mode
    // they're baked into the timeline, so a change flags a debounced rebuild (MarkB2Dirty → RebuildAndReseek).
    public int HoldOffsetMs { get => (int)_holdOffsetMs;            set { _holdOffsetMs = value; MarkB2Dirty(); } }
    public int TempoPct     { get => (int)Math.Round(_speed * 100); set { _speed = Math.Clamp(value, 25, 400) / 100.0; MarkB2Dirty(); } }
    public int Transpose    { get => _transpose;                   set { _transpose = value; MarkB2Dirty(); } }
    public int MaxPolyphony { get => _maxPoly;                     set { _maxPoly = Math.Clamp(value, 0, 16); MarkB2Dirty(); } }

    private bool _forceSustain;   // hold the sustain pedal down for the whole song, ignoring the MIDI's own CC64
    public bool ForceSustain
    {
        get => _forceSustain;
        set
        {
            if (_forceSustain == value) return;
            _forceSustain = value;
            if (!_playing) return;                 // otherwise applied at Start
            if (_netMode)
            {
                _pedalDown = value;
                FlushPedalB2(value);          // local: set the pedal now (local-only)
                SendPedalImmediateB2(value);  // network: SustainPedal down(1)/up(0) at the current position, for listeners
                _b2ForcePedalSent = true;     // the SendBatchB2 start-down one-shot is now covered by this live send
                return;
            }
            if (value) { FlushPedalB1(true); _pedalDown = true; }        // engage now
            else if (_pedalDown) { FlushPedalB1(false); _pedalDown = false; }  // release the held pedal
        }
    }

    public MidiPlayer(IPluginServices services, Func<string, string?> callLua)
    {
        _services = services;
        _callLua  = callLua;
    }

    public void Load(MidiSong song)
    {
        Stop();
        _song = song;
        _startAtFrac = -1;
        _elapsedMs = 0;
        _realMs    = 0;   // reset the position display for the new song
    }

    public void Start(int transpose)
    {
        if (_song == null) return;
        Stop();
        _transpose = transpose;
        _elapsedMs = 0;
        _cursor    = 0;
        _sentNotes = 0;
        _pedalDown = false;
        _completed = false;
        _clock.Restart();
        _lastClockMs = 0;
        _held.Clear();
        _heldOrder.Clear();
        _heldOn.Clear();
        BuildNoteOffMap();
        if (_netMode) ResetB2();
        if (_startAtFrac >= 0)   // begin from the marker set by seeking while stopped
        {
            RepositionTo((int)(_startAtFrac * DurationMs));
            _startAtFrac = -1;
        }
        if (_forceSustain) { ApplyPedal(true); _pedalDown = true; }   // engage the pedal once, held all song
        _playing   = true;
        if (!_subscribed) { _services.Framework.Update += OnUpdate; _subscribed = true; }
        SeekInstrumentEffect();   // set the tone/technique active at the start position (or Clean/Sustained if off)
    }

    // Pair each note-on with its matching note-off so release times can be scheduled (and offset) from the press side.
    private void BuildNoteOffMap()
    {
        var events = _song!.Events;
        _offForOn = new double[events.Count];
        var pending = new Dictionary<int, Stack<int>>(); // pitch → stack of open on-event indices
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Pedal) continue;
            if (e.On)
            {
                _offForOn[i] = -1;
                if (!pending.TryGetValue(e.Note, out var st)) { st = new Stack<int>(); pending[e.Note] = st; }
                st.Push(i);
            }
            else if (pending.TryGetValue(e.Note, out var st) && st.Count > 0)
            {
                _offForOn[st.Pop()] = e.TimeMs;
            }
        }
    }

    public void Stop()
    {
        if (_playing) { if (_netMode) ReleaseAllHeldB2(); else ReleaseAllHeld(); }
        _playing = false;
        _paused  = false;
        _clock.Stop();
        if (_subscribed) { try { _services.Framework.Update -= OnUpdate; } catch { } _subscribed = false; }
        _held.Clear();
        _heldOrder.Clear();
        _heldOn.Clear();
    }

    // Stop and reset to the beginning (the Stop button). Pause/Resume keep the position; this clears it.
    public void StopReset()
    {
        Stop();
        _completed   = false;
        _elapsedMs   = 0;
        _realMs      = 0;
        _startAtFrac = -1;
    }

    // Natural end of song → stop and latch a completion the playlist controller can consume to auto-advance.
    private void EndReached() { Stop(); _completed = true; }

    // Returns true once after a song finished on its own (not a user Stop/Pause), clearing the latch.
    public bool ConsumeCompleted() { if (!_completed) return false; _completed = false; return true; }

    // Freeze playback at the current position (releasing sounding notes) so it can be resumed. The Stopwatch is
    // paused so the position doesn't advance; cursors stay put.
    public void Pause()
    {
        if (!_playing) return;
        if (_netMode) ReleaseAllHeldB2(); else ReleaseAllHeld();
        _held.Clear();
        _heldOrder.Clear();
        _heldOn.Clear();
        _playing = false;
        _paused  = true;
        _clock.Stop();
        if (_subscribed) { try { _services.Framework.Update -= OnUpdate; } catch { } _subscribed = false; }
    }

    // Continue from a paused position — re-anchor the clock (and network base) to the held position and resume.
    public void Resume()
    {
        if (!_paused || _song == null) return;
        _paused  = false;
        _playing = true;
        _clock.Start();   // continues the Stopwatch from where Pause froze it
        if (_netMode) _sendIdx = _playIdx;
        AnchorClock();
        SeekInstrumentEffect();   // re-resolve tone/technique for the resumed position
        if (_forceSustain) { ApplyPedal(true); _pedalDown = true; }
        if (!_subscribed) { _services.Framework.Update += OnUpdate; _subscribed = true; }
    }

    // Jump playback to targetMs. Playing → live seek (releasing anything sounding). Paused → reposition the held
    // position (Resume re-anchors the clock from there). Stopped → set the start marker for the next Play.
    public void Seek(int targetMs)
    {
        if (_song == null) return;
        targetMs = Math.Clamp(targetMs, 0, DurationMs);

        if (_playing)
        {
            if (_netMode) ReleaseAllHeldB2();
            else { ReleaseAllHeld(); _held.Clear(); _heldOrder.Clear(); _heldOn.Clear(); }
            SetPositionCursors(targetMs);
            AnchorClock();
            SeekInstrumentEffect();   // re-resolve tone/technique for the new position
            if (_forceSustain) { ApplyPedal(true); _pedalDown = true; }   // re-engage the held pedal after the release
        }
        else if (_paused)
        {
            SetPositionCursors(targetMs);   // Resume re-anchors the clock/base from here
        }
        else   // stopped → set the start marker for the next Play
        {
            _startAtFrac = DurationMs > 0 ? (double)targetMs / DurationMs : 0;
            _elapsedMs = targetMs;
            _realMs    = targetMs;   // reflect the marker on the position slider
        }
    }

    // Position + event cursors + note counter to targetMs (no clock/base anchoring). Rolls the note count forward
    // to include every note-on we seeked past, so "notes X/Y" tracks the position.
    private void SetPositionCursors(int targetMs)
    {
        targetMs = Math.Clamp(targetMs, 0, Math.Max(0, DurationMs));
        if (_netMode)
        {
            _realMs  = targetMs;
            _playIdx = 0; while (_playIdx < _timeline.Count && _timeline[_playIdx].RealMs < targetMs) _playIdx++;
            _sendIdx = _playIdx;
            _b2Held.Clear();
            int n = 0; for (int i = 0; i < _playIdx; i++) if (_timeline[i].Type == CmdType.Press) n++;
            _sentNotes = n;
        }
        else
        {
            _elapsedMs = targetMs;
            var events = _song!.Events;
            _cursor = 0; while (_cursor < events.Count && events[_cursor].TimeMs < targetMs) _cursor++;
            int n = 0;
            for (int i = 0; i < _cursor; i++)
            {
                var e = events[i];
                if (e.Pedal || !e.On) continue;
                int key = e.Note + _transpose;
                if (key >= 0 && key <= 127) n++;
            }
            _sentNotes = n;
        }
    }

    // Anchor the running clock (and, in buffered mode, the network base) to the current position.
    private void AnchorClock()
    {
        if (_netMode)
        {
            _b2ClockOffset = _realMs - _clock.Elapsed.TotalMilliseconds;
            _callLua("pcall(function() rawset(_G,'__b2base', Z.ServerTime:GetServerTime() - " + (long)Math.Round(_realMs) + ") end)");
        }
        else
        {
            _lastClockMs = _clock.Elapsed.TotalMilliseconds;
        }
    }

    // Used by Start() to begin from the start marker.
    private void RepositionTo(int targetMs) { SetPositionCursors(targetMs); AnchorClock(); }

    // Flag that a live transpose/hold/tempo change needs the buffered timeline rebuilt (no-op in local mode, which
    // reads those values directly). Debounced in OnUpdateB2 so dragging a slider only rebuilds once it settles.
    private void MarkB2Dirty()
    {
        if (_netMode && _playing) { _b2Dirty = true; _b2DirtyMs = Environment.TickCount64; }
    }

    // Rebuild the buffered timeline with the current transpose/hold/tempo and re-seek to the same song position.
    // The ~lookahead of already-sent notes plays out on remotes at the old settings; the rest switches over.
    private void RebuildAndReseek()
    {
        double frac = _b2DurationMs > 0 ? _realMs / _b2DurationMs : 0.0;
        ReleaseAllHeldB2();
        BuildTimeline();                                   // uses current _transpose / _holdOffsetMs / _speed
        SetPositionCursors((int)(frac * _b2DurationMs));
        AnchorClock();
        if (_forceSustain) { ApplyPedal(true); _pedalDown = true; }
    }

    private void OnUpdate(float dt)
    {
        if (!_playing || _song == null) return;
        if (_netMode) { OnUpdateB2(dt); return; }
        // Advance the song clock by REAL elapsed time (× tempo), read from the wall clock rather than the frame
        // delta — so a frame hitch doesn't drift or clamp playback position; supports live tempo changes.
        double now = _clock.Elapsed.TotalMilliseconds;
        _elapsedMs += (now - _lastClockMs) * _speed;
        _lastClockMs = now;
        var events = _song.Events;

        TickInstrumentEffect();   // fire any tone/technique program-change boundaries now due (B1: live)

        _sb.Clear();
        _vmDeclaredThisFrame = false;
        int emitted = 0;

        // 1) Press notes / drive pedal for every event now due. Note-offs are ignored — each note's release is
        //    scheduled from its press using the pre-paired off time plus the (live) hold offset.
        while (_cursor < events.Count && events[_cursor].TimeMs <= _elapsedMs)
        {
            int idx = _cursor;
            var e   = events[_cursor++];

            if (e.Pedal)   // sustain pedal (CC64) — drive the game's pedal so held notes ring after key release
            {
                if (!_forceSustain && e.On != _pedalDown) { AppendPedal(e.On); _pedalDown = e.On; emitted++; }
                continue;   // when forced, ignore the MIDI's pedal — it's already held down
            }
            if (!e.On) continue;   // release is time-scheduled below, not event-driven

            int key = e.Note + _transpose;
            if (key < 0 || key > 127) continue;   // outside MIDI range — game would drop it anyway

            // Polyphony cap: if a new note would exceed the limit, free a voice. Steal the oldest if an older note
            // is still sounding; if everything sounding started at this same time (a chord), drop the LOWEST pitch —
            // which may be this incoming note, in which case skip it entirely.
            if (_maxPoly > 0 && !_held.ContainsKey(key) && _held.Count >= _maxPoly && _heldOrder.Count > 0)
            {
                int oldest = _heldOrder[0];
                if (_heldOn.TryGetValue(oldest, out int oldOn) && oldOn < e.TimeMs)
                {
                    _heldOrder.RemoveAt(0); _held.Remove(oldest); _heldOn.Remove(oldest);
                    _sb.Append("v:ReleaseKeyByKeyCount(").Append(oldest).Append(") "); emitted++;
                }
                else
                {
                    int lowKey = key;
                    foreach (var kk in _held.Keys) if (kk < lowKey) lowKey = kk;
                    if (lowKey == key) continue;   // incoming is the lowest of the chord → drop it
                    _held.Remove(lowKey); _heldOrder.Remove(lowKey); _heldOn.Remove(lowKey);
                    _sb.Append("v:ReleaseKeyByKeyCount(").Append(lowKey).Append(") "); emitted++;
                }
            }

            // Re-attack: if this key is still sounding (overlap or repeat), release before pressing so it re-strikes.
            if (_held.ContainsKey(key)) _sb.Append("v:ReleaseKeyByKeyCount(").Append(key).Append(") ");
            _sb.Append("v:PressKeyByKeyCount(").Append(key).Append(") ");

            double offMs = _offForOn[idx] >= 0 ? _offForOn[idx] : _song.DurationMs; // unmatched → hold to song end
            double rel   = offMs + _holdOffsetMs;
            double min   = e.TimeMs + MinHoldMs;
            if (rel < min) rel = min;
            _held[key] = rel;
            _heldOn[key] = e.TimeMs;
            _heldOrder.Remove(key); _heldOrder.Add(key);   // newest at the tail
            _sentNotes++;
            emitted++;
        }

        // 2) Release any held key whose scheduled release time has passed.
        if (_held.Count > 0)
        {
            _toRelease.Clear();
            foreach (var kv in _held) if (kv.Value <= _elapsedMs) _toRelease.Add(kv.Key);
            foreach (var key in _toRelease)
            {
                _held.Remove(key);
                _heldOrder.Remove(key);
                _heldOn.Remove(key);
                _sb.Append("v:ReleaseKeyByKeyCount(").Append(key).Append(") ");
                emitted++;
            }
        }

        if (emitted > 0) FlushChunk(_sb.ToString());

        // End of song: all events consumed and every scheduled release has fired.
        if (_cursor >= events.Count && _held.Count == 0 && _elapsedMs >= _song.DurationMs)
            EndReached();
    }

    // Appends a sustain-pedal command to the current frame chunk, mirroring the game's own input path:
    // vm:HandleInstrumentPedal(on, view.cancelSource:CreateToken()) — sets local pedal state and syncs it.
    private void AppendPedal(bool on)
    {
        if (!_vmDeclaredThisFrame) { _sb.Append("local vm=Z.VMMgr.GetVM('band') "); _vmDeclaredThisFrame = true; }
        _sb.Append("vm:HandleInstrumentPedal(").Append(on ? "true" : "false").Append(", v.cancelSource:CreateToken()) ");
    }

    private void ReleaseAllHeld()
    {
        if (_held.Count == 0 && !_pedalDown) return;
        _sb.Clear();
        _vmDeclaredThisFrame = false;
        foreach (var key in _held.Keys) _sb.Append("v:ReleaseKeyByKeyCount(").Append(key).Append(") ");
        if (_pedalDown) { AppendPedal(false); _pedalDown = false; }
        FlushChunk(_sb.ToString());
    }

    // Re-fetch the live view each frame so the chunk is a no-op the instant the player exits Free-Play.
    private void FlushChunk(string body)
        => _callLua("pcall(function() local v=Z.UIMgr:GetView('band_performance_main_pc') if v==nil then return end " + body + "end)");

    public void Dispose() => Stop();
}
