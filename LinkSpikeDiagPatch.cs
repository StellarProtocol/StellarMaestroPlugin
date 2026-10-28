using System;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// MAESTRO-LINK SYNC SPIKE (dev diagnostic, gated by the spike folder — see Plugin.LinkDiag.cs).
// Listener-side measurement log: what arrived off the network (RECV) and what actually played (PLAY), stamped with the
// framework's server clock. Own notes come back too via the server-player "own-echo" path (Band-Instrument-Playback.md
// "Double note" own-echo), so ONE client's log carries both its own echo and every other performer → the per-note delta
// between entities IS the band-sync error.
//
// Patch targets (release_3.7_2 dump.cs):
//   229283  public void EntityInstrumentPlayNote(ZEntity entity, int note, bool isLocal = False)   — POSTFIX
//   229325  private void onInstrumentPlayEvent(ZEntity entity, RepeatedField<InstrumentSyncData>) — PREFIX
// Neither has an `in`-struct param (those crash via the trampoline). The framework's ToneRelay already PREFIXES
// onInstrumentPlayEvent — Harmony chains both; ours is read-only. Prefix (not postfix) on the batch handler because
// InstrumentSyncData is IObjPooled (dump.cs 731230): after the original returns the records may already be recycled.
internal static class LinkSpikeDiagPatch
{
    private const string TargetType = "Panda.ZGame.InstrumentService";

    private static Func<long>?   _serverNow;
    private static Action<string>? _log;
    private static PropertyInfo? _piUuid;                          // ZEntity.Uuid (plain long property)
    private static PropertyInfo? _piSync, _piPlayType, _piParam, _piPlayTime;   // InstrumentSyncData fields (IL2CPP fields → properties)
    private static bool _recResolved;

    private static readonly object        _lock = new();
    private static readonly StringBuilder _buf  = new();          // rows pending the ~1 s flush (patches run on the main thread)
    private static string? _path;

    // Our own player uuid. CombatSnapshot.LocalEntityId stays None until first combat (known framework quirk), so we
    // also LEARN it from the local-audio call: B2 plays local audio via EntityInstrumentPlayNote(PlayerEnt, key, true).
    internal static long SelfUuid;

    internal static bool Install(Harmony harmony, Func<long> serverNow, Action<string> log)
    {
        _serverNow = serverNow;
        _log = log;

        var t = StellarInterop.FindType(TargetType);
        if (t is null) { log("[LinkSpike] InstrumentService not found — diag patches skipped"); return false; }
        _piUuid = StellarInterop.FindType("Panda.ZGame.ZEntity")?.GetProperty("Uuid", BindingFlags.Instance | BindingFlags.Public);
        if (_piUuid is null) { log("[LinkSpike] ZEntity.Uuid not found — diag patches skipped"); return false; }

        bool play = false, recv = false;
        var mPlay = StellarInterop.FindMethod(t, "EntityInstrumentPlayNote", 3);
        if (mPlay != null)
            try { harmony.Patch(mPlay, postfix: new HarmonyMethod(typeof(LinkSpikeDiagPatch), nameof(PostPlayNote))); play = true; }
            catch (Exception ex) { log($"[LinkSpike] PlayNote patch failed: {ex.Message}"); }

        var mRecv = StellarInterop.FindMethod(t, "onInstrumentPlayEvent", 2);
        if (mRecv != null)
            try { harmony.Patch(mRecv, prefix: new HarmonyMethod(typeof(LinkSpikeDiagPatch), nameof(PreRecv))); recv = true; }
            catch (Exception ex) { log($"[LinkSpike] onInstrumentPlayEvent patch failed: {ex.Message}"); }

        log($"[LinkSpike] diag patches: PLAY={play} RECV={recv}");
        return play || recv;
    }

    internal static void SetLogFile(string path)
    {
        lock (_lock) _path = path;
    }

    // Free-form row (CMD markers etc.) — same file, same flush.
    internal static void Row(string line)
    {
        lock (_lock) { if (_path != null) _buf.Append(line).Append('\n'); }   // no file yet (name unresolved) → drop, don't grow unbounded
    }

    // Called ~1 Hz from the plugin tick. One small append; never throws.
    internal static void Flush()
    {
        string? text, path;
        lock (_lock)
        {
            if (_buf.Length == 0 || _path == null) return;
            text = _buf.ToString(); path = _path;
            _buf.Clear();
        }
        try { File.AppendAllText(path, text); }
        catch (Exception ex) { _log?.Invoke($"[LinkSpike] diag flush failed: {ex.Message}"); }
    }

    // __args = (ZEntity entity, int note, bool isLocal). Untyped __args, same verified shape as the framework ToneRelay.
    private static void PostPlayNote(object?[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 3 || __args[0] == null) return;
            long uuid = Convert.ToInt64(_piUuid!.GetValue(__args[0]));
            if (Convert.ToBoolean(__args[2]))   // local audio = our own entity → learn self uuid, don't log
            {
                if (uuid != 0) SelfUuid = uuid;
                return;
            }
            long now = _serverNow?.Invoke() ?? 0;
            int note = Convert.ToInt32(__args[1]);
            Row($"PLAY\t{now}\t{uuid}\t{(uuid == SelfUuid ? 1 : 0)}\t{note}");
        }
        catch { /* never break band audio */ }
    }

    // __args = (ZEntity entity, RepeatedField<InstrumentSyncData> events). RepeatedField is NOT IEnumerable in IL2CPP.
    private static void PreRecv(object?[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 2 || __args[0] == null || __args[1] == null) return;
            var events = __args[1]!;
            int n = StellarInterop.Count(events);
            if (n == 0) return;
            if (!_recResolved)
            {
                var sample = StellarInterop.Item(events, 0);
                if (sample == null) return;
                var rt = sample.GetType();
                _piSync     = StellarInterop.FindPropertyUp(rt, "SyncType");
                _piPlayType = StellarInterop.FindPropertyUp(rt, "PlayType");
                _piParam    = StellarInterop.FindPropertyUp(rt, "PlayParam");
                _piPlayTime = StellarInterop.FindPropertyUp(rt, "PlayTime");
                _recResolved = true;
                _log?.Invoke($"[LinkSpike] InstrumentSyncData props: sync={_piSync != null} type={_piPlayType != null} param={_piParam != null} time={_piPlayTime != null}");
            }
            if (_piSync == null || _piPlayType == null || _piParam == null || _piPlayTime == null) return;

            long now  = _serverNow?.Invoke() ?? 0;
            long uuid = Convert.ToInt64(_piUuid!.GetValue(__args[0]));
            int  self = uuid == SelfUuid ? 1 : 0;
            var sb = new StringBuilder(n * 48);
            for (int i = 0; i < n; i++)
            {
                var rec = StellarInterop.Item(events, i);
                if (rec == null) continue;
                sb.Append("RECV\t").Append(now).Append('\t').Append(uuid).Append('\t').Append(self).Append('\t')
                  .Append(Convert.ToInt32(_piSync.GetValue(rec))).Append('\t')
                  .Append(Convert.ToInt32(_piPlayType.GetValue(rec))).Append('\t')
                  .Append(Convert.ToInt32(_piParam.GetValue(rec))).Append('\t')
                  .Append(Convert.ToInt64(_piPlayTime.GetValue(rec))).Append('\n');
            }
            lock (_lock) { if (_path != null) _buf.Append(sb); }
        }
        catch { /* never break band audio */ }
    }

    internal static void Reset()
    {
        // Harmony teardown is owned by IHarmonyHost (auto-unpatch on dispose) — only clear transient state.
        lock (_lock) { _buf.Clear(); _path = null; }
        _serverNow = null;
    }
}
