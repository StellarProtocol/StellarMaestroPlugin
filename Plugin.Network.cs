using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The "Network Settings" window — buffered-mode toggle plus its tuning knobs (lookahead / send interval / resend).
// Opened from the main window's "Network Settings" button. All values persist and apply live to the player.
public sealed partial class Plugin
{
    private IWindowControl _networkWindow = null!;
    private int _bandAheadMs = 400;   // lookahead ms
    private int _bandBatchMs = 100;   // send-batch interval ms
    private int _bandToneLeadMs = 120; // tone/technique real-time lead ms (fixes late-sounding tone switch at a boundary)

    private void LoadNetworkConfig()
    {
        _bandAheadMs    = _cfg.Get<int>("net_ahead_ms", 400);
        _bandBatchMs    = _cfg.Get<int>("net_batch_ms", 100);
        _bandToneLeadMs = _cfg.Get<int>("tone_lead_ms", 120);
        _bandPlayer.NetLookaheadMs = _bandAheadMs;
        _bandPlayer.NetBatchMs     = _bandBatchMs;
        _bandPlayer.ToneLeadMs     = _bandToneLeadMs;
    }

    private void SetAhead(int v)    { _bandAheadMs    = Math.Clamp(v, 100, 1500); _bandPlayer.NetLookaheadMs = _bandAheadMs;    _cfg.Set<int>("net_ahead_ms", _bandAheadMs);      _cfg.Save(); }
    private void SetBatch(int v)    { _bandBatchMs    = Math.Clamp(v, 16, 250);   _bandPlayer.NetBatchMs     = _bandBatchMs;    _cfg.Set<int>("net_batch_ms", _bandBatchMs);      _cfg.Save(); }
    private void SetToneLead(int v) { _bandToneLeadMs = Math.Clamp(v, 0, 500);    _bandPlayer.ToneLeadMs     = _bandToneLeadMs; _cfg.Set<int>("tone_lead_ms", _bandToneLeadMs);  _cfg.Save(); }

    private HudElement BuildNetworkRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("mst.net.header"), Emphasis: true),
        new TextElement(() => _loc.T("mst.net.desc"),
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.net.lookahead"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 84f),
            new CellElement(new SliderElement(
                Get: () => _bandAheadMs,
                Set: v  => SetAhead((int)System.MathF.Round(v)),
                Min: 100f, Max: 1500f), Weight: 1f),
            new CellElement(new TextElement(() => $"{_bandAheadMs}ms"), Width: 60f),
            ResetIconButton(() => SetAhead(400)),
        }, Gap: 6f),
        new TextElement(() => _loc.T("mst.net.lookahead.desc"),
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.net.sendEvery"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 84f),
            new CellElement(new SliderElement(
                Get: () => _bandBatchMs,
                Set: v  => SetBatch((int)System.MathF.Round(v)),
                Min: 16f, Max: 250f), Weight: 1f),
            new CellElement(new TextElement(() => $"{_bandBatchMs}ms"), Width: 60f),
            ResetIconButton(() => SetBatch(100)),
        }, Gap: 6f),
        new TextElement(() => _loc.T("mst.net.sendEvery.desc"),
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.net.tonelead"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 84f),
            new CellElement(new SliderElement(
                Get: () => _bandToneLeadMs,
                Set: v  => SetToneLead((int)System.MathF.Round(v)),
                Min: 0f, Max: 500f), Weight: 1f),
            new CellElement(new TextElement(() => $"{_bandToneLeadMs}ms"), Width: 60f),
            ResetIconButton(() => SetToneLead(120)),
        }, Gap: 6f),
        new TextElement(() => _loc.T("mst.net.tonelead.desc"),
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
    }, Gap: 8f);
}
