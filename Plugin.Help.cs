using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Per-setting help tooltips. A setting is rendered as [toggle] [label] … [?]; clicking "?" opens a single shared
// floating popover window anchored under the button, showing a title + richer multi-line description. The window
// framework has no hover tooltip, so this is the click-to-open equivalent. Content for every "?" is registered by
// key at build time (HelpToggle), and the one tip window renders whichever key is currently active.
public sealed partial class Plugin
{
    private readonly Dictionary<string, (Func<string> Title, Func<string> Body)> _helpFns = new();

    // Reset-icon PNG bytes, decoded once (the SpriteElement Func re-reads this ref every frame — never re-loads).
    private byte[]? _resetIconPng;
    private bool    _resetIconLoaded;

    private byte[]? ResetIconPng()
    {
        if (!_resetIconLoaded)
        {
            _resetIconLoaded = true;   // latch regardless so a missing/corrupt resource doesn't re-read per frame
            try
            {
                using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.Maestro.icon_reset.png");
                if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); _resetIconPng = ms.ToArray(); }
            }
            catch { }
        }
        return _resetIconPng;
    }

    // The compact "reset to default" control: the ↺ icon rendered via SpriteElement (full uv — a single standalone
    // image), kept SQUARE and made clickable by a SelectableElement wrapper, mirroring the StatInspector/CooldownBar
    // gear pattern. Gotcha: both SelectableElement and CellElement force-expand their child's WIDTH to fill the cell
    // (childControlWidth + childForceExpandWidth), but NOT its height — so a bare 16×16 sprite got stretched to the
    // cell's inner width (~22px) while staying 16px tall → wider-than-tall squish. A RowElement does NOT force-expand
    // (childForceExpandWidth=false), so nesting the sprite in a centred Row pins it to its intrinsic 16×16 no matter
    // how wide the clickable chip is. The cell is also kept snug (28px ≈ 16 icon + 12 selectable h-padding) so the
    // hover chip hugs the icon instead of leaving dead click area.
    private HudElement ResetIconButton(Action reset)
        => new CellElement(
            new SelectableElement(
                new RowElement(new HudElement[]
                {
                    new SpriteElement(() => ResetIconPng()!, new UvRect(0f, 0f, 1f, 1f), 16, 16),
                }, Justify: RowJustify.Center),
                OnClick: reset),
            Width: 28f);

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
        // OnClickWithRect reports SCREEN PIXELS; SetRect takes CANVAS units — scale first, or the tip drifts off its
        // "?" whenever the UI scale isn't 1 (WindowBuilder-Patterns.md, click-to-open tooltip).
        float k = _services.Framework.ScreenWidth > 0 && _services.Framework.CanvasWidth > 1f
            ? _services.Framework.CanvasWidth / _services.Framework.ScreenWidth : 1f;
        r = new WindowRect(r.X * k, r.Y * k, r.Width * k, r.Height * k);
        float canvasH = _services.Framework.CanvasHeight > 1f ? _services.Framework.CanvasHeight : _services.Framework.ScreenHeight;
        // Anchor below the "?", right-aligned to it so the panel opens to the LEFT of the "?" column instead of
        // covering the settings it describes. Clamp to the screen.
        float x = System.Math.Max(10f, r.X + r.Width - TipWidth);
        float y = System.Math.Min(r.Y + r.Height + 4f, canvasH - 40f);
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

    // Settings rows (house style, approved design 2026-11-06): every row ends in a "?" so the "?" column lines up
    // down the panel. Slider row = [label | round-knob slider | value | ↺ | ?]; toggle row = [toggle | label | … | ?].
    // Label column: the longest slider label in any language is en "Gap between songs" ≈ 127 px (then fil "Puwang ng
    // restrike" ≈ 122, "Volume ng monitor" ≈ 120), measured with Arial / Yu Gothic / Malgun / Leelawadee at 14 px as an
    // estimate of the game face — 120 cut it to "Gap between so…" in-game, so 140 leaves ~10 % headroom.
    private const float SliderLabelW = 140f;
    private const float SliderValueW = 50f;
    private const float HelpSize     = 20f;    // the round "?" button: a 20 px circle

    // The "?" button: a small round Neutral button (20 px circle, bold-12 "?"), lighter on hover, tinted with the accent
    // while its tip is open. Bare selectable = no padding / wash of its own, so the circle is exactly HelpSize and the
    // hover keeps its round shape; the whole circle is the click target. OnClickWithRect anchors the tip under it.
    private readonly Dictionary<string, bool> _helpHover = new();

    private HudElement HelpButtonCell(string key)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, HelpSize),   // height strut; the label box below sets the width
                new TextElement(() => "?", Color: () => NeutralText(), Width: HelpSize, Align: TextAlign.Center,
                    NoWrap: true, FontSize: 12) { Bold = true },
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { })                       // real work is in OnClickWithRect (needs the button's rect)
            {
                Bare = true,
                OnHover = over => _helpHover[key] = over,
                OnClickWithRect = r => ToggleTip(key, r),
            })
        {
            FillColorFn = () => _tipKey == key && _tipWindow.IsShown ? Over(AccentColor() with { A = 0.35f }, NeutralFill())
                              : _helpHover.TryGetValue(key, out var h) && h ? Over(TextColor() with { A = 0.10f }, NeutralFill())
                              : NeutralFill(),
            CornerRadius = HelpSize / 2f, PaddingX = 0f, PaddingY = 0f,
        };

    // Registers tooltip content for `key` and returns its "?" pill. Append it as the LAST child of a row so the "?"
    // lands in the panel's right-edge column. `label` is the tooltip title.
    private HudElement HelpDot(string key, Func<string> label, Func<string> help)
    {
        _helpFns[key] = (label, help);
        return HelpButtonCell(key);
    }

    private ColorRgba RowTextColor(Func<bool>? enabled) => enabled == null || enabled() ? TextColor() : MutedColor();

    // [toggle] [label] … [?]. `enabled` greys the toggle (framework) and the label.
    private HudElement HelpToggle(string key, Func<bool> get, Action<bool> set, Func<string> label,
                                  Func<string> help, Func<bool>? enabled = null)
        => new RowElement(new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: get, Set: set, Enabled: enabled),
            new CellElement(new TextElement(label, Color: () => RowTextColor(enabled), NoWrap: true) { Ellipsize = true }, Weight: 1f),
            HelpDot(key, label, help),
        }, Gap: 6f);

    // [label 120] [round-knob slider] [value 50, right] [↺] [?]. Pass the slider built with RoundSlider (and the same
    // `enabled`, which greys the label and value too).
    private HudElement SliderRow(string key, Func<string> label, HudElement slider,
                                 Func<string> value, Action reset, Func<string> help, Func<bool>? enabled = null)
        => new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(label, Color: () => RowTextColor(enabled), NoWrap: true) { Ellipsize = true }, Width: SliderLabelW),
            new CellElement(slider, Weight: 1f),
            new CellElement(new TextElement(value, Color: () => RowTextColor(enabled), Align: TextAlign.Right, NoWrap: true), Width: SliderValueW),
            ResetIconButton(() => { if (enabled == null || enabled()) reset(); }),
            HelpDot(key, label, help),
        }, Gap: 6f);
}
