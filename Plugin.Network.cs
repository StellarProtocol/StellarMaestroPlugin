using System;

namespace Stellar.Maestro;

// Network Sync tuning knobs (lookahead / send interval / tone lead). Their sliders live in the Settings window's NETWORK
// SYNC panel (Plugin.Settings.cs) — the separate Network window is gone. All values persist and apply live.
public sealed partial class Plugin
{
    private int _bandAheadMs = 400;   // lookahead ms
    private int _bandBatchMs = 100;   // send-batch interval ms
    private int _bandToneLeadMs = 120; // tone/technique real-time lead ms (fixes late-sounding tone switch at a boundary)

    private void LoadNetworkConfig()
    {
        _bandAheadMs    = _cfg.Get<int>("net_ahead_ms", 400);
        _bandBatchMs    = Math.Clamp(_cfg.Get<int>("net_batch_ms", 100), 100, 400);   // an old saved value (e.g. 33) clamps up; UI shows the clamped value
        _bandToneLeadMs = _cfg.Get<int>("tone_lead_ms", 120);
        _bandPlayer.NetLookaheadMs = _bandAheadMs;
        _bandPlayer.NetBatchMs     = _bandBatchMs;
        _bandPlayer.ToneLeadMs     = _bandToneLeadMs;
    }

    private void SetAhead(int v)    { _bandAheadMs    = Math.Clamp(v, 100, 1500); _bandPlayer.NetLookaheadMs = _bandAheadMs;    _cfg.Set<int>("net_ahead_ms", _bandAheadMs);      _cfg.Save(); }
    private void SetBatch(int v)    { _bandBatchMs    = Math.Clamp(v, 100, 400);  _bandPlayer.NetBatchMs     = _bandBatchMs;    _cfg.Set<int>("net_batch_ms", _bandBatchMs);      _cfg.Save(); }
    private void SetToneLead(int v) { _bandToneLeadMs = Math.Clamp(v, 0, 500);    _bandPlayer.ToneLeadMs     = _bandToneLeadMs; _cfg.Set<int>("tone_lead_ms", _bandToneLeadMs);  _cfg.Save(); }
}
