using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Il2CppInterop reflection plumbing for PreviewSynth — resolve once, then rent/post/update players by reflection.
// The recipe (and why each step is needed) is documented in Band-Instrument-Playback.md "Local game-sound preview".
internal sealed partial class PreviewSynth
{
    private Type?       _tPlayer, _tTechEnum;
    private MethodInfo? _mRent, _mReturn, _mUpdate, _mUpdatePos, _mStopAll, _mSetTone, _mSetTech, _mStopPid;
    private PropertyInfo? _pProvider, _pEventName;
    private object?     _noteOn;               // boxed AkMIDIEventTypes NOTE_ON(144)
    private const int   NoteFadeMs = 100;      // note-off fade — matches the instrument config's NoteReleaseTime (0.1s)

    // Per-instrument tone-id table (toneCat 0/1/2 → tone id) and valid technique kinds — from the game's own RESOLVE map.
    private static readonly Dictionary<int, int[]> TT = new()
    {
        { 10001, new[] { 1000000, 1000000, 1000000 } },   // piano  (one tone)
        { 10002, new[] { 1000001, 1000002, 1000003 } },   // guitar Clean/Overdrive/Distortion
        { 10003, new[] { 1000006, 1000006, 1000006 } },   // drum
        { 10004, new[] { 1000004, 1000005, 1000005 } },   // bass   Clean/Overdrive (no Distortion → Overdrive)
    };
    private static readonly Dictionary<int, HashSet<int>> MM = new()
    {
        { 10001, new() { 1 } }, { 10002, new() { 1, 2, 3 } }, { 10003, new() { 1 } }, { 10004, new() { 1, 2, 3, 6 } },
    };

    // GM program → (toneCat 0/1/2 = Clean/Overdrive/Distortion, techKind 1/2/3/6 = Sustained/Muffled/Harmonics/Slap).
    private static (int toneCat, int techKind) EffectFromProgram(int program)
    {
        if (program >= 24 && program <= 31)   // guitar family
            return (program == 30 ? 2 : program == 29 ? 1 : 0, program == 28 ? 2 : program == 31 ? 3 : 1);
        if (program >= 32 && program <= 39)   // bass family
            return ((program == 38 || program == 39) ? 1 : 0, (program == 36 || program == 37) ? 6 : 1);
        return (0, 1);                          // piano / drums / unknown / no program → Clean + Sustained
    }
    private object?     _zeroPos;              // boxed Vector3.zero (fallback listener position)
    private PropertyInfo? _pCamMain, _pCamPos; // UnityEngine.Camera.main / transform.position
    private object?     _camTransform;         // cached main-camera transform (re-resolved if it dies)
    private bool        _interopReady;

    private void LogEx(string where, Exception ex)
    {
        var e = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
        Log($"EXCEPTION {where}: {e.GetType().Name}: {e.Message}");
    }

    // Resolve every reflection handle once. Returns false if the core type/methods aren't present.
    private bool EnsureInterop()
    {
        if (_interopReady) return true;
        try
        {
            _tPlayer = StellarInterop.FindType("Panda.ZAudio.InstrumentPlayer");
            if (_tPlayer == null) { Log("InstrumentPlayer type not found"); return false; }

            var stat = BindingFlags.Static | BindingFlags.Public;
            var inst = BindingFlags.Instance | BindingFlags.Public;
            var priv = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _mRent = _tPlayer.GetMethods(stat).FirstOrDefault(m => m.Name == "Rent" && m.GetParameters().Length == 4
                                                                   && m.GetParameters()[2].ParameterType.Name == "Vector3");
            _mReturn    = _tPlayer.GetMethods(stat).FirstOrDefault(m => m.Name == "Return");
            _mUpdate    = _tPlayer.GetMethod("Update", Type.EmptyTypes);
            _mUpdatePos = _tPlayer.GetMethods(inst).FirstOrDefault(m => m.Name == "UpdatePosition" && m.GetParameters().Length == 1);
            _mStopAll   = _tPlayer.GetMethod("StopAllNotes", Type.EmptyTypes);
            _mSetTone   = _tPlayer.GetMethod("SetTone", new[] { typeof(int) });
            _mSetTech   = _tPlayer.GetMethods(inst).FirstOrDefault(m => m.Name == "SetTechnique" && m.GetParameters().Length == 1);
            _tTechEnum  = _mSetTech?.GetParameters()[0].ParameterType;   // EPlayingTechnique
            _pProvider  = _tPlayer.GetProperty("audioProvider_", priv);
            _pEventName = _tPlayer.GetProperty("midiEventName_", priv);

            if (_mRent == null || _pProvider == null || _pEventName == null)
            { Log($"missing core handles: Rent={_mRent != null} provider={_pProvider != null} event={_pEventName != null}"); return false; }

            var tVec = StellarInterop.FindType("UnityEngine.Vector3");
            _zeroPos = tVec?.GetProperty("zero", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

            var tCam = StellarInterop.FindType("UnityEngine.Camera");
            _pCamMain = tCam?.GetProperty("main", BindingFlags.Static | BindingFlags.Public);

            // AkSoundEngine.StopPlayingID(playingId, fadeMs) — the only reliable way to stop a directly-posted MIDI note.
            var tAk = StellarInterop.FindType("AkSoundEngine");
            _mStopPid = tAk?.GetMethod("StopPlayingID", new[] { typeof(uint), typeof(int) });
            if (_mStopPid == null) Log("StopPlayingID(uint,int) not found — notes won't stop");

            _interopReady = true;
            return true;
        }
        catch (Exception ex) { LogEx("EnsureInterop", ex); return false; }
    }

    // Rent an instrument player for stem `key`. The instrument TYPE (mode) picks the game id + timbre; the stem key
    // makes the uuid + Wwise name unique so two stems of the same instrument (e.g. Bass + Bass 2) get separate players.
    private Voice? RentVoice(string key)
    {
        try
        {
            string mode = _stemMode.TryGetValue(key, out var mm) ? mm : "piano";
            int id = Slots.First(s => s.mode == mode).id;
            long uuid = -900000000L - (long)(uint)key.GetHashCode();   // distinct per stem key (not per instrument)
            object pos = CameraPos();
            var player = _mRent!.Invoke(null, new object[] { id, uuid, pos, "maestro_preview_" + key });
            if (player == null) { Log($"rent {key}: null"); return null; }

            InitDefaults(player, id);

            var provider = _pProvider!.GetValue(player);
            var evName   = _pEventName!.GetValue(player) as string;
            if (provider == null || string.IsNullOrEmpty(evName)) { Log($"rent {key}: provider/event missing"); return null; }

            var post = provider.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                               .FirstOrDefault(m => m.Name == "PostMidiEvent" && m.GetParameters().Length == 4);
            if (post == null) { Log($"rent {key}: PostMidiEvent missing"); return null; }

            if (_noteOn == null)   // resolve the AkMIDIEventTypes NOTE_ON value once from the method signature
                _noteOn = Enum.ToObject(post.GetParameters()[2].ParameterType, 144);

            return new Voice { Mode = mode, Id = id, Player = player, Provider = provider, Post = post, EventName = evName! };
        }
        catch (Exception ex) { LogEx($"RentVoice({key})", ex); return null; }
    }

    // Apply the config's default tone + technique, as the game does on summon (avoids an unset-switch NRE path).
    private void InitDefaults(object player, int id)
    {
        try
        {
            var cfg = GetConfig(id);
            if (cfg == null) return;
            var ct   = cfg.GetType();
            var tone = ct.GetProperty("DefaultToneValue",       BindingFlags.Instance | BindingFlags.Public)?.GetValue(cfg);
            var tech = ct.GetProperty("DefaultTechniqueSwitch", BindingFlags.Instance | BindingFlags.Public)?.GetValue(cfg);
            if (tech != null && _mSetTech != null) try { _mSetTech.Invoke(player, new[] { tech }); } catch { }
            if (tone != null && _mSetTone != null) try { _mSetTone.Invoke(player, new object[] { Convert.ToInt32(tone) }); } catch { }
        }
        catch (Exception ex) { LogEx("InitDefaults", ex); }
    }

    private object? GetConfig(int id)
    {
        try
        {
            var tMgr = StellarInterop.FindType("Panda.ZAudio.ZInstrumentMgr") ?? StellarInterop.FindType("ZInstrumentMgr");
            var mgr  = StellarInterop.GetSingleton(tMgr);
            var m    = tMgr?.GetMethod("TryGetInstrumentConfig");
            if (mgr == null || m == null) return null;
            var args = new object?[] { id, null };
            return (bool)m.Invoke(mgr, args)! ? args[1] : null;
        }
        catch { return null; }
    }

    // Sustain-aware note-on: re-attack a still-ringing pitch, capture and remember this note's playingId.
    private void NoteOnV(Voice v, int note, int vel)
    {
        if (v.Sounding.TryGetValue(note, out var oldPid)) StopPid(oldPid, 0);   // re-strike: hard-stop the ringing voice first
        uint pid = PostOnRaw(v, note, vel);
        if (pid != 0) { v.Sounding[note] = pid; v.Sustained.Remove(note); }
    }

    // Sustain-aware note-off: while the pedal is down, DEFER the release until the pedal lifts (SetVoicePedalDown).
    private void NoteOffV(Voice v, int note)
    {
        if (v.Id == 10003) return;   // drums are one-shots — no note-off; each hit rings its natural sample
        if (v.PedalDown) { v.Sustained.Add(note); return; }
        if (v.Sounding.TryGetValue(note, out var pid)) { StopPid(pid, NoteFadeMs); v.Sounding.Remove(note); }
    }

    // Engage/lift sustain for a track. Lifting releases every deferred note.
    private void SetVoicePedalDown(Voice v, bool down)
    {
        if (v.PedalDown == down) return;
        v.PedalDown = down;
        if (!down)
        {
            foreach (var n in v.Sustained) if (v.Sounding.TryGetValue(n, out var pid)) { StopPid(pid, NoteFadeMs); v.Sounding.Remove(n); }
            v.Sustained.Clear();
        }
    }

    // Post a NOTE_ON on the voice's current event; return the Wwise playingId so we can stop exactly this note later.
    private uint PostOnRaw(Voice v, int note, int vel)
    {
        try { return Convert.ToUInt32(v.Post.Invoke(v.Provider, new object[] { v.EventName, note, _noteOn!, Math.Clamp(vel, 1, 127) })); }
        catch (Exception ex) { LogEx($"PostOn({v.Mode},{note})", ex); return 0; }
    }

    // Stop a specific Wwise voice by playingId. fadeMs>0 = release fade (≈ NoteReleaseTime); 0 = immediate cut.
    private void StopPid(uint pid, int fadeMs)
    {
        if (pid == 0 || _mStopPid == null) return;
        try { _mStopPid.Invoke(null, new object[] { pid, fadeMs }); }
        catch (Exception ex) { LogEx("StopPlayingID", ex); }
    }

    // Silence a voice: explicitly note-off every sounding note (on its own event) — StopAllNotes on the player does NOT
    // touch our directly-posted voices — then also call it as a belt-and-suspenders.
    private void StopAll(Voice v)
    {
        foreach (var kv in v.Sounding) StopPid(kv.Value, 0);   // immediate hard cut of every voice we started
        v.Sounding.Clear(); v.Sustained.Clear();
        try { _mStopAll?.Invoke(v.Player, null); } catch { }
    }

    // Set the voice's timbre + technique: from the MIDI's program when applyTone, else Clean+Sustained. Clamped per
    // instrument (guitar has no Slap, bass has no Distortion, piano/drum have one tone) via the TT/MM tables.
    private void ApplyEffect(Voice v, int program, bool applyTone)
    {
        var (toneCat, techKind) = applyTone ? EffectFromProgram(program) : (0, 1);
        if (!TT.TryGetValue(v.Id, out var trow)) return;
        int toneId = trow[Math.Clamp(toneCat, 0, 2)];
        int kind   = (MM.TryGetValue(v.Id, out var mm) && mm.Contains(techKind)) ? techKind : 1;
        try { _mSetTone?.Invoke(v.Player, new object[] { toneId }); } catch (Exception ex) { LogEx($"SetTone({v.Mode})", ex); }
        if (_mSetTech != null && _tTechEnum != null)
            try { _mSetTech.Invoke(v.Player, new[] { Enum.ToObject(_tTechEnum, kind) }); } catch (Exception ex) { LogEx($"SetTechnique({v.Mode})", ex); }

        // Tone (timbre) has no persistent switch like technique — SetTone may instead swap the player's Wwise EVENT.
        // We post via a cached event name, so re-read it here; if it changed, distortion/overdrive will actually take.
        try
        {
            var ev = _pEventName?.GetValue(v.Player) as string;
            if (!string.IsNullOrEmpty(ev) && ev != v.EventName) { Log($"[tone] {v.Mode} toneId={toneId}: event {v.EventName} -> {ev}"); v.EventName = ev!; }
        }
        catch { }
    }

    private void ReturnVoice(Voice v)
    {
        try { if (_mReturn != null) _mReturn.Invoke(null, new[] { v.Player }); } catch { }
    }

    // Tick each voice's Update() and pin it to the audio listener (camera) so 3D attenuation doesn't silence it.
    private void UpdateVoices()
    {
        object pos = CameraPos();
        foreach (var v in _voices.Values)
        {
            try { _mUpdate?.Invoke(v.Player, null); } catch { }
            if (pos != null && _mUpdatePos != null) try { _mUpdatePos.Invoke(v.Player, new[] { pos }); } catch { }
        }
    }

    // Current main-camera position (boxed Vector3), falling back to zero. Caches the transform; re-resolves if it dies.
    private object CameraPos()
    {
        try
        {
            if (_camTransform == null)
            {
                var cam = _pCamMain?.GetValue(null);
                _camTransform = cam?.GetType().GetProperty("transform", BindingFlags.Instance | BindingFlags.Public)?.GetValue(cam);
                if (_camTransform != null)
                    _pCamPos = _camTransform.GetType().GetProperty("position", BindingFlags.Instance | BindingFlags.Public);
            }
            var p = _pCamPos?.GetValue(_camTransform);
            if (p != null) return p;
        }
        catch { _camTransform = null; }   // stale/destroyed → re-resolve next frame
        return _zeroPos!;
    }

}
