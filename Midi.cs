using System;
using System.Collections.Generic;

namespace Stellar.Maestro;

// Standard MIDI File (SMF) parser for the auto-player.
//
// Turns a .mid file (format 0 or 1) into a flat, time-sorted list of note on/off events in absolute
// milliseconds, honouring a full tempo map (multiple set-tempo meta events) and SMPTE division.
// Pure C#, no game dependencies.
//
// Note numbers are standard MIDI (0-127); they map 1:1 to the game's keyCount. Range-fitting to a
// summoned instrument's MusicalInstrumentsRange happens later at playback time, not here.

public readonly struct MidiNoteEvent
{
    public readonly int  TimeMs;   // absolute time from song start
    public readonly byte Note;     // 0-127 (unused for pedal events)
    public readonly byte Velocity; // 0-127 (0 for note-off)
    public readonly bool On;       // note-on / note-off — OR pedal-down / pedal-up when Pedal is true
    public readonly bool Pedal;    // true = sustain-pedal (CC64) event; Note/Velocity ignored

    public MidiNoteEvent(int timeMs, byte note, byte velocity, bool on, bool pedal = false)
    {
        TimeMs = timeMs; Note = note; Velocity = velocity; On = on; Pedal = pedal;
    }
}

public sealed class MidiSong
{
    public string Name = "";
    public double Bpm;          // base tempo (quarter-notes/min); 0 = unknown (SMPTE) — for ensemble tempo-scaling
    public int DurationMs;
    public int NoteCount;
    public int PedalCount;
    public byte MinNote = 127;
    public byte MaxNote;
    public int  Program = -1;   // dominant non-drum channel's GM program (-1 = none/unknown/drums) — for tone/technique mapping
    public readonly List<(int ms, int program)> ProgramChanges = new(); // program-change timeline on the dominant channel (dynamic tone/technique)
    public readonly List<MidiNoteEvent> Events = new(); // notes + pedal events, time-sorted
}

public static class MidiParser
{
    public static MidiSong? TryParseFile(string path, out string? error)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            var name  = System.IO.Path.GetFileNameWithoutExtension(path);
            var song  = Parse(bytes, name);
            error = null;
            return song;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // A tempo change: at absolute tick `Tick`, microseconds-per-quarter-note becomes `UsPerQn`.
    private readonly struct TempoChange
    {
        public readonly long Tick;
        public readonly int  UsPerQn;
        public TempoChange(long tick, int usPerQn) { Tick = tick; UsPerQn = usPerQn; }
    }

    // A raw note event with absolute ticks (converted to ms in a second pass).
    private readonly struct RawNote
    {
        public readonly long Tick;
        public readonly byte Note;
        public readonly byte Velocity;
        public readonly bool On;
        public RawNote(long tick, byte note, byte vel, bool on) { Tick = tick; Note = note; Velocity = vel; On = on; }
    }

    // A raw sustain-pedal (CC64) event with absolute ticks.
    private readonly struct RawPedal
    {
        public readonly long Tick;
        public readonly bool Down;
        public RawPedal(long tick, bool down) { Tick = tick; Down = down; }
    }

    public static MidiSong Parse(byte[] data, string name)
    {
        int p = 0;
        if (data.Length < 14 || data[0] != 'M' || data[1] != 'T' || data[2] != 'h' || data[3] != 'd')
            throw new FormatException("not a MIDI file (missing MThd)");
        p = 4;
        int headerLen = ReadU32(data, ref p);
        int headerEnd = p + headerLen;
        int format    = ReadU16(data, ref p);
        int ntrks     = ReadU16(data, ref p);
        int division  = ReadU16(data, ref p);
        p = headerEnd; // skip any extra header bytes
        if (format != 0 && format != 1)
            throw new FormatException($"unsupported MIDI format {format} (only 0 and 1)");

        // Division: bit15=0 → ticks-per-quarter-note (tempo-based). bit15=1 → SMPTE (tempo-independent).
        bool  smpte      = (division & 0x8000) != 0;
        int   tpqn       = smpte ? 0 : (division & 0x7FFF);
        double smpteMsPerTick = 0;
        if (smpte)
        {
            int fps          = -(sbyte)(division >> 8);   // 24/25/29/30
            int ticksPerFrame = division & 0xFF;
            if (fps <= 0 || ticksPerFrame <= 0) throw new FormatException("bad SMPTE division");
            smpteMsPerTick = 1000.0 / (fps * ticksPerFrame);
        }
        else if (tpqn <= 0) throw new FormatException("bad tick division");

        var rawNotes  = new List<RawNote>();
        var rawPedals = new List<RawPedal>();
        var tempos    = new List<TempoChange>();
        long lastTick = 0;   // max absolute tick of ANY event across all tracks (incl. End-of-Track + lone note-offs)
        var progByChan  = new int[16]; for (int i = 0; i < 16; i++) progByChan[i] = -1;  // last program-change per channel
        var notesByChan = new int[16];                                                    // note-on count per channel
        var rawProgs    = new List<(long tick, int chan, int program)>();                 // full program-change timeline

        for (int t = 0; t < ntrks; t++)
        {
            if (p + 8 > data.Length) break;
            if (data[p] != 'M' || data[p + 1] != 'T' || data[p + 2] != 'r' || data[p + 3] != 'k')
                throw new FormatException($"track {t}: missing MTrk");
            p += 4;
            int trkLen = ReadU32(data, ref p);
            int trkEnd = p + trkLen;
            long tick = 0;
            byte runningStatus = 0;

            while (p < trkEnd)
            {
                tick += ReadVlq(data, ref p);
                if (tick > lastTick) lastTick = tick;   // track song end even for events dropped by the note merge (pad-to-length note-offs, EoT)
                byte status = data[p];
                if (status >= 0x80) { runningStatus = status; p++; }
                else status = runningStatus; // running status: reuse previous, data byte stays

                if (status == 0xFF) // meta event
                {
                    byte metaType = data[p++];
                    int  metaLen  = ReadVlq(data, ref p);
                    if (metaType == 0x51 && metaLen == 3) // set tempo
                    {
                        int us = (data[p] << 16) | (data[p + 1] << 8) | data[p + 2];
                        tempos.Add(new TempoChange(tick, us));
                    }
                    p += metaLen;
                }
                else if (status == 0xF0 || status == 0xF7) // sysex — skip
                {
                    int len = ReadVlq(data, ref p);
                    p += len;
                }
                else
                {
                    int hi = status & 0xF0;
                    switch (hi)
                    {
                        case 0x90: // note on
                        {
                            byte note = data[p++]; byte vel = data[p++];
                            rawNotes.Add(new RawNote(tick, note, vel, vel > 0)); // vel 0 = note-off
                            if (vel > 0) notesByChan[status & 0x0F]++;
                            break;
                        }
                        case 0x80: // note off
                        {
                            byte note = data[p++]; byte vel = data[p++];
                            rawNotes.Add(new RawNote(tick, note, vel, false));
                            break;
                        }
                        case 0xB0: // control change — capture CC64 (sustain pedal), skip the rest
                        {
                            byte cc = data[p++]; byte val = data[p++];
                            if (cc == 64) rawPedals.Add(new RawPedal(tick, val >= 64)); // >=64 = pedal down (MIDI convention)
                            break;
                        }
                        case 0xC0: progByChan[status & 0x0F] = data[p]; rawProgs.Add((tick, status & 0x0F, data[p])); p += 1; break;  // program change — instrument + timeline
                        case 0xA0: case 0xE0: p += 2; break;            // 2-byte channel msgs
                        case 0xD0: p += 1; break;                        // channel aftertouch (1-byte)
                        default: p = trkEnd; break;                     // unknown — bail this track
                    }
                }
            }
            p = trkEnd;
        }

        // Build the tempo timeline and convert ticks → ms.
        tempos.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        rawNotes.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.On.CompareTo(b.On)); // offs before ons at a tick

        // Merge overlapping same-pitch notes (a single key can't sound twice at once): many MIDIs double the melody
        // across tracks, producing two note-ons for the same pitch at the same instant. Reference-count per pitch so
        // an on emits only when the pitch was silent, and an off emits only when the last overlapping copy ends —
        // this turns unison duplicates into one sustained note (killing the double-strike), while genuine repeats
        // (which have an intervening note-off → count returns to 0) are preserved.
        var merged  = new List<RawNote>(rawNotes.Count);
        var onCount = new int[128];
        foreach (var rn in rawNotes)
        {
            int pitch = rn.Note & 0x7F;
            if (rn.On)
            {
                if (onCount[pitch] == 0) merged.Add(rn);
                onCount[pitch]++;
            }
            else if (onCount[pitch] > 0)
            {
                onCount[pitch]--;
                if (onCount[pitch] == 0) merged.Add(rn);
            }
        }

        var song = new MidiSong { Name = name };
        if (!smpte)   // base tempo = the earliest tempo event (default 120 BPM if none)
        {
            int  baseUs   = 500000;
            long bestTick = long.MaxValue;
            foreach (var tc in tempos) if (tc.Tick < bestTick) { bestTick = tc.Tick; baseUs = tc.UsPerQn; }
            song.Bpm = baseUs > 0 ? 60000000.0 / baseUs : 0;
        }
        foreach (var rn in merged)
        {
            int ms = smpte
                ? (int)Math.Round(rn.Tick * smpteMsPerTick)
                : TickToMs(rn.Tick, tempos, tpqn);
            song.Events.Add(new MidiNoteEvent(ms, rn.Note, rn.Velocity, rn.On));
            if (rn.On)
            {
                song.NoteCount++;
                if (rn.Note < song.MinNote) song.MinNote = rn.Note;
                if (rn.Note > song.MaxNote) song.MaxNote = rn.Note;
            }
            if (ms > song.DurationMs) song.DurationMs = ms;
        }
        foreach (var rp in rawPedals)
        {
            int ms = smpte
                ? (int)Math.Round(rp.Tick * smpteMsPerTick)
                : TickToMs(rp.Tick, tempos, tpqn);
            song.Events.Add(new MidiNoteEvent(ms, 0, 0, rp.Down, pedal: true));
            song.PedalCount++;
            if (ms > song.DurationMs) song.DurationMs = ms;
        }
        // A MIDI's true length is its last event (End-of-Track), not its last sounding note. Clamp up to it so
        // stems padded to equal length (a trailing lone note-off at SONG_END, dropped by the merge above) report
        // the same DurationMs — otherwise each stem ends at its last real note and length-matching breaks.
        int endMs = smpte ? (int)Math.Round(lastTick * smpteMsPerTick) : TickToMs(lastTick, tempos, tpqn);
        if (endMs > song.DurationMs) song.DurationMs = endMs;
        // Stable sort by time so simultaneous on/off keep a deterministic order (off before on).
        song.Events.Sort((a, b) =>
        {
            int c = a.TimeMs.CompareTo(b.TimeMs);
            return c != 0 ? c : a.On.CompareTo(b.On); // false(off) < true(on)
        });
        if (song.NoteCount == 0) song.MinNote = 0;

        // Dominant channel instrument: the non-drum channel with the most notes → its GM program (the arranger's intended
        // patch). -1 if no channel had an explicit program change (→ treated as Clean/Sustained by the tone mapping).
        int bestChan = -1, bestNotes = 0;
        for (int c = 0; c < 16; c++)
            if (c != 9 && notesByChan[c] > bestNotes) { bestNotes = notesByChan[c]; bestChan = c; }
        song.Program = bestChan >= 0 ? progByChan[bestChan] : -1;

        // Program-change timeline on the dominant channel → ms (for dynamic tone/technique switching).
        foreach (var (t, ch, pr) in rawProgs)
            if (ch == bestChan)
                song.ProgramChanges.Add((smpte ? (int)Math.Round(t * smpteMsPerTick) : TickToMs(t, tempos, tpqn), pr));
        song.ProgramChanges.Sort((a, b) => a.ms.CompareTo(b.ms));

        return song;
    }

    // Convert absolute ticks to ms across a piecewise-constant tempo map (default 120 BPM until first change).
    private static int TickToMs(long tick, List<TempoChange> tempos, int tpqn)
    {
        double ms = 0;
        long   prevTick = 0;
        int    curUs = 500000; // 120 BPM default
        foreach (var tc in tempos)
        {
            if (tc.Tick >= tick) break;
            ms += (tc.Tick - prevTick) * (curUs / 1000.0) / tpqn;
            prevTick = tc.Tick;
            curUs = tc.UsPerQn;
        }
        ms += (tick - prevTick) * (curUs / 1000.0) / tpqn;
        return (int)Math.Round(ms);
    }

    private static int ReadU16(byte[] d, ref int p) { int v = (d[p] << 8) | d[p + 1]; p += 2; return v; }
    private static int ReadU32(byte[] d, ref int p) { int v = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; p += 4; return v; }

    // Variable-length quantity (7 bits per byte, high bit = continuation).
    private static int ReadVlq(byte[] d, ref int p)
    {
        int v = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = d[p++];
            v = (v << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return v;
    }

}
