using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// More house-kit building blocks (Stellar Design Language §4; reference StellarMultiToolPlugin Plugin.Style.cs /
// Plugin.Hub.cs / Plugin.InviteAccept.cs RemoveIcon): embedded art, IconBox, ToolHeader, hub card, glyph pills, the
// round-knob slider, raised list tiles, clickable chips and the icon-only ✕. Shared by every Maestro window.
public sealed partial class Plugin
{
    private const float ToolIconSize = 40f;   // ToolHeader icon
    private const float CardIconSize = 32f;   // hub-card icon (the 2×2 grid's cards are 48 tall, not 56)

    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource and kept: ImageElement Funcs are polled. A missing resource caches null (the element then
    // draws nothing) instead of re-reading every poll.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.Maestro." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // A fixed-size square icon. ImageElement only sets a PREFERRED size and Cell/Selectable force-expand a child's
    // width (the icon-squish gotcha): the fixed-width Cell pins the column, the inner Row keeps the image square.
    private static HudElement IconBox(Func<byte[]?> png, float size) => new CellElement(
        new RowElement(new HudElement[] { new ImageElement(png, (int)size, (int)size) }, Justify: RowJustify.Center),
        Width: size);

    // Window title block: 40 px icon + bold title over a live status line (the hub card and the window header build
    // their status from the same Func, so the two can't drift).
    private HudElement ToolHeader(Func<byte[]?> icon, Func<string> title, HudElement status) => new RowElement(
        new HudElement[]
        {
            IconBox(icon, ToolIconSize),
            new ColumnElement(new HudElement[]
            {
                new TextElement(title, Color: () => TextColor(), Emphasis: true, NoWrap: true),
                status,
            }, Gap: 2f),
        }, Gap: 10f);

    // ── hub card ──────────────────────────────────────────────────────────────────────────────────────────────────
    // The house card (raised tile → Selectable → [icon | bold title over muted status | › ]) at 48 px: a 40 px strut +
    // the selectable's 4 + 4. Bubble OUTSIDE the selectable on purpose (a bubble wrapped IN a selectable gets a
    // full-width chip painted behind it). The text column sits in a weighted Cell so a long status ellipsizes instead
    // of pushing the chevron out of the half-width card.
    private const float HubCardStrut = 40f;

    private HudElement HubCard(Func<byte[]?> icon, Func<string> title, Func<string> status, Action onOpen)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                IconBox(icon, CardIconSize),
                new CellElement(new ColumnElement(new HudElement[]
                {
                    new TextElement(title, Color: () => TextColor(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                    new TextElement(status, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true },
                }, Gap: 1f), Weight: 1f),
                // Bold (styled/TMP path) so FontSize applies and the glyph can't fall back to a taller OS font.
                new TextElement(() => "›", Color: () => MutedColor(), NoWrap: true, FontSize: 18) { Bold = true },
                new SpacerElement(2f, HubCardStrut),   // right margin + the height strut that pins every card to 48
            }, Gap: 8f),
            OnClick: onOpen))
        {
            FillColorFn = () => TileColor(), CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
        };

    // ── glyph pills ───────────────────────────────────────────────────────────────────────────────────────────────
    // A pill whose label is a SYMBOL (◀◀ ▶▶ ▲ ▼ ✕). Same 28 px geometry as PillButton, but the label is PLAIN text, not
    // bold: the TMP bold face lacks the Geometric Shapes block ("■" and "▾" draw blank in bold, KB §6), so ◀ ▶ ▲ ▼ would
    // too. The plain menu font (14 px fixed) draws them, as the old ButtonElement transport did.
    // The label HUGS the glyph (width 0): a fixed label box clips (NoWrap + Width adds a RectMask2D), and the symbol
    // widths of the fallback face aren't known until measured in-game. Pill = glyph + 2×6 strut + 2×6 selectable.
    private const float GlyphWideLabelW  = 0f;    // "◀◀" / "▶▶"
    private const float GlyphLabelW      = 0f;    // "▲" / "▼" / "✕"

    private HudElement GlyphPill(Func<string> glyph, Action onClick, float width, Func<bool>? enabled = null)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillStrutHeight),
                new TextElement(glyph, Color: () => enabled == null || enabled() ? NeutralText() : MutedColor(),
                    Width: width, Align: TextAlign.Center, NoWrap: true),
                new SpacerElement(PillPadX, PillStrutHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (enabled == null || enabled()) onClick(); }))
        {
            FillColorFn = () => enabled == null || enabled() ? NeutralFill() : Mix(PanelColor(), NeutralFill(), 0.5f),
            CornerRadius = PillRadius, PaddingX = 0f, PaddingY = 0f,
        };

    // ── round-knob slider ─────────────────────────────────────────────────────────────────────────────────────────
    // SquareHandle = a knob exactly HandleSize square (the default handle is stretched to the row height = a capsule).
    private const float SliderKnobSize = 14f;

    private static SliderElement RoundSlider(Func<float> get, Action<float> set, float min, float max,
        Func<bool>? enabled = null)
        => new(get, set, min, max, enabled) { SquareHandle = true, HandleSize = SliderKnobSize };

    // ── list tiles ────────────────────────────────────────────────────────────────────────────────────────────────
    // A raised list row: the Tile fill (or a slightly lighter one while `selected`) around `row`; the row holds a strut
    // that pins the tile to ListTileHeight. ⚠️ A VirtualList slot is a column layout that STRETCHES its one child to the
    // full RowHeight (WindowBuilder.VirtualList: slot sizeDelta.y = RowHeight + a VLG), so a 26 px tile in a 28 px slot
    // filled all 28 and the rows touched (in-game 2026-11-06). Each pooled row is therefore ListSlot(tile) = a column of
    // [26 px tile, 2 px transparent spacer] whose preferred height equals the pitch, so nothing is stretched.
    private const float ListTileHeight = 26f;
    private const float ListRowGap     = 2f;
    private const float ListRowPitch   = ListTileHeight + ListRowGap;   // the VirtualList RowHeight

    private static HudElement ListSlot(HudElement tile) => new ColumnElement(new HudElement[]
    {
        tile,
        new SpacerElement(0f, ListRowGap),
    }, Gap: 0.01f);

    // Viewport height that shows exactly `rows` rows: the last row's 2 px gap falls outside, so the list ends flush
    // with a tile.
    private static float ListHeight(int rows) => rows * ListRowPitch - ListRowGap;
    private const float TilePadX       = 6f;
    private const float TileRadius     = 6f;    // raised tiles / hub cards (the panels' rounding)

    private ColorRgba SelectedTileColor() => Over(Menu.MenuText with { A = 0.08f }, TileColor());

    private ColorRgba HoverTileColor() => Over(Menu.MenuText with { A = 0.05f }, TileColor());

    // `hovered` (optional): the row draws its OWN hover (a Bare selectable inside has no wash), so the whole tile —
    // the trailing ✕ included — lights up as one piece.
    private HudElement ListTile(HudElement row, Func<bool>? selected = null, Func<bool>? hovered = null) => new BubbleElement(row)
    {
        FillColorFn = () => selected != null && selected() ? SelectedTileColor()
                          : hovered != null && hovered() ? HoverTileColor() : TileColor(),
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 0f,
    };

    // The selected row's 3 px accent bar at the tile's left edge (transparent when not selected, so nothing moves).
    private HudElement AccentBar(Func<bool> selected) => new BubbleElement(new SpacerElement(3f, ListTileHeight - 6f))
    {
        FillColorFn = () => selected() ? AccentColor() : Transparent, CornerRadius = 2f, PaddingX = 0f, PaddingY = 0f,
    };

    // ── clickable chip ────────────────────────────────────────────────────────────────────────────────────────────
    // A StatusChip that toggles/cycles on click: a Bare selectable (no tint / padding of its own) around the chip, so
    // the on/off geometry stays the chip's 72×24.
    private HudElement ChipToggle(Func<string> label, Func<bool> on, Action onClick, float width = ChipWidth)
        => new SelectableElement(
            StatusChip(label, () => on() ? StatusGreen : (ColorRgba?)null, width), OnClick: onClick) { Bare = true };

    // ── icon-only ✕ ───────────────────────────────────────────────────────────────────────────────────────────────
    // At rest a muted glyph; on hover a red glyph on a soft red rounded wash (house "Remove hover"). ⚠️ Pooled row: the
    // hover flag is keyed per pool SLOT and reset when the slot shows a different item (a ✕ click slides the next row
    // under the pointer; it must not inherit the red state until the pointer re-enters).
    private static readonly ColorRgba RemoveWash  = Rgb(0xff4d4f, 0.22f);
    private static readonly ColorRgba RemoveGlyph = Rgb(0xff8a8b);

    private sealed class HoverSlots
    {
        public readonly bool[] Over;
        public readonly string?[] Id;
        public HoverSlots(int n) { Over = new bool[n]; Id = new string?[n]; }

        public bool Get(int slot, string? id)
        {
            if (id != Id[slot]) { Id[slot] = id; Over[slot] = false; }
            return Over[slot];
        }
    }

    private HudElement RemoveIcon(HoverSlots hover, int slot, Func<string?> id, Action onClick)
        => new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(3f, 18f),
                new TextElement(() => "✕", NoWrap: true,
                    Color: () => hover.Get(slot, id())
                        ? EnsureContrast(RemoveGlyph, Over(RemoveWash, TileColor()), 3f)
                        : MutedColor()),
                new SpacerElement(3f, 18f),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick)
        {
            Bare = true,
            OnHover = over => { hover.Over[slot] = over; hover.Id[slot] = id(); },
        })
        {
            FillColorFn = () => hover.Get(slot, id()) ? RemoveWash : Transparent,
            CornerRadius = 4f, PaddingX = 2f, PaddingY = 2f,
        };

    // A muted body line (footers, hints in a panel).
    private HudElement MutedLine(Func<string> text) => new TextElement(text, Color: () => MutedColor());

    // ONE always-present status line at a fixed height (the Link NOW PLAYING recipe): a 0-width height strut pins the
    // row so bold / empty / warning text never changes the panel height; NoWrap + Ellipsize keeps it on one line.
    private const float StatusStrutHeight = 22f;

    private static HudElement FixedLine(HudElement content) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, StatusStrutHeight),
        new CellElement(content, Weight: 1f),
    }, Gap: 0.01f);
}
