using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// "Monitor Volume" (ear-return / 耳返) control for the band instrument. Mirrors the game's own band volume panel:
// persist to the SAME LocalUserDataMgr key the game reads, and apply live via
// InstrumentService:SetInstrumentVolume(EPlayerServer, v). See the Monitor-Volume finding in Band-Instrument-Playback.md.
//
// The Lua bridge is write-only for numbers (get_Item can't return a float — see Lua-Injection-from-CSharp.md), so our
// config is the slider's display source of truth. We write the same store the game uses, so once set the values match;
// we never auto-push on startup, so a value you set in the game's own panel is not clobbered when the plugin loads.
public sealed partial class Plugin
{
    private const int    MonitorVolChannel = 2;   // EPlayerServer / P1_Inst_Back_Volume (耳返 ear-return = "Monitor Volume")
    private const string MonitorVolKey     = "BKL_BAND_P1_Inst_Back_Volume";
    private int _monitorVol;   // 0..100; the game's ear-return default is 0

    private void SetMonitorVolume(int v)
    {
        _monitorVol = Math.Clamp(v, 0, 100);
        _cfg.Set<int>("monitor_volume", _monitorVol);
        _cfg.Save();
        ApplyMonitorVolume();
    }

    // Push the current value the way the band UI does: persist to LocalUserDataMgr + apply live. Best-effort — if no
    // instrument is summoned the InstrumentService is nil (guarded), but the persisted key still takes effect on load.
    private void ApplyMonitorVolume()
    {
        string v = _monitorVol.ToString(CultureInfo.InvariantCulture);
        _services.Lua.DoString(
            "pcall(function() Z.LocalUserDataMgr.SetFloatByLua(E.LocalUserDataType.Device, '" + MonitorVolKey + "', " + v + ") " +
            "local svc = Z.DIServiceMgr.InstrumentService " +
            "if svc ~= nil then svc:SetInstrumentVolume(Panda.ZGame.EInstrumentVolumeType.IntToEnum(" + MonitorVolChannel + "), " + v + ") end end)");
    }

    private HudElement BuildMonitorVolumeRow() => SliderRow("monitor_volume", () => "Monitor vol",
        new SliderElement(Get: () => _monitorVol, Set: v => SetMonitorVolume((int)System.MathF.Round(v)), Min: 0f, Max: 100f),
        () => $"{_monitorVol}",
        () => SetMonitorVolume(0),
        () => "How loud you hear your OWN instrument through the ear-return (monitor) feed. Local only — it does not "
            + "change what other players hear.\n\n"
            + "You may need to enable \"Mute my local sound\" to avoid hearing your instrument doubled.");
}
