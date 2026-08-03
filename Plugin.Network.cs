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

    private void LoadNetworkConfig()
    {
        _bandAheadMs = _cfg.Get<int>("net_ahead_ms", 400);
        _bandBatchMs = _cfg.Get<int>("net_batch_ms", 100);
        _bandPlayer.NetLookaheadMs = _bandAheadMs;
        _bandPlayer.NetBatchMs     = _bandBatchMs;
    }

    private void SetAhead(int v)  { _bandAheadMs = Math.Clamp(v, 100, 1500); _bandPlayer.NetLookaheadMs = _bandAheadMs; _cfg.Set<int>("net_ahead_ms", _bandAheadMs); _cfg.Save(); }
    private void SetBatch(int v)  { _bandBatchMs = Math.Clamp(v, 16, 250);   _bandPlayer.NetBatchMs     = _bandBatchMs; _cfg.Set<int>("net_batch_ms", _bandBatchMs); _cfg.Save(); }

    private HudElement BuildNetworkRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => "Buffered Network Sync", Emphasis: true),
        new TextElement(() => "Notes are timestamped and streamed ahead so listeners hear a steadier performance. These knobs apply live.",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => "Lookahead", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 84f),
            new CellElement(new SliderElement(
                Get: () => _bandAheadMs,
                Set: v  => SetAhead((int)System.MathF.Round(v)),
                Min: 100f, Max: 1500f), Weight: 1f),
            new CellElement(new TextElement(() => $"{_bandAheadMs}ms"), Width: 60f),
            new CellElement(new ButtonElement(Label: () => "↺", OnClick: () => SetAhead(400)), Width: 34f),
        }, Gap: 6f),
        new TextElement(() => "How far ahead notes are sent (stay under the receiver's ~500ms buffer).",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => "Send every", Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 84f),
            new CellElement(new SliderElement(
                Get: () => _bandBatchMs,
                Set: v  => SetBatch((int)System.MathF.Round(v)),
                Min: 16f, Max: 250f), Weight: 1f),
            new CellElement(new TextElement(() => $"{_bandBatchMs}ms"), Width: 60f),
            new CellElement(new ButtonElement(Label: () => "↺", OnClick: () => SetBatch(100)), Width: 34f),
        }, Gap: 6f),
        new TextElement(() => "Batch cadence — smaller = smoother local, more packets.",
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
    }, Gap: 8f);
}
