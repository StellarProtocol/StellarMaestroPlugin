using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Stellar.Maestro;

// DEBUG BUILDS ONLY — the csproj removes this file from non-Debug builds; MidiPlayer reaches it through partial methods
// declared in MidiPlayer.Net.cs (calls vanish in Release).
// DEV-ONLY note-accuracy diagnostics for the player (dormant unless the maestro-link-spike folder exists — the rows go
// through LinkSpikeDiagPatch.Row, which is a no-op until Plugin.LinkDiag.cs binds the diag-<name>.tsv file).
//   SEND\t{serverNow}\t{syncType}\t{playType}\t{playParam}\t{playTime}\t{localMs}  — every buffered (B2) record we send, AFTER the
//        plugin's own transforms (restrike gap, hold, tempo, pedal/force-sustain, tone lead). Same int encodings as the
//        RECV row: syncType Note=0/Technique=1/Tone=2/SustainPedal=3, playType Press=1/Release=2 (dump.cs 697783/697804,
//        pb_enum_define.lua:158/188). playTime is ABSOLUTE: __b2base (read back from Lua — it is re-anchored on
//        seek/resume) + the record's offset. Tone/Technique playParam is -1: timbre/kind resolve inside Lua (C# never
//        sees them).
//   END\t{serverNow}\t{reason}\t{localMs}  — a playing/paused song stopped: finished / stop / pause / switch.
// Every row ends with localMs = LinkSpikeDiagPatch.LocalMs (never-resyncing local Stopwatch, ms, 0.1 ms resolution).
public sealed partial class MidiPlayer
{
    private const int PlayPress = 1, PlayRelease = 2;   // (Sync* codes live in MidiPlayer.Net.cs — B2Item uses them)

    // Current __b2base, or 0 if unreadable. One Lua read — only ever called while the diag is on.
    private long DiagBase()
        => _services.Lua.TryReadGlobalNumber("__b2base", out double b) ? (long)b : 0;

    // SEND rows for one sorted B2 batch (called from SendBatchB2 just before the send).
    partial void DiagSendBatch(List<B2Item> batch)
    {
        if (!LinkSpikeDiagPatch.On) return;
        try
        {
            long now = _services.CombatSnapshot.ServerNowMs, b = DiagBase();
            var sb = new StringBuilder(batch.Count * 40);
            foreach (var it in batch)
            {
                if (it.Frag != null) AppendSend(sb, now, it.Sync, PlayPress + (it.Release ? 1 : 0), it.Param, b + it.PlayTime);
                else
                {
                    AppendSend(sb, now, SyncTone,      PlayPress, -1, b + it.PlayTime);
                    AppendSend(sb, now, SyncTechnique, PlayPress, -1, b + it.PlayTime);
                }
            }
            LinkSpikeDiagPatch.Row(sb.ToString(0, Math.Max(0, sb.Length - 1)));   // Row appends the final '\n'
        }
        catch { /* never break band audio */ }
    }

    // Out-of-batch sends (SendRecsB2: rebuild/stop closing releases, live force-sustain pedals; EmitNetSend: the
    // seek/resume tone one-shot) only exist as the Lua record literals WE generate, so parse those back. A non-numeric
    // playParam (timbre / kind — resolved in Lua) logs as -1.
    private static readonly Regex RecRx = new(
        @"syncType=E\.EInstrumentSyncType\.(\w+),playTime=__b2base\+(-?\d+),playParam=([^,]+),playType=E\.EInstrumentPlayType\.(\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    partial void DiagSendRecs(string recs)
    {
        if (!LinkSpikeDiagPatch.On) return;
        try
        {
            long now = _services.CombatSnapshot.ServerNowMs, b = DiagBase();
            var sb = new StringBuilder();
            foreach (Match m in RecRx.Matches(recs))
            {
                int sync = m.Groups[1].Value switch { "Note" => SyncNote, "Technique" => SyncTechnique, "Tone" => SyncTone, "SustainPedal" => SyncPedal, _ => -1 };
                int pt   = m.Groups[4].Value == "Release" ? PlayRelease : PlayPress;
                int prm  = int.TryParse(m.Groups[3].Value, out var p) ? p : -1;
                AppendSend(sb, now, sync, pt, prm, b + long.Parse(m.Groups[2].Value));
            }
            if (sb.Length > 0) LinkSpikeDiagPatch.Row(sb.ToString(0, sb.Length - 1));
        }
        catch { }
    }

    private static void AppendSend(StringBuilder sb, long now, int sync, int playType, int param, long playTime)
        => sb.Append("SEND\t").Append(now).Append('\t').Append(sync).Append('\t').Append(playType).Append('\t')
             .Append(param).Append('\t').Append(playTime).Append('\t').Append(LinkSpikeDiagPatch.LocalMs).Append('\n');

    // END row — only when a song was actually playing or paused (Start/Load call Stop() on an idle player too).
    partial void DiagEnd(string reason)
    {
        if (!LinkSpikeDiagPatch.On || (!_playing && !_paused)) return;
        try { LinkSpikeDiagPatch.Row($"END\t{_services.CombatSnapshot.ServerNowMs}\t{reason}\t{LinkSpikeDiagPatch.LocalMs}"); } catch { }
    }

    // The base the network timeline is anchored to: __b2base in buffered mode; 0 in B1 (no shared base — the game
    // stamps each note's playTime at the press moment).
    public long DiagSongBaseMs() => _netMode ? DiagBase() : 0;
}
