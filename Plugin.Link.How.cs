using System;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Join view "How it works" panel (approved prototype 2026-11-02, option A): a framed section with a small muted
// caption, three numbered steps and an "Open website" button + the bare site address. Kept deliberately compact (small
// font, 4 px gaps) because the window width is fixed at 420 and the user asked for minimal added height.
//
// Built from what SDK 2.3.0 has: PanelElement (theme-live framed panel) stands in for the house SectionPanel's
// BubbleElement, and a classic ButtonElement for the neutral pill — neither BubbleElement nor custom fills exist in
// this SDK, and Maestro's other buttons are all classic ButtonElements anyway.
public sealed partial class Plugin
{
    // The ONE place the site address lives (button target + the visible text, which drops the scheme).
    private const string MaestroSiteUrl = "https://maestro.stellarresonance.app";

    private const int HowFontSize = 13;   // one step below body text — the panel is secondary to the join form

    private HudElement BuildLinkHowPanel() => new PanelElement(new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("mst.link.how.title").ToUpperInvariant(), Color: Muted, NoWrap: true, FontSize: 12)
            { Bold = true },
        HowStep(1), HowStep(2), HowStep(3),
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.link.openSite"), OnClick: OpenMaestroSite), Width: 130f),
            new TextElement(() => MaestroSiteUrl.Substring("https://".Length), Color: Muted, NoWrap: true, FontSize: HowFontSize),
        }, Gap: 6f),
    }, Gap: 4f), Padding: 6f);

    // "1. …" as one wrapped text line — a separate number cell would put wrapping text inside a Row.
    private HudElement HowStep(int n)
        => new TextElement(() => $"{n}. {_loc.T("mst.link.how." + n)}", FontSize: HowFontSize);

    // Opens the Maestro Link website in the default browser (same shell-execute pattern as OpenMidiFolder).
    private void OpenMaestroSite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = MaestroSiteUrl, UseShellExecute = true });
        }
        catch (Exception ex) { _services.Log.Warning($"[MaestroLink] open website failed: {ex.Message}"); }
    }
}
