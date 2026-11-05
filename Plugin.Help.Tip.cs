using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The help popover (the "?" tip), house style (user-approved 2026-11-06, option A):
//   [32 px accent tile with a white bold "?"] [bold setting name / muted "SETTINGS · HELP"] … [round ✕]
//   then the help text in one raised tile (TipWidth wide).
// No framework title bar: the header already carries the title and a close, and a second "Help ✕" bar above it
// would repeat both. With ShowTitleBar = false the whole frame is the drag handle (still Draggable). Placement is
// unchanged (ToggleTip / TipRepositionTick anchor it under the clicked "?"; re-clicking that "?" closes it).
public sealed partial class Plugin
{
    // The section ("Settings" / "Preview") each "?" key belongs to, for the subtitle. Recorded at build time: Plugin.cs
    // sets _helpBuildSection before building a window, and HelpDot stamps it on every key it registers.
    private readonly Dictionary<string, string> _helpSection = new();
    private string _helpBuildSection = "";

    private const float TipIconSize = 32f;

    private string TipSubtitle()
    {
        string help = _loc.T("mst.tip.help");
        return _helpSection.TryGetValue(_tipKey, out var s) && s.Length > 0
            ? (_loc.T(s) + " · " + help).ToUpperInvariant()
            : help.ToUpperInvariant();
    }

    private void CloseTip()
    {
        _tipKey = "";
        _tipWindow.SetVisible(false);
    }

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
            { Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
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
        new RowElement(new HudElement[]
        {
            TipIcon(),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(TipTitle, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(TipSubtitle, Color: () => MutedColor(), NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true },
            }, Gap: 1f), Weight: 1f),
            TipCloseButton(),
        }, Gap: 10f),
        // The help text: one raised tile (house Tile colour, theme-live), wrapping inside the 360 px popover.
        new BubbleElement(new TextElement(TipBody, Color: () => TextColor()))
        {
            FillColorFn = () => TileColor(), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        },
    }, Gap: 10f);

    // 32 px accent tile with a white bold "?" (the IconBox look, drawn with text instead of a PNG). The row holds a
    // 32 px strut, and CenterGlyphs centres the drawn "?" in its box (the RoundRadius note: radius 7 keeps the sliced
    // tile's min size, 16, under the 32 px side).
    private HudElement TipIcon() => new CellElement(new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, TipIconSize),
            new TextElement(() => "?", Color: () => White, Width: TipIconSize, Align: TextAlign.Center, NoWrap: true,
                FontSize: 18) { Bold = true, CenterGlyphs = true },
        }, Gap: 0.01f, Justify: RowJustify.Center))
    {
        FillColorFn = () => AccentColor(), CornerRadius = MathF.Round(TipIconSize * 0.22f), PaddingX = 0f, PaddingY = 0f,
    }, Width: TipIconSize);

    // Icon-only ✕ that closes the tip (and releases the "?" highlight): a round 24 px hover wash around a Bare
    // selectable (the house hover-follows-shape rule); muted glyph at rest, text colour on hover.
    private readonly HoverFlag _tipCloseHover = new();

    private HudElement TipCloseButton() => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
        {
            new SpacerElement(0f, 24f),
            new TextElement(() => "✕", Color: () => _tipCloseHover.On ? TextColor() : MutedColor(), Width: 24f,
                Align: TextAlign.Center, NoWrap: true),
        }, Gap: 0.01f, Justify: RowJustify.Center),
        OnClick: CloseTip) { Bare = true, OnHover = over => _tipCloseHover.On = over })
    {
        FillColorFn = () => _tipCloseHover.On ? Menu.MenuText with { A = 0.10f } : Transparent,
        CornerRadius = RoundRadius(24f), PaddingX = 0f, PaddingY = 0f,
    };
}
