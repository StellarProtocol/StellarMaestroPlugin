using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// House style (Stellar Design Language, KB Stellar-Design-Language.md) for the Maestro Link window — copied from the
// reference StellarMultiToolPlugin\Plugin.Style.cs and trimmed to what the Link window uses: theme-live tokens,
// SectionPanel, 28 px pills, PillPair and the fixed-width status chip (72 px here: the Link chips carry longer words
// than MultiTool's 64 px on/off chips). Needs SDK >= 2.26 (BubbleElement.FillColorFn) — Maestro is on 2.28.1.
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it,
//     so small captions are Bold+FontSize while hints stay at the fixed plain size.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → a bold/regular variant is two complete elements swapped by a ConditionalElement
//     wrapping the WHOLE pill, each in the same fixed-width cell so the swap never moves its neighbours.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Derived from _services.Theme.Colors on every call and handed to elements as Funcs, so a theme switch repaints on
    // the next refresh. Only SEMANTIC status colours are fixed (green / amber / red), like channel colours.
    private static readonly ColorRgba PanelShade  = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix     = 0.10f;   // raised tile = panel + 10% MenuText (lighter on dark, darker on light)
    private const float NeutralMix      = 0.22f;   // neutral pill = panel + 22% MenuMuted
    private const float PillMinContrast = 4.5f;    // WCAG AA for the pill labels
    private const float ChipWashAlpha   = 0.22f;   // coloured chip = bold text on a 22% pill of its colour

    private static readonly ColorRgba StatusGreen = Rgb(0x5fd38d);
    private static readonly ColorRgba StatusAmber = Rgb(0xffcc73);   // the house "Join" chip yellow
    private static readonly ColorRgba StatusRed   = Rgb(0xff4d4f);
    private static readonly ColorRgba ProblemText = Rgb(0xff8a8b);   // the house remove-hover glyph red (readable body text)
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    private IThemeMenuColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedColor()  => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()   => Over(Menu.MenuText with { A = TileTextMix }, PanelColor());
    private ColorRgba NeutralFill() => Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor());
    private ColorRgba PrimaryFill() => Menu.MenuAccent;
    private ColorRgba PrimaryText() => EnsureContrast(White, PrimaryFill(), PillMinContrast);
    private ColorRgba NeutralText() => EnsureContrast(TextColor(), NeutralFill(), PillMinContrast);
    // Error body text = the Problem red, kept readable on the surface it sits on (the panel for join errors, the raised
    // tile for a player row's Problem reason): #ff8a8b is ~7.7:1 on the dark themes (unchanged), but too pale on the
    // Light theme, where EnsureContrast walks it toward black into a dark red.
    private ColorRgba ErrorTextColor() => ErrorTextColor(PanelColor());
    private ColorRgba ErrorTextColor(ColorRgba surface) => EnsureContrast(ProblemText, surface, PillMinContrast);

    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque
    // bg (ChatTools Plugin.Tabs.cs → MultiTool Plugin.Style.cs). Pure struct math — no allocation per refresh.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb)
        => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);

    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    private static ColorRgba Rgb(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, a);

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: a fill-tinted Bubble around a SelectableElement, 28 px = a 20 px strut on
    // each side of a bold-13 label + the selectable's 4 + 4 padding (MultiTool's in-game-measured pill). `enabled`
    // (optional) is a Link addition — the house kit has no disabled pill: a disabled pill keeps its geometry, swallows
    // the click, and dims (label → Muted, fill → halfway to the panel), so toggling it never moves anything.
    private const float PillStrutHeight = 20f;
    private const float PillPadX        = 6f;
    private const float PillPairWidth   = 96f;   // label box for two-in-a-row pairs (weighted cells only split
                                                 // equally when both halves prefer the same width)

    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float width = 0f, Func<bool>? enabled = null) => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillStrutHeight),
                new TextElement(label, Color: () => enabled == null || enabled() ? text() : MutedColor(),
                    Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true },
                new SpacerElement(PillPadX, PillStrutHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (enabled == null || enabled()) onClick(); }))
        {
            FillColorFn = () => enabled == null || enabled() ? fill() : Mix(PanelColor(), fill(), 0.5f),
            PaddingX = 0f, PaddingY = 0f,
        };

    private HudElement PrimaryButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null) =>
        PillButton(label, PrimaryFill, PrimaryText, onClick, width, enabled);
    private HudElement NeutralButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null) =>
        PillButton(label, NeutralFill, NeutralText, onClick, width, enabled);

    private static HudElement PillPair(HudElement left, HudElement right) => new RowElement(new HudElement[]
    {
        new CellElement(left,  Weight: 1f),
        new CellElement(right, Weight: 1f),
    }, Gap: 8f);

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): small UPPERCASE caption + right-aligned muted hint (+ an optional
    // trailing control after the hint, e.g. the PLAYERS Hide/Show), then the children. Directly in a Column it FILLS
    // the width (a Bubble only hugs inside a Row).
    //
    // ⚠️ The trailing variant has its OWN NAME on purpose. As an overload SectionPanel(caption, hint, HudElement?
    // trailing, params children) it silently stole the first child of EVERY call: with two params-array overloads both
    // applicable only in expanded form, C# prefers the one with MORE declared parameters (spec §12.6.4.3), so
    // SectionPanel(cap, hint, a, b, c) bound `a` as `trailing` → it was drawn inside the caption row (How step 1, the
    // ROOM code line, the NOW PLAYING title), or dropped outright when there was no caption row (the join panel hint).
    private HudElement SectionPanel(Func<string>? caption, Func<string>? hint, params HudElement[] children)
        => SectionPanelWithTrailing(caption, hint, null, children);

    private HudElement SectionPanelWithTrailing(Func<string>? caption, Func<string>? hint, HudElement? trailing,
        params HudElement[] children)
    {
        var items = new List<HudElement>(children.Length + 1);
        if (caption != null || hint != null)
        {
            var row = new List<HudElement>
            {
                new TextElement(() => caption?.Invoke().ToUpperInvariant() ?? "",
                    Color: () => MutedColor(), NoWrap: true, FontSize: 12) { Bold = true },
                new SpacerElement(0f),   // flexible: pushes the hint to the right edge
                new TextElement(() => hint?.Invoke() ?? "", Color: () => MutedColor(), NoWrap: true),
            };
            if (trailing != null) row.Add(trailing);
            items.Add(new RowElement(row, Gap: 6f));
        }
        items.AddRange(children);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => PanelColor(), CornerRadius = 6f, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // ── status chip ───────────────────────────────────────────────────────────────────────────────────────────────
    // Coloured = bold text in the (semantic) colour on a 22% wash of it; muted = regular MenuMuted text on a faint
    // MenuMuted pill. Two complete pills swapped by a Conditional wrapping the WHOLE pill (never a Conditional inside a
    // bubble's measured child — the bubble re-hug gotcha), both in the same ChipWidth cell with a height strut, so a
    // state change never moves the row (bold text is taller than regular; the strut pins both to 24).
    private const float ChipWidth       = 72f;
    private const float ChipStrutHeight = 22f;

    private HudElement StatusChip(Func<string> label, Func<ColorRgba?> color) => new ConditionalElement(
        () => color() != null,
        ChipPill(new TextElement(label, Color: () => color() ?? MutedColor(), Align: TextAlign.Center, NoWrap: true) { Bold = true },
                 () => (color() ?? MutedColor()) with { A = ChipWashAlpha }),
        ChipPill(new TextElement(label, Color: () => MutedColor(), Align: TextAlign.Center, NoWrap: true),
                 () => MutedColor() with { A = 0.15f }));

    private static HudElement ChipPill(TextElement text, Func<ColorRgba> fill) => new CellElement(
        new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipStrutHeight),   // width 0 + height > 0 = a non-flexible height strut
                text,
            }, Gap: 0.01f, Justify: RowJustify.Center))
        { FillColorFn = () => fill(), PaddingX = 4f, PaddingY = 1f },
        Width: ChipWidth);
}
