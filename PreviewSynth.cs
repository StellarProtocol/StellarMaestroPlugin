using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// In-game "game-sound preview": plays a song's stems locally through the REAL game instrument timbres, with no summon
// and no network — by renting a Panda.ZAudio.InstrumentPlayer per instrument and posting MIDI straight through its
// audio provider. Each track (piano/guitar/bass/drum) has its OWN, independent controls: mute, sustain mode
// (File = respect the MIDI's pedal / Hold = force-sustain the whole song / Off), and tone-from-MIDI. One shared clock
// keeps every track in sync. All game types are reflected. See Band-Instrument-Playback.md "Local game-sound preview".
internal sealed partial class PreviewSynth : IDisposable
{
    // mode → game instrument id.
    internal static readonly (string mode, int id)[] Slots =
        { ("piano", 10001), ("guitar", 10002), ("bass", 10004), ("drum", 10003) };

    internal const int PedalFile = 0, PedalHold = 1, PedalOff = 2;

    private readonly IPluginServices _services;

    private sealed class Voice
    {
        public string Mode = "";
        public int    Id;
        public object Player = null!;
        public object Provider = null!;
        public MethodInfo Post = null!;   // PostMidiEvent(string, int, AkMIDIEventTypes, int)
        public string EventName = "";
        public bool PedalDown;                                   // sustain currently engaged for this track
        public readonly Dictionary<int, uint> Sounding = new();  // note → Wwise playingId (each PostMidiEvent note-on is its own instance; stop it by playingId)
        public readonly HashSet<int> Sustained = new();          // note-offs deferred until the pedal lifts
    }
    private readonly Dictionary<string, Voice> _voices = new();

    private enum EvKind : byte { NoteOn, NoteOff, PedalDown, PedalUp, Program }
    private readonly struct Ev
    {
        public readonly int Ms; public readonly string Key; public readonly EvKind Kind;
        public readonly byte Note; public readonly byte Vel; public readonly int Program;
        public Ev(int ms, string key, EvKind kind, byte note = 0, byte vel = 0, int program = 0)
        { Ms = ms; Key = key; Kind = kind; Note = note; Vel = vel; Program = program; }
    }

    private readonly Dictionary<string, MidiSong> _stems = new();     // stem key → parsed song (for program/pedal timelines)
    private readonly Dictionary<string, string>   _stemMode = new();  // stem key → instrument mode (piano/guitar/bass/drum) for timbre
    private List<Ev> _events = new();
    private readonly List<string> _loadedKeys = new();               // stem keys, in load order (one per stem file)

    // Per-track controls (persist across previews). Defaults: audible, sustain=File, tone-from-MIDI on.
    private readonly HashSet<string> _muted = new();
    private readonly Dictionary<string, int>  _pedalMode = new();   // PedalFile / PedalHold / PedalOff
    private readonly Dictionary<string, bool> _applyTone = new();

    private readonly Stopwatch _clock = new();
    private double _posMs;
    private double _baseMs;   // position = _clock.Elapsed + _baseMs (lets Seek jump the position; Stopwatch can't be set)
    private int    _cursor;
    private bool   _playing, _paused, _subscribed;
    private int    _durationMs;

    // Instrument-sync: when set, the preview FOLLOWS an external master (the Maestro band player) instead of its own
    // clock — arms on play, waits for the master to start, then locks position (and the seek bar) to it.
    private Func<(bool active, int posMs)>? _syncSource;
    private bool _syncStarted;

    internal PreviewSynth(IPluginServices services) => _services = services;
    private void Log(string m) => _services.Log.Info("[PreviewSynth] " + m);

    internal bool IsPlaying => _playing;
    internal bool IsPaused  => _paused;
    internal int  PositionMs => (int)_posMs;
    internal int  DurationMs => _durationMs;
    internal IReadOnlyList<string> LoadedKeys => _loadedKeys;

    // ---- per-track control accessors (live) ----
    internal bool IsMuted(string m) => _muted.Contains(m);
    internal void SetMute(string m, bool on)
    { if (on) { if (_muted.Add(m) && _voices.TryGetValue(m, out var v)) StopAll(v); } else _muted.Remove(m); }

    internal int  GetPedalMode(string m) => _pedalMode.TryGetValue(m, out var v) ? v : PedalFile;
    internal void SetPedalMode(string m, int mode)
    { _pedalMode[m] = ((mode % 3) + 3) % 3; if (_playing && _voices.TryGetValue(m, out var v)) ApplyPedalNow(v, m); }

    internal bool GetApplyTone(string m) => !_applyTone.TryGetValue(m, out var v) || v;   // default true
    internal void SetApplyTone(string m, bool on)
    { _applyTone[m] = on; if (_playing && _voices.TryGetValue(m, out var v)) ApplyEffectNow(v, m); }

    // Change a LOADED stem's instrument (timbre) live: return its old voice and re-rent with the new instrument,
    // preserving the stem's mute/sustain/tone. No-op if the stem isn't loaded (the plugin applies the override at
    // the next Load). The notes are unchanged — only the sound (and sustain/tone gating) follow the new instrument.
    internal void SetStemMode(string key, string mode)
    {
        if (!_stemMode.TryGetValue(key, out var cur) || cur == mode) return;
        _stemMode[key] = mode;
        if (_voices.TryGetValue(key, out var v)) { StopAll(v); ReturnVoice(v); _voices.Remove(key); }
        if (_playing || _paused)
        {
            var nv = RentVoice(key);
            if (nv != null) { _voices[key] = nv; ApplyEffectNow(nv, key); ApplyPedalNow(nv, key); }
        }
    }


    // Parse each stem, keep per-stem songs (+ its instrument mode for timbre), and merge notes/pedal/program into one
    // time-sorted stream tagged by stem key. Multiple stems of the same instrument (e.g. Bass + Bass 2) are distinct keys.
    internal void Load(IEnumerable<(string key, string mode, string path)> stems)
    {
        _stems.Clear(); _stemMode.Clear(); _loadedKeys.Clear();
        var evs = new List<Ev>();
        foreach (var (key, mode, path) in stems)
        {
            var song = MidiParser.TryParseFile(path, out var err);
            if (song == null) { Log($"parse failed {key}: {err}"); continue; }
            _stems[key] = song;
            _stemMode[key] = mode;
            if (!_loadedKeys.Contains(key)) _loadedKeys.Add(key);
            foreach (var e in song.Events)
            {
                if (e.Pedal) evs.Add(new Ev(e.TimeMs, key, e.On ? EvKind.PedalDown : EvKind.PedalUp));
                else         evs.Add(new Ev(e.TimeMs, key, e.On ? EvKind.NoteOn : EvKind.NoteOff, e.Note, e.Velocity));
            }
            foreach (var (ms, prog) in song.ProgramChanges) evs.Add(new Ev(ms, key, EvKind.Program, program: prog));
            Log($"loaded {key} ({mode}): {song.NoteCount} notes, {song.ProgramChanges.Count} prog-changes ({System.IO.Path.GetFileName(path)})");
        }
        // At the same instant: note-offs first, then pedal/program (so a note plays with the new pedal/tone), then note-ons.
        evs.Sort((a, b) => a.Ms != b.Ms ? a.Ms.CompareTo(b.Ms) : SortKey(a.Kind).CompareTo(SortKey(b.Kind)));
        _events = evs;
        _durationMs = 0;
        foreach (var e in evs) if (e.Kind == EvKind.NoteOn || e.Kind == EvKind.NoteOff) _durationMs = Math.Max(_durationMs, e.Ms);
        // Clamp to each stem's full length (End-of-Track), not just its last sounding note, so padded-to-equal-length
        // stems report a consistent length here too (mirrors the MidiSong.DurationMs / _b2DurationMs fixes).
        foreach (var s in _stems.Values) _durationMs = Math.Max(_durationMs, s.DurationMs);
    }
    private static int SortKey(EvKind k) => k == EvKind.NoteOff ? 0 : k == EvKind.NoteOn ? 2 : 1;

    internal bool IsSyncing  => _syncSource != null;
    internal bool SyncWaiting => _syncSource != null && !_syncStarted;

    // Rent voices for the loaded stems and (re)apply each track's tone/technique + sustain. Shared by Play/PlaySynced.
    private bool Arm()
    {
        if (_events.Count == 0) { Log("nothing loaded"); return false; }
        if (!EnsureInterop()) { Log("interop resolve failed"); return false; }
        foreach (var key in _loadedKeys)
        {
            if (!_voices.TryGetValue(key, out var v)) { v = RentVoice(key); if (v == null) continue; _voices[key] = v; }
            ApplyEffectNow(v, key);   // (re)apply tone/technique for this song (voices persist across Stop)
            ApplyPedalNow(v, key);    // (re)apply sustain state
        }
        if (_voices.Count == 0) { Log("no voices could be rented"); return false; }
        return true;
    }

    private void Subscribe() { if (!_subscribed) { _services.Framework.Update += Tick; _subscribed = true; } }

    internal bool Play()
    {
        if (_playing) return true;
        if (!Arm()) return false;
        _syncSource = null; _syncStarted = false;
        _cursor = 0; _posMs = 0; _baseMs = 0;
        _clock.Restart();
        _playing = true; _paused = false;
        Subscribe();
        Log($"play: {_voices.Count} voice(s) [{string.Join(",", _voices.Keys)}], {_events.Count} events, {_durationMs}ms");
        return true;
    }

    // Arm instrument-sync: play nothing until `source` reports active (the band player started), then follow its position.
    internal bool PlaySynced(Func<(bool active, int posMs)> source)
    {
        if (_playing) return true;
        if (!Arm()) return false;
        _syncSource = source; _syncStarted = false;
        _cursor = 0; _posMs = 0;
        _playing = true; _paused = false;
        Subscribe();
        Log("synced play: waiting for the band player to start…");
        return true;
    }

    internal void Stop()
    {
        _playing = false; _paused = false;
        _clock.Stop();
        if (_subscribed) { try { _services.Framework.Update -= Tick; } catch { } _subscribed = false; }
        // Silence but KEEP the rented players — recycling here orphans our directly-posted voices (they keep ringing).
        foreach (var v in _voices.Values) StopAll(v);
        _posMs = 0; _cursor = 0; _baseMs = 0;
        _syncSource = null; _syncStarted = false;
    }

    internal void Pause() { if (!_playing) return; _playing = false; _paused = true; _clock.Stop(); foreach (var v in _voices.Values) StopAll(v); }
    internal void Resume() { if (!_paused) return; _paused = false; _playing = true; _clock.Start(); }

    // Jump to targetMs (playing or paused): re-base the clock, silence ringing notes, reposition the cursor, and
    // re-apply each track's tone/technique + sustain for the new spot.
    internal void Seek(int targetMs)
    {
        if (_syncSource != null) return;   // position is driven by the band player in sync mode
        if (_events.Count == 0 || (!_playing && !_paused)) return;
        targetMs = Math.Clamp(targetMs, 0, _durationMs);
        _baseMs = targetMs - _clock.Elapsed.TotalMilliseconds;
        _posMs  = targetMs;
        _cursor = 0;
        while (_cursor < _events.Count && _events[_cursor].Ms < targetMs) _cursor++;
        foreach (var kv in _voices) { StopAll(kv.Value); ApplyEffectNow(kv.Value, kv.Key); ApplyPedalNow(kv.Value, kv.Key); }
    }

    private void Tick(float dt)
    {
        if (!_playing) return;
        if (_syncSource != null) { TickSynced(); return; }

        _posMs = _clock.Elapsed.TotalMilliseconds + _baseMs;
        UpdateVoices();
        FireDueEvents();
        if (_cursor >= _events.Count && _posMs >= _durationMs + 250) { Log("preview complete"); Stop(); }
    }

    // Follow the master (band player): wait until active, lock our position to it, fire events. A large position jump =
    // the band seeked → reposition + silence. When the band stops after starting, stop the preview too.
    private void TickSynced()
    {
        var (active, pos) = _syncSource!();
        UpdateVoices();
        if (!active)
        {
            if (_syncStarted) { Log("band player stopped — preview stop"); Stop(); }
            return;   // else: still waiting for the band to start
        }
        _syncStarted = true;
        if (pos < _posMs - 60 || pos > _posMs + 1000)   // band seeked (discontinuity)
        {
            _cursor = 0; while (_cursor < _events.Count && _events[_cursor].Ms < pos) _cursor++;
            foreach (var kv in _voices) { StopAll(kv.Value); ApplyEffectNow(kv.Value, kv.Key); ApplyPedalNow(kv.Value, kv.Key); }
        }
        _posMs = pos;
        FireDueEvents();
    }

    private void FireDueEvents()
    {
        while (_cursor < _events.Count && _events[_cursor].Ms <= _posMs)
        {
            var e = _events[_cursor++];
            if (!_voices.TryGetValue(e.Key, out var v)) continue;
            switch (e.Kind)
            {
                case EvKind.NoteOn:  if (!_muted.Contains(e.Key)) NoteOnV(v, e.Note, e.Vel); break;
                case EvKind.NoteOff: NoteOffV(v, e.Note); break;
                case EvKind.PedalDown: if (GetPedalMode(e.Key) == PedalFile) SetVoicePedalDown(v, true);  break;
                case EvKind.PedalUp:   if (GetPedalMode(e.Key) == PedalFile) SetVoicePedalDown(v, false); break;
                case EvKind.Program:   if (GetApplyTone(e.Key)) ApplyEffect(v, e.Program, true); break;
            }
        }
    }

    // ---- per-track apply helpers ----
    // Set the voice's tone/technique for the current position: from the active program (if tone-from-MIDI on) else Clean/Sustained.
    private void ApplyEffectNow(Voice v, string key)
    {
        bool apply = GetApplyTone(key);
        int program = apply ? ActiveProgramAt(key, (int)_posMs) : -1;
        ApplyEffect(v, program, apply);
    }

    // Set the voice's sustain for the current position per its pedal mode.
    private void ApplyPedalNow(Voice v, string key)
    {
        int m = GetPedalMode(key);
        bool down = m == PedalHold || (m == PedalFile && PedalStateAt(key, (int)_posMs));
        SetVoicePedalDown(v, down);
    }

    private int ActiveProgramAt(string key, int ms)
    {
        if (!_stems.TryGetValue(key, out var song)) return -1;
        int prog = song.Program;
        foreach (var (t, p) in song.ProgramChanges) { if (t <= ms) prog = p; else break; }
        return prog;
    }

    private bool PedalStateAt(string key, int ms)
    {
        if (!_stems.TryGetValue(key, out var song)) return false;
        bool down = false;
        foreach (var e in song.Events) { if (!e.Pedal) continue; if (e.TimeMs <= ms) down = e.On; else break; }
        return down;
    }

    public void Dispose()
    {
        Stop();
        foreach (var v in _voices.Values) ReturnVoice(v);   // release the rented players only on teardown
        _voices.Clear();
    }
}
