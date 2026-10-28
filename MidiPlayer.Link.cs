using System;
using System.Collections.Generic;

namespace Stellar.Maestro;

// Maestro Link: play a part pinned to a room-wide server-time start T (KB Band-Instrument-Playback.md → "Band sync
// WITHOUT the game ensemble"). Every network record is stamped playTime = T + songPosMs, so ANY change made while
// linked must keep __b2base == T — the normal live paths (AnchorClock on seek/resume/slider rebuild) re-anchor the
// base to the LOCAL server clock and would silently desync this client from the band. KeepBase routes those through
// the keep-base variants below instead.
//
// Two extra mechanisms, both only active after a late join / keep-base rebuild:
//   • Floors (_playFloorMs/_sendFloorMs): song positions already "decided" — nothing at or before them is ever played
//     locally or sent. A late join at position P sets both floors just below P.
//   • Orphan-release skips (_skipRelLocal/_skipRelSend): a note that started before the floor was never pressed, so its
//     release (which lies after the floor) must not be played/sent either — a stray network Release could gate a
//     different voice of the same pitch on listeners (they STACK same-pitch voices, see "Remote drop-outs").
public sealed partial class MidiPlayer
{
    public bool KeepBase;   // linked: never re-anchor __b2base; live rebuilds keep T (set by the plugin while linked)

    private readonly Dictionary<int, int> _skipRelLocal = new();   // key → releases to swallow on the local cursor
    private readonly Dictionary<int, int> _skipRelSend  = new();   // key → releases to swallow on the send cursor
    private readonly Dictionary<int, int> _netOpen      = new();   // key → presses sent minus releases sent (network-open notes)
    private double _playFloorMs = -1, _sendFloorMs = -1;           // positions already decided (see header); -1 = none
    private double _sendHorizonMs = -1;                            // every command with RealMs <= this has been sent/decided

    private void ResetLinkCursors()
    {
        _skipRelLocal.Clear(); _skipRelSend.Clear(); _netOpen.Clear();
        _playFloorMs = -1; _sendFloorMs = -1; _sendHorizonMs = -1;
    }

    private static bool ConsumeSkip(Dictionary<int, int> d, int key)
    {
        if (!d.TryGetValue(key, out int n) || n <= 0) return false;
        if (n == 1) d.Remove(key); else d[key] = n - 1;
        return true;
    }

    // Called by SendBatchB2 for every note record it actually sends, and once per batch with the new horizon.
    private void TrackNetSent(int key, bool press)
    {
        _netOpen.TryGetValue(key, out int n);
        n += press ? 1 : -1;
        if (n <= 0) _netOpen.Remove(key); else _netOpen[key] = n;
    }

    // Start a linked part: base = T (server ms), local clock = serverNow − T (negative = count-in). startPosMs > 0 =
    // LATE JOIN: everything before startPosMs is neither played nor sent, local audio begins at startPosMs, base stays T.
    public void StartLinked(long baseMs, long serverNowMs, int startPosMs, int transpose)
    {
        if (_song == null) return;
        EnsembleBaseMs      = baseMs;
        EnsembleClockOffset = serverNowMs - baseMs;
        EnsembleGrid        = true;
        _netMode            = true;
        _startAtFrac        = -1;   // a stale "seek while stopped" marker would RepositionTo → AnchorClock
        Start(transpose);           // ResetB2 pins __b2base = T and _b2ClockOffset = serverNow − T
        if (startPosMs <= 0) return;

        _realMs = _clock.Elapsed.TotalMilliseconds + _b2ClockOffset;
        SkipToKeepBase(startPosMs);
        SeekInstrumentEffect();     // tone/technique active at the CURRENT position (Start resolved it for position 0)
    }

    // Move both cursors to the first command at/after posMs without touching the base, and arm orphan-release skips for
    // notes that are still open at posMs. Also restores a song sustain pedal that is down at posMs.
    private void SkipToKeepBase(int posMs)
    {
        double floor = posMs - 1e-6;
        int idx = 0;
        var open = new Dictionary<int, int>();
        bool pedal = false; int presses = 0;
        while (idx < _timeline.Count && _timeline[idx].RealMs <= floor)
        {
            var c = _timeline[idx++];
            switch (c.Type)
            {
                case CmdType.Press:   open.TryGetValue(c.Key, out int a); open[c.Key] = a + 1; presses++; break;
                case CmdType.Release: if (open.TryGetValue(c.Key, out int r)) { if (r <= 1) open.Remove(c.Key); else open[c.Key] = r - 1; } break;
                case CmdType.PedalOn:  pedal = true;  break;
                case CmdType.PedalOff: pedal = false; break;
            }
        }
        _playIdx = _sendIdx = idx;
        _sentNotes = presses;
        _b2Held.Clear(); _netOpen.Clear();
        _skipRelLocal.Clear(); _skipRelSend.Clear();
        foreach (var kv in open) { _skipRelLocal[kv.Key] = kv.Value; _skipRelSend[kv.Key] = kv.Value; }
        _playFloorMs = _sendFloorMs = floor;
        _sendHorizonMs = Math.Max(_sendHorizonMs, floor);
        _b2ForcePedalAtMs = posMs;   // force sustain's one-shot DOWN goes at the start position, never in the past

        if (pedal && !_forceSustain)
        {
            _pedalDown = true;
            FlushPedalB2(true);
            SendPedalAtB2(true, posMs);
        }
    }

    // Keep-base live rebuild (transpose / hold / poly / restrike changed while linked). Switches over at two boundaries:
    //   local   at L = max(current position, play floor)  — what we already played stays played;
    //   network at H = max(send horizon, send floor)       — what we already SENT (≈ lookahead ahead) stays sent, so
    //   remotes hear the change ~lookahead later, still stamped against the same T.
    // At each boundary the old and new "open note" multisets are reconciled per key: notes open in both carry over (the
    // new timeline's release ends them), old-only notes are released AT the boundary, new-only notes (never pressed)
    // get their release swallowed.
    // Pedal: nothing to reconcile — pedal events depend only on song time and tempo (fixed 100 % while linked), not on
    // transpose / hold / poly / restrike, so the old and new timelines agree on the pedal at H; force sustain doesn't
    // rebuild (its toggle is LiveForceSustainB2).
    private void RebuildKeepBase()
    {
        BuildTimeline();
        double L = Math.Max(_realMs, _playFloorMs);
        double H = Math.Max(_sendHorizonMs, _sendFloorMs);

        // Local boundary.
        var newLocal = OpenAt(L, out _playIdx, out int presses);
        _sentNotes = presses;
        _skipRelLocal.Clear();
        _sb.Clear();
        var heldNow = new List<int>(_b2Held);
        foreach (int k in heldNow)
            if (!newLocal.ContainsKey(k)) { _sb.Append("svc:EntityInstrumentReleaseNote(ent,").Append(k).Append(",true) "); _b2Held.Remove(k); }
        foreach (var kv in newLocal)
        {
            int carry = _b2Held.Contains(kv.Key) ? 1 : 0;   // _b2Held is a set — at most one local voice per key
            if (kv.Value > carry) _skipRelLocal[kv.Key] = kv.Value - carry;
        }
        if (_sb.Length > 0) FlushLocalNotesB2();

        // Network boundary.
        var newNet = OpenAt(H, out _sendIdx, out _);
        _skipRelSend.Clear();
        long hStamp = (long)Math.Round(Math.Max(0, H));
        _payload.Clear();
        foreach (var kv in new List<KeyValuePair<int, int>>(_netOpen))
        {
            newNet.TryGetValue(kv.Key, out int nw);
            int carry = Math.Min(kv.Value, nw);
            for (int i = carry; i < kv.Value; i++)
            {
                _payload.Append("recs[#recs+1]={syncType=E.EInstrumentSyncType.Note,playTime=__b2base+").Append(hStamp)
                        .Append(",playParam=").Append(kv.Key).Append(",playType=E.EInstrumentPlayType.Release,expectedSyncTime=0} ");
                TrackNetSent(kv.Key, press: false);
            }
        }
        foreach (var kv in newNet)
        {
            _netOpen.TryGetValue(kv.Key, out int old);
            int carry = Math.Min(old, kv.Value);
            if (kv.Value > carry) _skipRelSend[kv.Key] = kv.Value - carry;
        }
        if (_payload.Length > 0) SendRecsB2(_payload.ToString());
        if (_forceSustain) { ApplyPedal(true); _pedalDown = true; }
    }

    // Open-note multiset of the CURRENT timeline at position `at` (inclusive) + the first index after it + press count.
    private Dictionary<int, int> OpenAt(double at, out int firstAfter, out int presses)
    {
        var open = new Dictionary<int, int>();
        int i = 0; presses = 0;
        while (i < _timeline.Count && _timeline[i].RealMs <= at)
        {
            var c = _timeline[i++];
            if (c.Type == CmdType.Press) { open.TryGetValue(c.Key, out int a); open[c.Key] = a + 1; presses++; }
            else if (c.Type == CmdType.Release && open.TryGetValue(c.Key, out int r)) { if (r <= 1) open.Remove(c.Key); else open[c.Key] = r - 1; }
        }
        firstAfter = i;
        return open;
    }

    // Linked "stop my part" / re-equip: stop locally AND release every note listeners still hold. A plain Stop only
    // silences the local instrument — releases past the send horizon were never sent, so on listeners those notes
    // would ring until the game cleans them up. Stamped at the horizon (after every press we sent).
    // Also lifts the NETWORK sustain pedal (song pedal or force sustain, as last SENT — _netPedalDown, not the local
    // _pedalDown): otherwise listeners keep it down, the released notes ring on, and our next part starts pedal-down.
    // The pedal UP is stamped hStamp+1 so it lands AFTER the note releases (B2Rank orders Pedal before Note at an equal
    // playTime, and we don't rely on how the receiver breaks ties).
    public void StopLinked()
    {
        if (_playing && _netMode && (_netOpen.Count > 0 || _netPedalDown))
        {
            long hStamp = (long)Math.Round(Math.Max(0, Math.Max(_sendHorizonMs, _sendFloorMs)));
            _payload.Clear();
            foreach (var kv in _netOpen)
                for (int i = 0; i < kv.Value; i++)
                    _payload.Append("recs[#recs+1]={syncType=E.EInstrumentSyncType.Note,playTime=__b2base+").Append(hStamp)
                            .Append(",playParam=").Append(kv.Key).Append(",playType=E.EInstrumentPlayType.Release,expectedSyncTime=0} ");
            if (_netPedalDown) _payload.Append(PedalRec(false, hStamp + 1));
            SendRecsB2(_payload.ToString());
        }
        _netOpen.Clear();
        _netPedalDown = false;
        StopReset();
    }

    // Song sustain-pedal state at song position `pos` (inclusive), from the current timeline.
    private bool SongPedalAt(double pos)
    {
        bool down = false;
        for (int i = 0; i < _timeline.Count && _timeline[i].RealMs <= pos; i++)
        {
            var t = _timeline[i].Type;
            if (t == CmdType.PedalOn) down = true; else if (t == CmdType.PedalOff) down = false;
        }
        return down;
    }

    // Live force-sustain toggle in buffered mode (was: a blind down/up at the current position).
    //   ON  → pedal down now, locally and for listeners; and again just after the send horizon, because song pedal
    //         records up to the horizon were already sent and a later song pedal-UP among them would otherwise cancel it.
    //   OFF → the SONG's pedal state at the current position (down if the timeline has it down there), locally and for
    //         listeners — then re-send the song pedal records in (position, horizon] that were skipped while forced.
    private void LiveForceSustainB2(bool on)
    {
        _b2ForcePedalSent = true;   // the SendBatchB2 start-down one-shot is covered by this live send
        double pos = Math.Max(Math.Max(0, _realMs), _playFloorMs);
        double h   = Math.Max(_sendHorizonMs, _sendFloorMs);
        long   pt  = (long)Math.Round(pos);
        _payload.Clear();
        if (on)
        {
            _pedalDown = true;
            FlushPedalB2(true);
            _payload.Append(PedalRec(true, pt));
            if (h > pos) _payload.Append(PedalRec(true, (long)Math.Round(h) + 1));
            _netPedalDown = true;
        }
        else
        {
            bool down = SongPedalAt(pos);
            _pedalDown = down;
            FlushPedalB2(down);
            _payload.Append(PedalRec(down, pt));
            _netPedalDown = down;
            for (int i = 0; i < _sendIdx && i < _timeline.Count; i++)
            {
                var c = _timeline[i];
                if (c.RealMs <= pos || (c.Type != CmdType.PedalOn && c.Type != CmdType.PedalOff)) continue;
                _payload.Append(PedalRec(c.Type == CmdType.PedalOn, (long)Math.Round(c.RealMs)));
                _netPedalDown = c.Type == CmdType.PedalOn;
            }
        }
        SendRecsB2(_payload.ToString());
    }

    private static string PedalRec(bool down, long pt)
        => "recs[#recs+1]={syncType=E.EInstrumentSyncType.SustainPedal,playTime=__b2base+" + pt +
           ",playParam=" + (down ? 1 : 0) + ",playType=E.EInstrumentPlayType.Press,expectedSyncTime=0} ";

    private void SendPedalAtB2(bool down, int posMs) { _netPedalDown = down; SendRecsB2(PedalRec(down, posMs)); }

    private void SendRecsB2(string recs)
        => _services.Lua.DoString(
            "pcall(function() local vm=Z.VMMgr.GetVM('band') local tok=Z.DataMgr.Get('band_data').CancelSource:CreateToken() local recs={} " +
            recs + "if #recs>0 then Z.CoroUtil.create_coro_xpcall(function() vm:AsyncSendInstrumentSyncData(recs, tok) end)() end end)");
}
