using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.Maestro;

// STEP-0 SPIKE for ensemble auto-play: can we read the ensemble grid from C#?
// Numbers can't be read back through Lua (get_Item = boolean sentinels), so we resolve Panda.ZGame.InstrumentService
// in C# and call its parameterless getters directly. The instance is captured from a Tick() postfix (ITickable, runs
// every frame). If Read() returns sane values while in an ensemble, the rest of the plan (grid-aligned scheduling)
// is unblocked. See Band-Instrument-Playback.md "Ensemble mode".
internal static class EnsemblePatch
{
    private const string TargetType = "Panda.ZGame.InstrumentService";

    private static Harmony?    _harmony;
    private static object?     _instance;       // captured InstrumentService (Il2Cpp proxy)
    private static MethodInfo? _mIsIn, _mBpm, _mBeat, _mStart;
    private static Action<string>? _onLog;

    internal static bool Install(string harmonyId, Action<string> onLog)
    {
        _onLog = onLog;

        var t = FindType(TargetType);
        if (t is null) { onLog("[EnsemblePatch] InstrumentService not found"); return false; }

        var tick = t.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public);
        if (tick is null) { onLog("[EnsemblePatch] Tick not found"); return false; }

        _mIsIn  = t.GetMethod("GetPlayerIsInEnsemble",     BindingFlags.Instance | BindingFlags.Public);
        _mBpm   = t.GetMethod("GetEnsembleBpm",            BindingFlags.Instance | BindingFlags.Public);
        _mBeat  = t.GetMethod("GetEnsembleBeat",           BindingFlags.Instance | BindingFlags.Public);
        _mStart = t.GetMethod("GetEnsembleStartTimestamp", BindingFlags.Instance | BindingFlags.Public);

        _harmony = new Harmony(harmonyId);
        _harmony.Patch(tick, postfix: new HarmonyMethod(typeof(EnsemblePatch), nameof(TickPostfix)));

        onLog($"[EnsemblePatch] installed (getters found: inEns={_mIsIn != null} bpm={_mBpm != null} beat={_mBeat != null} start={_mStart != null})");
        return true;
    }

    internal static void Uninstall()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony  = null;
        _instance = null;
        _mIsIn = _mBpm = _mBeat = _mStart = null;
    }

    private static void TickPostfix(Il2CppObjectBase __instance) => _instance = __instance;

    // Reads the live ensemble grid. Returns null if the instance/getters aren't available.
    internal static (int inEnsemble, int bpm, int beat, long startTs)? Read()
    {
        if (_instance == null) { _onLog?.Invoke("[EnsemblePatch] Read: no instance captured yet (Tick not run?)"); return null; }
        try
        {
            int  inEns = _mIsIn  != null ? Convert.ToInt32(_mIsIn.Invoke(_instance, null))  : -1;
            int  bpm   = _mBpm   != null ? Convert.ToInt32(_mBpm.Invoke(_instance, null))   : -1;
            int  beat  = _mBeat  != null ? Convert.ToInt32(_mBeat.Invoke(_instance, null))  : -1;
            long start = _mStart != null ? Convert.ToInt64(_mStart.Invoke(_instance, null)) : -1;
            return (inEns, bpm, beat, start);
        }
        catch (Exception ex) { _onLog?.Invoke($"[EnsemblePatch] Read error: {ex.Message}"); return null; }
    }

    // Current server time (ms) via ZServerTime : ZSingleton<ZServerTime>.GetServerTime(). -1 if unavailable.
    private static MethodInfo? _mServerTime;
    private static object?     _serverTimeInst;
    internal static long ReadServerTime()
    {
        try
        {
            if (_mServerTime == null || _serverTimeInst == null)
            {
                var t = FindType("ZServerTime") ?? FindType("Panda.Utility.ZServerTime");
                if (t == null) { _onLog?.Invoke("[EnsemblePatch] ZServerTime type not found"); return -1; }
                _serverTimeInst = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)?.GetValue(null);
                _mServerTime = t.GetMethod("GetServerTime", BindingFlags.Instance | BindingFlags.Public);
                if (_serverTimeInst == null || _mServerTime == null) { _onLog?.Invoke($"[EnsemblePatch] ZServerTime inst={_serverTimeInst != null} getter={_mServerTime != null}"); return -1; }
            }
            return Convert.ToInt64(_mServerTime.Invoke(_serverTimeInst, null));
        }
        catch (Exception ex) { _onLog?.Invoke($"[EnsemblePatch] ReadServerTime error: {ex.Message}"); return -1; }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var x = asm.GetType(fullName);
            if (x != null) return x;
        }
        return null;
    }
}
