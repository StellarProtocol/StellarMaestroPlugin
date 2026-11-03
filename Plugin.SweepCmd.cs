using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Stellar.Maestro;

// DEBUG BUILDS ONLY (the csproj removes this file from non-Debug builds: C:\Users\Public is world-writable, so a release
// must not carry a hidden local control channel). Called via partial methods declared in Plugin.cs.
// DEV-ONLY, DORMANT — remote control + run markers for Claude's automated note-accuracy sweep (one client, measuring
// the server's echo of our own notes: SEND rows = what we sent, PLAY/REL rows = what came back). Runs ONLY while the
// Plugin.LinkDiag.cs diag is active (C:\Users\Public\maestro-link-spike\ exists); without that folder none of this
// executes. Not a user feature: no UI, no user-facing strings (a localized plugin — Rule 10 — so keep it that way).
// The 2026-10-28 spike's cmd.json poller was removed on purpose (Maestro Link replaced it); this one exists for the sweep.
//
// cmd.txt (polled ~500 ms; read → deleted → executed; write it as a temp file + rename so a half-written file is never
// read). One command per line, blank lines and '#' comments ignored:
//   set <key> <value>   net_ahead_ms | net_batch_ms | restrike_gap_ms | hold_ms | max_poly | tempo_pct | tone_lead_ms
//                       (ints), net_prebuffer | force_sustain | mute_local | auto_advance (1/0/true/false/on/off),
//                       loop_mode (0 none/1 all/2 one), transpose (int, not persisted — same as the UI) — through the
//                       SAME setters the UI uses, so the live player and the persisted config both update.
//   play <absolute .mid path>   queue it in the active playlist the way the Library does (file name if it lives in midi/,
//                       else the absolute path; an existing entry is reused) and play it via PlayQueueIndex — the UI path.
//                       Needs the free-play instrument view open (the plain path never equips; Link does).
//   stop                same as the UI Stop button.
//   status              playing/paused/linked, song, position/duration ms, live CFG.
// cmd.out.txt (overwritten per poll): one line per command, `OK <cmd>[ <detail>]` or `ERR <cmd>: <reason>`.
// TSV markers: CMD\t{serverNow}\t{line} before each command; START/CFG at plain-playback start (see LinkDiagPlainStart).
public sealed partial class Plugin
{
    private string CmdPath    => Path.Combine(LinkDiagDir, "cmd.txt");
    private string CmdOutPath => Path.Combine(LinkDiagDir, "cmd.out.txt");
    private double _sweepPollAccumMs;

    private void SweepCmdTick(float dt)
    {
        _sweepPollAccumMs += dt * 1000.0;
        if (_sweepPollAccumMs < 500) return;
        _sweepPollAccumMs = 0;
        if (!File.Exists(CmdPath)) return;

        string text;
        try { text = File.ReadAllText(CmdPath); File.Delete(CmdPath); }
        catch (IOException) { return; }   // writer still has it open — retry next poll

        var outp = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            LinkDiagRow($"CMD\t{_services.CombatSnapshot.ServerNowMs}\t{line}\t{LinkSpikeDiagPatch.LocalMs}");
            string? detail = null, err;
            try { err = RunSweepCmd(line, out detail); }
            catch (Exception ex) { err = ex.Message; }
            outp.Append(err == null ? $"OK {line}{(detail != null ? " " + detail : "")}" : $"ERR {line}: {err}").Append('\n');
        }
        try
        {
            var tmp = CmdOutPath + ".tmp";
            File.WriteAllText(tmp, outp.ToString());
            File.Move(tmp, CmdOutPath, overwrite: true);   // the reader never sees a half-written result
        }
        catch (Exception ex) { _services.Log.Warning($"[MaestroSweep] cmd.out write failed: {ex.Message}"); }
        _services.Log.Info($"[MaestroSweep] ran cmd.txt:\n{outp}");
    }

    // null = OK; otherwise the error reason.
    private string? RunSweepCmd(string line, out string? detail)
    {
        detail = null;
        var sp = line.IndexOf(' ');
        string verb = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
        string rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
        switch (verb)
        {
            case "set":    return SweepSet(rest);
            case "play":   return SweepPlay(rest.Trim('"'));
            case "stop":   StopPlayback(); return null;
            case "status": detail = SweepStatus(); return null;
            default:       return "unknown command";
        }
    }

    private string? SweepSet(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return "usage: set <key> <value>";
        string key = parts[0].ToLowerInvariant(), val = parts[1];
        bool isInt = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
        bool? b = val.ToLowerInvariant() switch { "1" or "true" or "on" => true, "0" or "false" or "off" => false, _ => null };
        Action? apply = key switch
        {
            "net_ahead_ms"    when isInt => () => SetAhead(n),
            "net_batch_ms"    when isInt => () => SetBatch(n),
            "tone_lead_ms"    when isInt => () => SetToneLead(n),
            "restrike_gap_ms" when isInt => () => SetRestrikeGap(n),
            "hold_ms"         when isInt => () => SetHold(n),
            "max_poly"        when isInt => () => SetMaxPoly(n),
            "tempo_pct"       when isInt => () => SetTempo(n),
            "transpose"       when isInt => () => SetTranspose(n),
            "loop_mode"       when isInt && n is >= 0 and <= 2 => () => { _loopMode = n; _cfg.Set<int>("loop_mode", n); _cfg.Save(); },
            "net_prebuffer"   when b != null => () => SetNetPrebuffer(b.Value),
            "force_sustain"   when b != null => () => SetForceSustain(b.Value),
            "mute_local"      when b != null => () => SetMuteLocal(b.Value),
            "auto_advance"    when b != null => () => SetAutoAdvance(b.Value),
            _ => null,
        };
        if (apply == null) return $"unknown key or bad value '{val}'";
        apply();   // setters clamp — report what actually took effect via `status`
        return null;
    }

    private string? SweepPlay(string path)
    {
        if (_lkPhase != LinkPhase.Off) return "Maestro Link is active — leave the room first (the room owns the player)";
        if (!Path.IsPathRooted(path) || !File.Exists(path)) return "file not found (absolute path required)";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".mid" && ext != ".midi") return "not a .mid/.midi file";
        if (!IsBandViewOpen()) return "free-play instrument view not open (summon an instrument first)";

        // Same entry shape as the Library (a bare file name resolved against midi/); outside midi/ the absolute path
        // works too — PlayQueueIndex's Path.Combine(midiDir, rooted) yields the rooted path. Reuse an existing entry so
        // a long sweep doesn't grow the persisted playlist.
        string full = Path.GetFullPath(path);
        string dir  = Path.GetFullPath(BandMidiDir()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string entry = string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : full;
        int idx = Active.Songs.FindIndex(s => string.Equals(s, entry, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { AddToActive(entry); idx = Active.Songs.Count - 1; }

        PlayQueueIndex(idx);
        if (_ensembleWaiting) return null;   // armed for ensemble sync — starts when the ensemble goes live (UI behaviour)
        return _bandPlayer.IsPlaying ? null : $"did not start: {_bandMidiStatus}";
    }

    private string SweepStatus()
    {
        var songs = Active.Songs;
        string song = _nowPlaying >= 0 && _nowPlaying < songs.Count ? Path.GetFileName(songs[_nowPlaying]) : "";
        return $"playing={(_bandPlayer.IsPlaying ? 1 : 0)} paused={(_bandPlayer.IsPaused ? 1 : 0)} linked={(_lkPhase != LinkPhase.Off ? 1 : 0)} " +
               $"ensemble_wait={(_ensembleWaiting ? 1 : 0)} song={song} pos_ms={_bandPlayer.PositionMs} dur_ms={_bandPlayer.DurationMs} " +
               $"sent_notes={_bandPlayer.SentNotes} cfg={SweepCfg()}";
    }

    // The LIVE values the player is using (read back from MidiPlayer, not the config). net_prebuffer is the player's
    // current mode — a UI/`set` change applies at the next Play, exactly like the toggle.
    private string SweepCfg()
    {
        var p = _bandPlayer;
        return $"net_ahead_ms={p.NetLookaheadMs};net_batch_ms={p.NetBatchMs};restrike_gap_ms={p.RestrikeGapMs};" +
               $"hold_ms={p.HoldOffsetMs};max_poly={p.MaxPolyphony};net_prebuffer={(p.PreBufferedNet ? 1 : 0)};" +
               $"force_sustain={(p.ForceSustain ? 1 : 0)};mute_local={(p.MuteLocal ? 1 : 0)};tempo_pct={p.TempoPct};" +
               $"tone_lead_ms={p.ToneLeadMs};transpose={p.Transpose};";
    }

    private void LinkDiagCfgRow(long now)
    {
        if (LinkSpikeDiagPatch.On) LinkDiagRow($"CFG\t{now}\t{SweepCfg()}\t{LinkSpikeDiagPatch.LocalMs}");
    }

    // Plain (non-Link) playback start — same START layout as the Link row (Plugin.Link.Song.cs):
    //   START\t{serverNow}\t{song}\t{songStartServerMs}\t{startPosMs}
    // song = queue entry's file name; songStartServerMs = __b2base (buffered mode: song position x is stamped
    // __b2base + x, read back from Lua — the game's Z.ServerTime clock, not ServerNowMs) or, in B1 (no shared base),
    // serverNow at start.
    partial void LinkDiagPlainStart(string entry)
    {
        if (!LinkSpikeDiagPatch.On) return;
        try
        {
            long now = _services.CombatSnapshot.ServerNowMs;
            long b   = _bandPlayer.DiagSongBaseMs();
            LinkDiagRow($"START\t{now}\t{Path.GetFileName(entry)}\t{(b != 0 ? b : now)}\t{_bandPlayer.PositionMs}\t{LinkSpikeDiagPatch.LocalMs}");
            LinkDiagCfgRow(now);
        }
        catch { }
    }
}
