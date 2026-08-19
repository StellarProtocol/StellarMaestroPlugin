using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Per-setting help tooltips. A setting is rendered as [toggle] [?] [label]; clicking "?" opens a single shared
// floating popover window anchored under the button, showing a title + richer multi-line description. The window
// framework has no hover tooltip, so this is the click-to-open equivalent. Content for every "?" is registered by
// key at build time (HelpToggle), and the one tip window renders whichever key is currently active.
public sealed partial class Plugin
{
    private readonly Dictionary<string, (Func<string> Title, Func<string> Body)> _helpFns = new();
    private IWindowControl _tipWindow = null!;
    private string _tipKey = "";   // which help entry the tip window is showing; "" = closed
    private const float TipWidth = 360f;
    private WindowRect _tipRect;    // where the tip should sit (re-applied for a few frames after opening)
    private int _tipRepositionTicks;

    // Toggle the tooltip for `key`: re-clicking the open one closes it; otherwise (re)point + anchor it under the "?".
    private void ToggleTip(string key, WindowRect r)
    {
        if (_tipKey == key && _tipWindow.IsShown) { _tipKey = ""; _tipWindow.SetVisible(false); return; }
        _tipKey = key;
        // Anchor below the "?", right-aligned to it so the panel opens to the LEFT of the settings column (which
        // sits at the screen's right edge) instead of covering the settings it describes. Clamp to the screen.
        float x = System.Math.Max(10f, r.X + r.Width - TipWidth);
        float y = System.Math.Min(r.Y + r.Height + 4f, _services.Framework.ScreenHeight - 40f);
        _tipRect = new WindowRect(x, y, TipWidth, 0f);
        _tipWindow.SetVisible(true);
        _tipWindow.SetRect(_tipRect);
        _tipWindow.BringToFront();   // may already be visible (switching between "?"s) → won't remount, needs the resort
        _tipWindow.MarkDirty();      // re-pull TipTitle/TipBody for the new key this frame
        // First-ever open mounts the window and applies its DefaultRect AFTER this SetRect → it would land at the
        // default spot. Re-apply the anchored rect for the next few frames so it snaps to the "?" on first open too.
        _tipRepositionTicks = 4;
    }

    // Called each frame (from PlaylistTick): re-assert the tip position while the reposition window is open.
    private void TipRepositionTick()
    {
        if (_tipRepositionTicks <= 0) return;
        _tipRepositionTicks--;
        if (_tipWindow.IsShown) _tipWindow.SetRect(_tipRect);
    }

    private string TipTitle() => _helpFns.TryGetValue(_tipKey, out var e) ? e.Title() : "";
    private string TipBody()  => _helpFns.TryGetValue(_tipKey, out var e) ? e.Body()  : "";

    // The shared floating tooltip: a small, non-persisted GlassMenu popover with a ✕ that also clears the active key
    // (so the "?" accent highlight releases). Positioned per click by ToggleTip; starts hidden.
    private IWindowControl RegisterTipWindow()
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "maestro.tip",
                Title:       _loc.T("mst.win.help"),
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - TipWidth - 20f, 20f, TipWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Help popover for the Maestro tools — only relevant while in-world.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: BuildTipRoot(),
            OnClose: () => { _tipKey = ""; w!.SetVisible(false); }));
        _windows.Add(w);
        return w;
    }

    private HudElement BuildTipRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(TipTitle, Emphasis: true),
        new TextElement(TipBody, Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
    }, Gap: 6f)
    { Padding = 2 };

    // The "?" help button for a slider/number row: registers the tooltip content for `key` and returns the button
    // width. Settings labels share this so their sliders (and the right-aligned "?" column) line up; wide enough
    // for the longest label ("Restrike gap") on one line.
    private const float SliderLabelW = 90f;

    // The bare "?" button cell (fixed width) that opens the tooltip for `key`. Content is registered separately.
    private HudElement HelpButtonCell(string key)
        => new CellElement(new ButtonElement(
            Label:   () => "?",
            OnClick: () => { },                       // real work is in OnClickWithRect (needs the button's rect)
            Active:  () => _tipKey == key)
        { OnClickWithRect = r => ToggleTip(key, r) }, Width: 26f);

    // Registers tooltip content for `key` and returns the "?" button cell. Append it as the LAST child of a row so
    // the "?" lands in the panel's right-edge column (aligned across all rows). `label` is the tooltip title.
    private HudElement HelpDot(string key, Func<string> label, Func<string> help)
    {
        _helpFns[key] = (label, help);
        return HelpButtonCell(key);
    }

    // A boolean setting rendered as [toggle] [label] … [?] — a flexible spacer pushes the "?" to the right edge so
    // it lines up with the sliders' "?" column. `label`/`help` are Funcs; `enabled` gates the toggle as before.
    private HudElement HelpToggle(string key, Func<bool> get, Action<bool> set, Func<string> label,
                                  Func<string> help, Func<bool>? enabled = null)
    {
        _helpFns[key] = (label, help);   // register content so the shared tip window can render this key
        return new RowElement(new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: get, Set: set, Enabled: enabled),
            new TextElement(label),
            new SpacerElement(),          // flexible: pushes the "?" to the panel's right edge
            HelpButtonCell(key),
        }, Gap: 6f);
    }

    // A slider/number setting: [label] [slider] [value] [↺ reset] [?]. The label width is uniform (SliderLabelW) and
    // the "?" is the last child, so both the sliders and the "?" align in columns across the settings panel.
    private HudElement SliderRow(string key, Func<string> label, HudElement slider,
                                 Func<string> value, Action reset, Func<string> help)
        => new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(label, Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: SliderLabelW),
            new CellElement(slider, Weight: 1f),
            new CellElement(new TextElement(value), Width: 48f),
            new CellElement(new ButtonElement(Label: () => "↺", OnClick: reset), Width: 34f),
            HelpDot(key, label, help),
        }, Gap: 6f);
}
