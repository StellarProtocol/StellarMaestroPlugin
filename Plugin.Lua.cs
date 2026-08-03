using System;
using System.Reflection;

namespace Stellar.Maestro;

// C# → game-Lua bridge: DoString injection + boolean-sentinel read-back.
// See Knowledge Base\Lua-Injection-from-CSharp.md.
public sealed partial class Plugin
{
    private object?     _luaState;
    private MethodInfo? _luaDoString;
    private MethodInfo? _luaGetItem;

    private void EnsureLuaState()
    {
        if (_luaDoString != null) return;

        var lsType = FindType("LuaInterface.LuaState");
        if (lsType != null)
        {
            _luaState =
                lsType.GetProperty("mainState", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
                ?? lsType.GetField("mainState",  BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
        }

        if (_luaState is null)
        {
            var clientType = FindType("LuaClient");
            var clientInst = clientType
                ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (clientInst != null)
            {
                var t = clientInst.GetType();
                _luaState =
                    t.GetProperty("luaState", BindingFlags.Instance | BindingFlags.Public)?.GetValue(clientInst)
                    ?? t.GetField("luaState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(clientInst);
            }
        }

        if (_luaState != null)
        {
            foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "DoString" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length < 2 || ps[0].ParameterType != typeof(string)) continue;
                if (m.ReturnType == typeof(void)) { _luaDoString = m; break; }
            }

            // get_Item(object) is the only global reader this game exposes; it returns null for Lua nil and a
            // non-null Il2CppSystem.Object for anything else — use as a boolean sentinel only.
            foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "get_Item" || m.IsGenericMethod) continue;
                if (m.GetParameters().Length != 1) continue;
                _luaGetItem = m;
                break;
            }
        }

        _services.Log.Info($"[Maestro] LuaState type={_luaState?.GetType()?.FullName ?? "null"} DoString={(_luaDoString != null)} GetItem={(_luaGetItem != null)}");
    }

    // Reads a Lua global back into C#. Returns null for Lua nil, non-null for any non-nil value.
    // The value itself cannot be extracted (get_Item boxes everything) — use as a boolean sentinel only.
    private object? ReadLuaRaw(string key)
    {
        EnsureLuaState();
        if (_luaState is null || _luaGetItem is null) return null;
        try { return _luaGetItem.Invoke(_luaState, new object[] { key }); }
        catch { return null; }
    }

    private string? CallLua(string chunk)
    {
        EnsureLuaState(); // idempotent; primes the state so first-use callers (e.g. the MIDI player) work without a warm-up
        if (_luaState is null || _luaDoString is null)
        {
            _services.Log.Warning("[Maestro] CallLua: LuaState not ready");
            return "LuaState not ready";
        }
        try
        {
            _luaDoString.Invoke(_luaState, new object[] { chunk, "maestro" });
            return null;
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            _services.Log.Warning($"[Maestro] CallLua threw: {msg}");
            return msg;
        }
    }
}
