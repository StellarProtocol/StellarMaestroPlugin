using System;
using System.Collections.Generic;

namespace Stellar.Maestro;

// Approach B2 — pre-buffered, musical-time-stamped network send for reliable remote playback.
//
// The default path (B1) drives the view's PressKeyByKeyCount, which stamps each note's network playTime at the
// press moment and flushes one small unreliable notify per frame — so under dense note streams, lost packets
// become dropped notes for listeners (see Band-Instrument-Playback.md, "Remote drop-outs").
//
// B2 decouples send-time from play-time:
//   • Local audio is driven directly via InstrumentService.EntityInstrument{Play,Release}Note (no view sync).
//   • Network records are stamped playTime = songStartServerTime + eventRealMs and sent ~250ms AHEAD in fewer,
//     larger batches. The receiver's ~500ms jitter buffer schedules them precisely by playTime, so early sends
//     still land on-beat and there is no "arrived too late → dropped" — packet loss is the only remaining risk.
//
// Tempo/hold are frozen into the timeline at Play (they can't be adjusted live in this mode).
public sealed partial class MidiPlayer
{
    private double _lookaheadMs   = 400.0;  // how far ahead of playback to send records (under the ~500ms buffer)
    private double _sendIntervalMs = 100.0; // batch cadence
    private readonly System.Text.StringBuilder _payload = new();

    // Live network tuning (exposed via the Network Settings window).
    public int NetLookaheadMs { get => (int)_lookaheadMs;    set => _lookaheadMs    = Math.Clamp(value, 100, 1500); }
    public int NetBatchMs     { get => (int)_sendIntervalMs; set => _sendIntervalMs = Math.Clamp(value, 16, 250); }

    private enum CmdType : byte { Release = 0, PedalOff = 1, PedalOn = 2, Press = 3 } // value = same-time ordering

    private readonly struct Cmd
    {
        public readonly double RealMs; public readonly int Key; public readonly CmdType Type;
        public Cmd(double realMs, int key, CmdType type) { RealMs = realMs; Key = key; Type = type; }
    }

    private bool   _netMode;                     // false = B1 (view sync), true = B2 (pre-buffered)
    private double _realMs;                      // real elapsed ms since Play (B2 clock; timeline already bakes tempo)
    private double _b2ClockOffset;               // added to the wall clock so a seek can reposition _realMs
    private int    _b2DurationMs;                // real-time song length (last timeline command), for the position UI
    private int    _playIdx, _sendIdx;           // separate cursors into the timeline (send runs ahead of play)
    private double _sendAccumMs;
    private bool   _b2ForcePedalSent;            // force-sustain: the one-shot SustainPedal DOWN has been sent this play
    private const long B2RebuildDebounceMs = 200; // settle time before rebuilding after a live transpose/hold/tempo change
    private double _restrikeGapMs;                // ms to release a restruck note BEFORE the re-press; 0 = off (release exactly at re-press)
    public int RestrikeGapMs { get => (int)_restrikeGapMs; set { _restrikeGapMs = Math.Clamp(value, 0, 100); MarkB2Dirty(); } }   // live in buffered mode

    private bool _muteLocal;                      // buffered mode: skip local EntityInstrumentPlayNote (listeners still hear)
    public bool MuteLocal
    {
        get => _muteLocal;
        set
        {
            if (_muteLocal == value) return;
            _muteLocal = value;
            if (value && _playing && _netMode) ReleaseAllHeldB2();   // silence any locally-ringing notes immediately
        }
    }

    private bool _showKeyViz;                      // buffered mode: mirror the on-screen key-press visual (view internals)
    public bool ShowKeyViz { get => _showKeyViz; set => _showKeyViz = value; }

    // Replicates the visual half of the view's PressKeyByKeyCount/ReleaseKeyByKeyCount (light the key + click effect)
    // WITHOUT the AddWaitSend* network sync — so buffered mode shows the animation but stays the sole network sender.
    // Registered as Lua globals __mvp/__mvr so the per-note flush only emits a tiny call. Guarded (pcall + nil checks).
    private void DefineKeyVizHelpers() => _callLua(
        "pcall(function() " +
        "rawset(_G,'__mvp',function(kc) local v=Z.UIMgr:GetView('band_performance_main_pc') if v==nil then return end " +
        "local ik=v:GetKeyIndexByKeyCount(kc) local it=v.uiBinder.binder_key_root['band_key_item_tpl_'..ik] if it==nil then return end " +
        "local mc=Z.TableMgr.GetRow('BandMidiTableMgr',kc) if mc==nil then return end " +
        "it.Ref:SetVisible(it.img_press,true) it.Ref:SetVisible(it.img_prompt,false) if it.img_bg~=nil then it.Ref:SetVisible(it.img_bg,false) end " +
        "it.img_press:SetColorByHex(mc.KeyscolorBottom) it.effect_click:SetEffectGoVisible(true) v.bandData_.PressKeyDict[kc]=true end) " +
        "rawset(_G,'__mvr',function(kc) local v=Z.UIMgr:GetView('band_performance_main_pc') if v==nil then return end " +
        "local ik=v:GetKeyIndexByKeyCount(kc) local it=v.uiBinder.binder_key_root['band_key_item_tpl_'..ik] if it==nil then return end " +
        "local mc=Z.TableMgr.GetRow('BandMidiTableMgr',kc) if mc==nil then return end local ih=v.bandData_:CheckKeyIsHint(kc) " +
        "it.Ref:SetVisible(it.img_press,false) it.Ref:SetVisible(it.img_prompt,ih) if it.img_bg~=nil then it.Ref:SetVisible(it.img_bg,not ih) end " +
        "if ih then it.img_prompt:SetColorByHex(mc.KeyscolorBottom) elseif it.img_bottom~=nil then it.img_bottom:SetColorByHex(mc.KeyscolorBottom) end " +
        "v.bandData_.PressKeyDict[kc]=nil end) " +
        "end)");
    private bool   _b2Dirty;
    private long   _b2DirtyMs;
    private readonly List<Cmd>    _timeline = new();
    private readonly HashSet<int> _b2Held   = new();

    public bool PreBufferedNet { get => _netMode; set => _netMode = value; }

    // Ensemble grid: when set (by the plugin, before Start), B2 anchors __b2base to a measure downbeat and offsets the
    // local clock so both local audio and the network send begin on that server-time beat — a real count-in.
    public bool   EnsembleGrid;        // align to the grid this play
    public long   EnsembleBaseMs;      // grid downbeat (server ms) = __b2base
    public double EnsembleClockOffset; // _b2ClockOffset for the count-in (serverNowAtPlay − base, negative)
    public int CountInRemainMs => (_netMode && EnsembleGrid && _realMs < 0) ? (int)Math.Ceiling(-_realMs) : 0;

    // Called from Start() when in B2 mode: freeze timing into a real-ms command timeline and capture the server base.
    private void ResetB2()
    {
        _realMs        = 0;
        _b2ClockOffset = 0;
        _playIdx       = 0;
        _sendIdx       = 0;
        _sendAccumMs   = _sendIntervalMs;   // send the first lookahead batch on frame one
        _b2ForcePedalSent = false;
        _b2Dirty       = false;
        _b2Held.Clear();
        BuildTimeline();
        DefineKeyVizHelpers();
        if (EnsembleGrid && EnsembleBaseMs > 0)
        {
            _b2ClockOffset = EnsembleClockOffset;   // negative → local audio waits for the downbeat (count-in)
            _callLua("pcall(function() rawset(_G,'__b2base'," + EnsembleBaseMs + ") end)");
        }
        else
        {
            _callLua("pcall(function() rawset(_G,'__b2base', Z.ServerTime:GetServerTime()) end)");
        }
    }

    // Turn the parsed song into an ordered list of real-ms press/release/pedal commands, applying transpose,
    // hold offset, the min-hold floor, tempo, and repeated-note cutting (a note ends when the same pitch restrikes).
    private void BuildTimeline()
    {
        _timeline.Clear();
        var events = _song!.Events;
        double speed = _speed <= 0 ? 1.0 : _speed;
        double hold  = _holdOffsetMs;

        // Per-pitch ascending on-times, for "cut this note when the same pitch is next struck".
        var onByPitch = new Dictionary<int, List<int>>();
        foreach (var e in events)
            if (!e.Pedal && e.On)
            {
                if (!onByPitch.TryGetValue(e.Note, out var l)) { l = new List<int>(); onByPitch[e.Note] = l; }
                l.Add(e.TimeMs);
            }

        // Build note intervals (real-ms) with hold + min-hold + same-pitch restrike cut applied; pedal passes through.
        var onMs = new List<double>(); var relMs = new List<double>(); var keys = new List<int>();
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Pedal) { _timeline.Add(new Cmd(e.TimeMs / speed, 0, e.On ? CmdType.PedalOn : CmdType.PedalOff)); continue; }
            if (!e.On) continue;

            int key = e.Note + _transpose;
            if (key < 0 || key > 127) continue;

            double offSong = _offForOn[i] >= 0 ? _offForOn[i] : _song.DurationMs;
            double relSong = offSong + hold;
            double minRel  = e.TimeMs + MinHoldMs;
            if (relSong < minRel) relSong = minRel;
            // Same-pitch restrike cuts this note — but release it a hair BEFORE the next press, never exactly at it.
            // Emitting Release(K) and Press(K) at the identical playTime makes the receiver mis-order the two
            // same-pitch events (it either drops the release → note sticks, or applies it after the press → chops
            // the new note). A small gap gives them distinct, correctly-ordered timestamps. Gap is capped at half the
            // inter-onset interval so very fast repeats keep a positive duration.
            double nextOn = NextAfter(onByPitch[e.Note], e.TimeMs);
            // Pull the release to gap-ms before the next same-pitch press whenever it would otherwise land WITHIN the
            // restrike gap of it — this covers back-to-back notes (release ~0ms before the next press), not just
            // overlaps. Without it, adjacent same-pitch notes emit Release(K)+Press(K) at ~identical playTime → the
            // receiver mis-orders them and the note dies at the LISTENER (plays fine locally). Needs restrike gap > 0.
            if (nextOn >= 0 && nextOn - relSong < _restrikeGapMs)
            {
                double gap = Math.Min(_restrikeGapMs, (nextOn - e.TimeMs) * 0.5);   // capped at half the inter-onset
                relSong = nextOn - gap;
            }

            onMs.Add(e.TimeMs / speed); relMs.Add(relSong / speed); keys.Add(key);
        }

        // Polyphony cap: sweep notes in on-order; when one starts and the limit is reached, drop a voice. If an
        // OLDER note is still sounding, steal that (oldest) — good for sustained overlaps. If everything active
        // started at the SAME time (a chord), drop the LOWEST pitch instead (which may be the incoming note).
        if (_maxPoly > 0 && onMs.Count > 0)
        {
            var order = new int[onMs.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => onMs[a].CompareTo(onMs[b]));
            var active = new List<int>();   // note indices, oldest first (on-order)
            foreach (int idx in order)
            {
                double on = onMs[idx];
                active.RemoveAll(j => relMs[j] <= on);           // drop notes already ended
                if (active.Count >= _maxPoly)
                {
                    if (onMs[active[0]] < on)                     // an older note exists → steal the oldest
                    {
                        relMs[active[0]] = on;
                        active.RemoveAt(0);
                        active.Add(idx);
                    }
                    else                                         // all simultaneous → drop the lowest pitch
                    {
                        int lowPos = 0;
                        for (int k = 1; k < active.Count; k++) if (keys[active[k]] < keys[active[lowPos]]) lowPos = k;
                        if (keys[idx] <= keys[active[lowPos]]) { relMs[idx] = on; }   // incoming is lowest → drop it
                        else { relMs[active[lowPos]] = on; active.RemoveAt(lowPos); active.Add(idx); }
                    }
                }
                else active.Add(idx);
            }
        }

        for (int i = 0; i < onMs.Count; i++)
        {
            if (relMs[i] <= onMs[i]) continue;   // dropped by the cap (zero-length)
            _timeline.Add(new Cmd(onMs[i],  keys[i], CmdType.Press));
            _timeline.Add(new Cmd(relMs[i], keys[i], CmdType.Release));
        }

        _timeline.Sort((a, b) =>
        {
            int c = a.RealMs.CompareTo(b.RealMs);
            return c != 0 ? c : ((byte)a.Type).CompareTo((byte)b.Type); // releases/offs before presses/ons at same time
        });

        _b2DurationMs = _timeline.Count > 0 ? (int)_timeline[_timeline.Count - 1].RealMs : 0;
        // The timeline ends at the last note-off; a song padded to a longer End-of-Track (see MidiSong.DurationMs)
        // never adds a command past its last note, so equal-length stems would report different lengths. Clamp up to
        // the full song length in real-ms (song-ms / speed — same units as the timeline) so length-matching holds.
        int songEndReal = (int)Math.Round((_song.DurationMs) / speed);
        if (songEndReal > _b2DurationMs) _b2DurationMs = songEndReal;
    }

    private static double NextAfter(List<int> sorted, int t)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (sorted[mid] <= t) lo = mid + 1; else hi = mid; }
        return lo < sorted.Count ? sorted[lo] : -1.0;
    }

    private void OnUpdateB2(float dt)
    {
        _realMs = _clock.Elapsed.TotalMilliseconds + _b2ClockOffset;   // wall clock (+ seek offset) — exact position, hitch-immune

        // Apply a settled live transpose/hold/tempo change: rebuild the timeline and re-seek to the same position.
        if (_b2Dirty && Environment.TickCount64 - _b2DirtyMs >= B2RebuildDebounceMs)
        {
            _b2Dirty = false;
            RebuildAndReseek();
        }

        TickInstrumentEffect();   // B2: local-set tone/technique at play time + send timestamped records ~lookahead ahead

        // 1) Local audio: play every command now due, batched into one Lua call.
        _sb.Clear();
        int noteEmitted = 0;
        while (_playIdx < _timeline.Count && _timeline[_playIdx].RealMs <= _realMs)
        {
            var c = _timeline[_playIdx++];
            switch (c.Type)
            {
                case CmdType.Press:
                    if (!_muteLocal) { _sb.Append("svc:EntityInstrumentPlayNote(ent,").Append(c.Key).Append(",true) ");    _b2Held.Add(c.Key); }
                    if (_showKeyViz)  _sb.Append("__mvp(").Append(c.Key).Append(") ");
                    if (!_muteLocal || _showKeyViz) noteEmitted++;
                    _sentNotes++;
                    break;
                case CmdType.Release:
                    if (!_muteLocal) { _sb.Append("svc:EntityInstrumentReleaseNote(ent,").Append(c.Key).Append(",true) "); _b2Held.Remove(c.Key); }
                    if (_showKeyViz)  _sb.Append("__mvr(").Append(c.Key).Append(") ");
                    if (!_muteLocal || _showKeyViz) noteEmitted++;
                    break;
                case CmdType.PedalOn:  if (!_forceSustain) { FlushPedalB2(true);  _pedalDown = true;  } break;
                case CmdType.PedalOff: if (!_forceSustain) { FlushPedalB2(false); _pedalDown = false; } break;
            }
        }
        if (noteEmitted > 0) FlushLocalNotesB2();

        // 2) Network: send records up to LookaheadMs ahead, on a throttle (fewer, larger packets).
        _sendAccumMs += dt * 1000.0;
        if (_sendAccumMs >= _sendIntervalMs) { _sendAccumMs = 0; SendBatchB2(); }

        // End: timeline fully played and fully sent, nothing left ringing — AND the clock has reached the song's full
        // length (End-of-Track), so a song padded past its last note plays out to its real duration instead of ending
        // early on the last note.
        if (_playIdx >= _timeline.Count && _sendIdx >= _timeline.Count && _b2Held.Count == 0 && _realMs >= _b2DurationMs)
            EndReached();
    }

    private void FlushLocalNotesB2()
        => _callLua("pcall(function() local svc=Z.DIServiceMgr.InstrumentService local ent=Z.EntityMgr.PlayerEnt if svc==nil or ent==nil then return end " + _sb + "end)");

    // Local-only pedal set: applies the sustain pedal to the local instrument now. The NETWORK pedal is no longer sent
    // here — it rides the timestamped SustainPedal record in SendBatchB2 (pre-buffered, on-beat with the notes/tone).
    private void FlushPedalB2(bool on)
        => _callLua("pcall(function() local svc=Z.DIServiceMgr.InstrumentService local ent=Z.EntityMgr.PlayerEnt if svc~=nil and ent~=nil then svc:EntityInstrumentSetSustainPedal(ent, " + (on ? "true" : "false") + ", true) end end)");

    // B1 (non-net): drive the pedal through the game's own path (local set + networked settings sync to listeners).
    private void FlushPedalB1(bool on)
        => _callLua("pcall(function() local vm=Z.VMMgr.GetVM('band') local v=Z.UIMgr:GetView('band_performance_main_pc') local tok=(v and v.cancelSource or Z.DataMgr.Get('band_data').CancelSource):CreateToken() vm:HandleInstrumentPedal(" + (on ? "true" : "false") + ", tok) end)");

    // Apply the pedal for the current mode: B2 = local-only (network rides the sync records); B1 = local + network via the game path.
    private void ApplyPedal(bool on) { if (_netMode) FlushPedalB2(on); else FlushPedalB1(on); }

    // Send a single SustainPedal record to listeners at the CURRENT position (for a live mid-song force-sustain toggle).
    private void SendPedalImmediateB2(bool down)
        => _callLua("pcall(function() Z.CoroUtil.create_coro_xpcall(function() " +
            "local vm=Z.VMMgr.GetVM('band') local tok=Z.DataMgr.Get('band_data').CancelSource:CreateToken() " +
            "vm:AsyncSendInstrumentSyncData({{syncType=E.EInstrumentSyncType.SustainPedal,playTime=__b2base+" +
            (long)System.Math.Round(_realMs) + ",playParam=" + (down ? 1 : 0) +
            ",playType=E.EInstrumentPlayType.Press,expectedSyncTime=0}}, tok) end)() end)");

    private void SendBatchB2()
    {
        // Collect this tick's newly-due records (each advances _sendIdx once) and send them ONCE. Resending note-offs
        // was tried and rejected: a duplicate ReleaseNote is not a silent no-op in the game's audio engine — it
        // re-damps the note (audible flutter), even though it's idempotent in the note on/off logic. So every record
        // is sent exactly once. See Band-Instrument-Playback.md. Same-pitch restrike collisions are handled upstream
        // by the restrike gap in BuildTimeline, not here.
        _payload.Clear();
        // Force sustain: hold the pedal DOWN for the whole song — send exactly one SustainPedal down at song start
        // (playTime=__b2base+0), never an up, and skip the song's own pedal records below.
        if (_forceSustain && !_b2ForcePedalSent)
        {
            _b2ForcePedalSent = true;
            _payload.Append("{syncType=E.EInstrumentSyncType.SustainPedal,playTime=__b2base+0,playParam=1,playType=E.EInstrumentPlayType.Press,expectedSyncTime=0},");
        }
        while (_sendIdx < _timeline.Count && _timeline[_sendIdx].RealMs <= _realMs + _lookaheadMs)
        {
            var c = _timeline[_sendIdx++];
            if (c.Type == CmdType.PedalOn || c.Type == CmdType.PedalOff)
            {
                if (_forceSustain) continue;   // force-sustain: song's pedal events are skipped (one-shot down above covers it)
                // Pedal rides the same timestamped stream as the notes. Pedal is ALWAYS playType=Press; up/down is
                // carried by playParam (1=down for PedalOn, 0=up for PedalOff).
                _payload.Append("{syncType=E.EInstrumentSyncType.SustainPedal,playTime=__b2base+")
                        .Append((long)Math.Round(c.RealMs))
                        .Append(",playParam=").Append(c.Type == CmdType.PedalOn ? 1 : 0)
                        .Append(",playType=E.EInstrumentPlayType.Press,expectedSyncTime=0},");
                continue;
            }
            if (c.Type != CmdType.Press && c.Type != CmdType.Release) continue;
            _payload.Append("{syncType=E.EInstrumentSyncType.Note,playTime=__b2base+")
                    .Append((long)Math.Round(c.RealMs))
                    .Append(",playParam=").Append(c.Key)
                    .Append(",playType=E.EInstrumentPlayType.").Append(c.Type == CmdType.Press ? "Press" : "Release")
                    .Append(",expectedSyncTime=0},");
        }

        if (_payload.Length == 0) return;
        _callLua("pcall(function() Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM('band') local tok=Z.DataMgr.Get('band_data').CancelSource:CreateToken() vm:AsyncSendInstrumentSyncData({" + _payload + "}, tok) end)() end)");
    }

    private void ReleaseAllHeldB2()
    {
        if (_b2Held.Count > 0)
        {
            _sb.Clear();
            foreach (var key in _b2Held) _sb.Append("svc:EntityInstrumentReleaseNote(ent,").Append(key).Append(",true) ");
            FlushLocalNotesB2();
        }
        if (_pedalDown) { FlushPedalB2(false); _pedalDown = false; }
        _b2Held.Clear();
    }
}
