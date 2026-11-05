using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// House style (Stellar Design Language, KB Stellar-Design-Language.md) — the ONE kit every Maestro window builds from
// (main, Settings, Library, Preview, Link). Copied from the reference StellarMultiToolPlugin\Plugin.Style.cs: theme-live
// tokens, SectionPanel, 28 px pills, PillPair and the fixed-width status chip (72 px here: the Link chips carry longer
// words than MultiTool's 64 px on/off chips). Needs SDK >= 2.26 (BubbleElement.FillColorFn) — Maestro is on 2.28.1.
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
    private const float TileTextMix     = 0.08f;   // raised tile = panel + 8% MenuText (lighter on dark, darker on light)
    private const float TileAccentMix   = 0.06f;   // … + a 6% accent tint (user-approved 2026-11-06; MultiTool stays untinted)
    private const float NeutralMix      = 0.22f;   // neutral pill = panel + 22% MenuMuted
    private const float NeutralAccentMix = 0.08f;  // … + an 8% accent tint, like the tiles (user-approved 2026-11-06)
    private const float PillMinContrast = 4.5f;    // WCAG AA for the pill labels
    // Primary (accent) pills only: white labels down to 3:1 (WCAG AA for large/bold text), so the purple theme's
    // #c8508f keeps a WHITE label (≈4.2:1) instead of flipping dark; a very light accent still flips (user-approved
    // 2026-11-06, option B). Neutral / Caution / Danger keep 4.5.
    private const float PrimaryMinContrast = 3.0f;
    private const float ChipWashAlpha   = 0.22f;   // coloured chip = bold text on a 22% pill of its colour

    private static readonly ColorRgba StatusGreen = Rgb(0x5fd38d);
    private static readonly ColorRgba StatusAmber = Rgb(0xffcc73);   // the house "Join" chip yellow
    private static readonly ColorRgba StatusRed   = Rgb(0xff4d4f);
    private static readonly ColorRgba DangerFill  = Rgb(0xff4d4f, 0.85f);   // the house Danger pill (fixed, not themed)
    private static readonly ColorRgba ProblemText = Rgb(0xff8a8b);   // the house remove-hover glyph red (readable body text)
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);

    private IThemeMenuColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedColor()  => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    // Tile carries the theme accent so rows / cards read purple-pink on the purple theme (≈ #3b2e3b, the prototype's
    // #3a2e41) instead of a neutral grey (#353238 measured in-game), and teal on teal.
    private ColorRgba TileColor()   => Over(Menu.MenuAccent with { A = TileAccentMix },
                                            Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    // Neutral carries the accent too (prototype #4a3c52 on the purple theme), so the pills sit in the same family as
    // the tinted tiles instead of reading grey. Hover / disabled / the "?" open tint all derive from this.
    // Hub cards sit directly on the WINDOW background (not in a panel), where the panel-based Tile (#353238 measured)
    // almost matched the window and blended in. Card = window background + 6 % text + 8 % accent: on the purple theme
    // ≈ #41313f over a ≈ #2a232f window (prototype: #3a2e41 cards on #2e2433) — clearly lighter, same accent family.
    private const float CardTextMix   = 0.06f;
    private const float CardAccentMix = 0.08f;
    private ColorRgba CardColor() => Over(Menu.MenuAccent with { A = CardAccentMix },
                                          Over(Menu.MenuText with { A = CardTextMix }, Menu.MenuBackground));
    private ColorRgba NeutralFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                            Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    private ColorRgba PrimaryFill() => Menu.MenuAccent;
    private ColorRgba PrimaryText() => EnsureContrast(White, PrimaryFill(), PrimaryMinContrast);
    private ColorRgba NeutralText() => EnsureContrast(TextColor(), NeutralFill(), PillMinContrast);
    private ColorRgba DangerText()  => EnsureContrast(White, Over(DangerFill, PanelColor()), PillMinContrast);
    // Caution: a TINTED amber pill (the amber chip wash, opaque over the panel) so it reads one step below the SOLID red
    // Danger. The amber label is pushed to 4.5:1 against that fill (darkens on the Light theme).
    private ColorRgba CautionFill() => Over(StatusAmber with { A = ChipWashAlpha }, PanelColor());
    private ColorRgba CautionText() => EnsureContrast(StatusAmber, CautionFill(), PillMinContrast);
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

    // ── hover that follows the shape ──────────────────────────────────────────────────────────────────────────────
    // A normal SelectableElement paints its hover as a SQUARE wash over its rect, which showed as a rectangle on the
    // round pills (in-game 2026-11-06). Every clickable here is therefore a Bare selectable (no wash, no padding of
    // its own) whose OnHover flips a flag, and the surrounding Bubble's FillColorFn shows the hover — same capsule /
    // circle / rounded tile. Hover = MenuText@HoverMix over the fill (lighter on dark themes, darker on Light).
    // There is no pressed tint: the toolkit has no pointer-down callback for a Bare selectable.
    private sealed class HoverFlag { public bool On; }

    private const float HoverMix = 0.10f;
    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: a fill-tinted Bubble around a Bare SelectableElement; 28 px tall = the
    // 28 px struts either side of a bold-13 label, 12 px side padding (the old 6 px strut + the non-Bare selectable's
    // 6). `enabled` (optional): a disabled pill keeps its geometry, swallows the click, and dims (label → Muted,
    // fill → halfway to the panel), so toggling it never moves anything.
    private const float PillHeight      = 28f;
    private const float PillRadius      = PillHeight / 2f - 1f;   // 13 = RoundRadius(28): round ends without growing the 28 px pill

    // The corner radius for a ROUND (capsule / circle) bubble of height `side`: side / 2 − 1, NOT side / 2.
    // Measured cause (in-game 2026-11-06): the bubble's background is a 9-slice Image whose sprite border is radius + 1
    // on every side (framework WindowThemeAssets.BubbleBg), and a sliced Image is itself a layout element reporting a
    // MIN size of the summed borders. The bubble's height = the max of its content's (24 / 28) and that min, so
    // radius side/2 made every round control 2 px taller than its content: the "?" drew 24 × 26 around a 24 × 24
    // selectable, and the 28 px pills drew 30 px (pixel scan of t05_full.png: Play fill rows 512..541). With
    // radius side/2 − 1 the borders sum to exactly `side`, so the bubble is its content's size; the ends stay round
    // (a 1 px straight run each side). Width is not affected (BubbleBinding pins it through a LayoutElement).
    private static float RoundRadius(float side) => side / 2f - 1f;
    private const float PillPadX        = 12f;
    private const float PillPairWidth   = 96f;   // label box for two-in-a-row pairs (weighted cells only split
                                                 // equally when both halves prefer the same width)
    private const float LinkActionLabelW = 150f; // label box of the linked view's bottom pair (half of the 540 window ≈ 250):
                                                 // roomier than PillPairWidth for "Resume my part" + the longer locales

    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick,
        float width = 0f, Func<bool>? enabled = null) => RoundPill(
            DropBold(new TextElement(label, Color: () => enabled == null || enabled() ? text() : MutedColor(),
                Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true }),
            fill, onClick, enabled);

    // Bold (TMP) labels: centre the CAP-HEIGHT band, so a word without descenders ("Rename") has equal space above and
    // below. A centred bold-13 line sits HIGH: measured in-game at UI scale 1 (2026-11-06, 28 px pills) with a 1 px
    // lift, "Rename" / "+ New" / "+ Add" ink spanned rows 7..16 (7 above, 11 below) — i.e. 1.5–2 px high even
    // unlifted. (A first, lifted attempt misread "Play" / "Stop", whose descenders made the ink look low.) A Row
    // centres its children vertically, so a strut can't move the text; instead the label sits in a Column under a
    // 2×BoldLabelDrop spacer, which centres the pair and moves the text DOWN by BoldLabelDrop: caps ≈ 9..18 in the
    // 28 px pill. Canvas units, so it scales with the UI scale like the struts. Pill height unchanged (the column is
    // 2 px taller than the text, far below the 28 px struts).
    private const float BoldLabelDrop = 1f;

    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f * BoldLabelDrop),
        label,
    }, Gap: 0.01f);

    // The shared pill shell (labelled pills + glyph pills): Bubble → Bare Selectable → Row[strut, label, strut].
    private HudElement RoundPill(HudElement label, Func<ColorRgba> fill, Action onClick, Func<bool>? enabled)
    {
        var hover = new HoverFlag();
        bool En() => enabled == null || enabled();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillHeight),
                label,
                new SpacerElement(PillPadX, PillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => !En() ? Mix(PanelColor(), fill(), 0.5f) : hover.On ? HoverFill(fill()) : fill(),
            CornerRadius = PillRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement PrimaryButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null) =>
        PillButton(label, PrimaryFill, PrimaryText, onClick, width, enabled);
    private HudElement NeutralButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null) =>
        PillButton(label, NeutralFill, NeutralText, onClick, width, enabled);
    // Danger: house "destructive confirm" pill — used for Link's Disconnect (user-approved exception 2026-11-06: it
    // interrupts a live shared activity mid-song, so it must not look like the Neutral pill beside it).
    private HudElement DangerButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null) =>
        PillButton(label, () => DangerFill, DangerText, onClick, width, enabled);
    // Caution: stops / pauses only the user's OWN part of a live shared activity (Link "Stop my part").
    private HudElement CautionButton(Func<string> label, Action onClick, float width = 0f, Func<bool>? enabled = null) =>
        PillButton(label, CautionFill, CautionText, onClick, width, enabled);

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
    // state change never moves the row (bold text is taller than regular; the strut pins both to 24). `width` (default
    // ChipWidth) lets a chip whose label varies (Loop: Off / All / One) take the width of its LONGEST label in any
    // language, so cycling it never changes its size.
    private const float ChipWidth       = 72f;
    private const float ChipStrutHeight = 22f;
    private const float ChipRadius      = (ChipStrutHeight + 2f) / 2f - 1f;   // 11 = RoundRadius(24): round ends of the 24 px chip (see RoundRadius)

    private HudElement StatusChip(Func<string> label, Func<ColorRgba?> color, float width = ChipWidth) => new ConditionalElement(
        () => color() != null,
        ChipPill(DropBold(new TextElement(label, Color: () => color() ?? MutedColor(), Align: TextAlign.Center, NoWrap: true) { Bold = true }),
                 () => (color() ?? MutedColor()) with { A = ChipWashAlpha }, width),
        ChipPill(new TextElement(label, Color: () => MutedColor(), Align: TextAlign.Center, NoWrap: true),
                 () => MutedColor() with { A = 0.15f }, width));

    private static HudElement ChipPill(HudElement text, Func<ColorRgba> fill, float width) => new CellElement(
        new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipStrutHeight),   // width 0 + height > 0 = a non-flexible height strut
                text,
            }, Gap: 0.01f, Justify: RowJustify.Center))
        { FillColorFn = () => fill(), CornerRadius = ChipRadius, PaddingX = 4f, PaddingY = 1f },
        Width: width);
}
