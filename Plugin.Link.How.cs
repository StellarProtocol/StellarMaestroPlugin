using System;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Join view "How it works" panel (approved prototype 2026-11-02, option A): a section with a small muted caption,
// three numbered steps and an "Open website" button + the bare site address. Kept compact because the user asked for
// minimal added height. Restyled to the house SectionPanel + Neutral pill with the 2.28.1 SDK bump
// (the 2.3.0 build had to stand in a PanelElement + classic ButtonElement for them).
public sealed partial class Plugin
{
    // The ONE place the site address lives (button target + the visible text, which drops the scheme).
    private const string MaestroSiteUrl = "https://maestro.stellarresonance.app";

    private HudElement BuildLinkHowPanel() => SectionPanel(() => _loc.T("mst.link.how.title"), null,
        HowStep(1), HowStep(2), HowStep(3),
        new RowElement(new HudElement[]
        {
            new CellElement(NeutralButton(() => _loc.T("mst.link.openSite"), OpenMaestroSite, 106f), Width: 130f),
            new TextElement(() => MaestroSiteUrl.Substring("https://".Length), Color: () => MutedColor(), NoWrap: true),
        }, Gap: 8f));

    // "1. …" as one wrapped text line — a separate number cell would put wrapping text inside a Row.
    private HudElement HowStep(int n)
        => new TextElement(() => $"{n}. {_loc.T("mst.link.how." + n)}", Color: () => TextColor());

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
