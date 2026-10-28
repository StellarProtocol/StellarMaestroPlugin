using System;
using System.IO;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// MAESTRO LINK — dev-only sync diagnostics (left over from the 2026-10-28 spike that proved shared-T band sync).
// Fully DORMANT unless C:\Users\Public\maestro-link-spike\ exists (probed every 2 s; activation lazily installs the
// read-only LinkSpikeDiagPatch). No UI, no user-facing strings — log lines + one file:
//   diag-<CharacterName>.tsv → PLAY/RECV rows (what each listener heard, stamped with ServerNowMs) + START rows when a
//                               Link part starts; flushed ~1 Hz. Diff two clients' files to measure per-note band delta.
// The spike's cmd.json file-command poller was removed — Maestro Link (Plugin.Link*.cs) is the real transport now.
// Server time is ALWAYS CombatSnapshot.ServerNowMs — never reflect ZServerTime (Server-Clock-and-Ping-HUD.md).
public sealed partial class Plugin
{
    private const string LinkDiagDir = @"C:\Users\Public\maestro-link-spike";

    private bool   _ldActive;
    private double _ldProbeAccumMs = 2000;   // probe on the first tick
    private double _ldFlushAccumMs;
    private string _ldName = "";

    private void InitLinkDiag() => _services.Framework.Update += LinkDiagTick;

    private void DisposeLinkDiag()
    {
        try { _services.Framework.Update -= LinkDiagTick; } catch { }
        try { LinkSpikeDiagPatch.Flush(); LinkSpikeDiagPatch.Reset(); } catch { }
    }

    // Row marker for the diag file (no-op unless active + a log file is bound).
    private static void LinkDiagRow(string line) => LinkSpikeDiagPatch.Row(line);

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
            _ldFlushAccumMs += dt * 1000.0;
            if (_ldFlushAccumMs < 1000) return;
            _ldFlushAccumMs = 0;
            RefreshLinkDiagIdentity();
            LinkSpikeDiagPatch.Flush();
        }
        catch (Exception ex) { _services.Log.Warning($"[MaestroLink] diag tick error: {ex.Message}"); }
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
