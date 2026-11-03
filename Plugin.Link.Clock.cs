using System;

namespace Stellar.Maestro;

// MAESTRO LINK CLOCK — every Link time (room start T, count-in, position, status serverNowMs) is in the GAME's server
// clock: Lua Z.ServerTime:GetServerTime(), the clock listeners schedule our notes against and the one solo Maestro pins
// __b2base to. The framework's CombatSnapshot.ServerNowMs is NOT that clock: it is anchored on SyncServerTime field 1
// (client_milliseconds = our own PC time echoed back), so it equals the PC wall clock (Server-Clock-and-Ping-HUD.md,
// "Framework CombatSnapshot.ServerNowMs is the PC clock"). Measured 2026-11-03: on a PC 1.7 s slow, Link sent every note
// 1.7 s late → listeners got them 1.4–1.65 s past due and played them on arrival, bunched.
//
// So Link keeps using ServerNowMs for its smooth per-frame interpolation, plus a correction = Lua clock − ServerNowMs,
// sampled back-to-back in one frame. Plain clock reading, no smoothing / learning (repo Rule 12): the latest good sample
// wins. Refreshed right before a part start, on welcome, and every 5 s while linked (one DoString). The Lua clock itself
// steps ±5–80 ms on each 5 s game resync — accepted. NEVER read ZServerTime via C# reflection (stalls ping + day/night).
public sealed partial class Plugin
{
    private const double LinkClockRefreshMs = 5000;

    private long   _lkClockCorrMs;    // Lua Z.ServerTime − framework ServerNowMs (ms); 0 until the first good sample
    private bool   _lkClockSampled;   // at least one good sample taken (log only)
    private double _lkClockAccumMs;

    // Server "now" for everything Link: the game's own server clock. 0 = the framework clock isn't synced yet.
    private long LinkServerNowMs()
    {
        long fw = _services.CombatSnapshot.ServerNowMs;
        return fw > 0 ? fw + _lkClockCorrMs : 0;
    }

    // Re-sample the correction. Global nil'd first inside the pcall so a failed call can't leave a stale value; a failed
    // read, a non-positive Lua time, or an unsynced framework clock keeps the previous correction.
    private void RefreshLinkClock()
    {
        _lkClockAccumMs = 0;
        try
        {
            _services.Lua.DoString("pcall(function() rawset(_G,'__lkclk',nil) rawset(_G,'__lkclk', Z.ServerTime:GetServerTime()) end)");
            long fw = _services.CombatSnapshot.ServerNowMs;   // read right after the Lua sample — same frame
            if (!_services.Lua.TryReadGlobalNumber("__lkclk", out double lua) || lua <= 0 || fw <= 0) return;
            _lkClockCorrMs  = (long)lua - fw;
            _lkClockSampled = true;
        }
        catch (Exception ex) { _services.Log.Warning($"[MaestroLink] server-clock read error: {ex.Message}"); }
    }

    // Linked-only: keep the correction fresh so status reports (the server's room-clock estimate) stay right.
    private void LinkClockTick(float dt)
    {
        _lkClockAccumMs += dt * 1000.0;
        if (_lkClockAccumMs >= LinkClockRefreshMs) RefreshLinkClock();
    }

    private string LinkClockLabel() => _lkClockSampled ? $"{_lkClockCorrMs:+0;-0;0}ms" : "n/a";
}
