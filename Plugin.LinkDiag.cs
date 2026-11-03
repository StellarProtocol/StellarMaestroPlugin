using System;
using System.IO;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// MAESTRO LINK — dev-only sync diagnostics (left over from the 2026-10-28 spike that proved shared-T band sync).
// DEBUG BUILDS ONLY: the csproj removes this file (and LinkSpikeDiagPatch.cs, MidiPlayer.Diag.cs, Plugin.SweepCmd.cs)
// from non-Debug builds; the callers reach it through partial methods declared in Plugin.cs, so they vanish in Release.
// Even in Debug it is fully DORMANT unless C:\Users\Public\maestro-link-spike\ exists (probed every 2 s; activation lazily installs the
// read-only LinkSpikeDiagPatch). No UI, no user-facing strings — log lines + one file:
//   diag-<CharacterName>.tsv → PLAY/REL/RECV rows (what each listener heard, stamped with ServerNowMs) + START rows when a
//                               Link part starts; flushed ~1 Hz. Diff two clients' files to measure per-note band delta.
// The spike's cmd.json file-command poller was removed — Maestro Link (Plugin.Link*.cs) is the real transport now. The
// note-accuracy sweep adds SEND/END/CFG/CMD rows + a cmd.txt poller (MidiPlayer.Diag.cs, Plugin.SweepCmd.cs).
// Every row ends with a trailing localMs column (local Stopwatch, LinkSpikeDiagPatch.LocalMs); CLK rows (below) log
// both server clocks against it.
// Server time is ALWAYS CombatSnapshot.ServerNowMs — never reflect ZServerTime (Server-Clock-and-Ping-HUD.md).
public sealed partial class Plugin
{
    private const string LinkDiagDir = @"C:\Users\Public\maestro-link-spike";

    private bool   _ldActive;
    private double _ldProbeAccumMs = 2000;   // probe on the first tick
    private double _ldFlushAccumMs;
    private string _ldName = "";

    partial void InitLinkDiag() => _services.Framework.Update += LinkDiagTick;

    partial void DisposeLinkDiag()
    {
        try { _services.Framework.Update -= LinkDiagTick; } catch { }
        try { LinkSpikeDiagPatch.Flush(); LinkSpikeDiagPatch.Reset(); } catch { }
    }

    // Row marker for the diag file (no-op unless active + a log file is bound).
    private static void LinkDiagRow(string line) => LinkSpikeDiagPatch.Row(line);

    // Link part start (Plugin.Link.Song.cs TryStartLinkedPart): START\t{serverNow}\t{songId}\t{T}\t{startPos}, then CFG.
    partial void LinkDiagLinkStart(long now, string songId, long songStartMs, int startPos)
    {
        LinkDiagRow($"START\t{now}\t{songId}\t{songStartMs}\t{startPos}\t{LinkSpikeDiagPatch.LocalMs}");
        LinkDiagCfgRow(now);
    }

    private void LinkDiagTick(float dt)
    {
        try
        {
            if (!_ldActive)
            {
                _ldProbeAccumMs += dt * 1000.0;
                if (_ldProbeAccumMs < 2000) return;
                _ldProbeAccumMs = 0;
                if (!Directory.Exists(LinkDiagDir)) return;
                _ldActive = true;
                _services.Log.Info($"[MaestroLink] diag ACTIVE — folder present: {LinkDiagDir}");
                try { LinkSpikeDiagPatch.Install(_services.Harmony.Create("linkspike"), () => _services.CombatSnapshot.ServerNowMs, m => _services.Log.Info(m)); }
                catch (Exception ex) { _services.Log.Warning($"[MaestroLink] diag patch install failed: {ex.Message}"); }
                return;
            }
            SweepCmdTick(dt);   // dev-only file-command poller (Plugin.SweepCmd.cs)
            LinkDiagClockTick(dt);
            _ldFlushAccumMs += dt * 1000.0;
            if (_ldFlushAccumMs < 1000) return;
            _ldFlushAccumMs = 0;
            RefreshLinkDiagIdentity();
            LinkSpikeDiagPatch.Flush();
        }
        catch (Exception ex) { _services.Log.Warning($"[MaestroLink] diag tick error: {ex.Message}"); }
    }

    // CLK\t{serverNow}\t{luaServerTime}\t{localMs} at ~10 Hz — both server clocks against the never-resyncing local
    // Stopwatch, to see whether either STEPS on its ~5 s resync (no latency correction) and skews received note timing.
    //   serverNow     = framework CombatSnapshot.ServerNowMs (what PLAY/REL/RECV `now` use)
    //   luaServerTime = game Lua Z.ServerTime:GetServerTime() (what __b2base / SEND playTime use). Read the same one-way
    //                   way as __b2base: DoString parks it in a global, typed-number readback. NEVER via C# reflection /
    //                   GetSingleton on ZServerTime — that stalls the game's clock (Server-Clock-and-Ping-HUD.md).
    //                   The global is nil'd first inside the pcall, so a failed call can't leave a stale value → -1.
    private double _ldClockAccumMs;

    private void LinkDiagClockTick(float dt)
    {
        _ldClockAccumMs += dt * 1000.0;
        if (_ldClockAccumMs < 100) return;
        _ldClockAccumMs = 0;
        if (!LinkSpikeDiagPatch.On) return;   // no file bound yet → skip the Lua round-trip too
        long now = _services.CombatSnapshot.ServerNowMs;
        long lua = -1;
        try
        {
            _services.Lua.DoString("pcall(function() rawset(_G,'__mdclk',nil) rawset(_G,'__mdclk', Z.ServerTime:GetServerTime()) end)");
            if (_services.Lua.TryReadGlobalNumber("__mdclk", out double v)) lua = (long)v;
        }
        catch { }
        LinkDiagRow($"CLK\t{now}\t{lua}\t{LinkSpikeDiagPatch.LocalMs}");
    }

    // Own character name binds the log file; the self uuid lets rows flag the own-echo (LocalEntityId is None until the
    // first combat — the diag patch also learns it from the local-audio PlayNote call).
    private void RefreshLinkDiagIdentity()
    {
        var ps = _services.PlayerState;
        string name = ps.IsAvailable ? (ps.Name ?? "") : "";
        if (name.Length > 0 && name != _ldName)
        {
            _ldName = name;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            LinkSpikeDiagPatch.SetLogFile(Path.Combine(LinkDiagDir, $"diag-{name}.tsv"));
        }
        var local = _services.CombatSnapshot.LocalEntityId;
        if (!local.IsNone) LinkSpikeDiagPatch.SelfUuid = local.Value;
    }
}
