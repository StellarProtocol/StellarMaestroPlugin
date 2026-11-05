using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Maestro Link window. ONE window, two faces:
//   • unlinked → the join form (code + Join, Connecting / error lines, consent text) + "How it works";
//   • linked   → the Link status view, which REPLACES the main Maestro window while linked (the launcher tile
//                opens this instead — see Plugin.cs OnOpen). Disconnect hands the main window back (EndLink).
// Look = the Stellar Design Language (approved design 2026-11-05; helpers in Plugin.Link.Style.cs): ROOM + NOW PLAYING
// panels side by side, the PLAYERS panel (Plugin.Link.Players.cs), then a Neutral pill pair. Window width 540.
// Opening from hidden remounts on top within its ZCat, so no BringToFront (Rule 5).
public sealed partial class Plugin
{
    private IWindowControl _linkWindow = null!;
    private IWindowControl _mainWindow = null!;

    // Two equal top panels: (540 − 2×12 body − 8 gap) / 2. Fixed widths, because weighted cells only split equally
    // when both children PREFER the same width (WindowBuilder-Patterns.md) and a long song title would widen one side.
    private const float LinkTopPanelW = 254f;
    private const float LinkLabelW    = 56f;   // "My part" / "Next" label column

    private HudElement BuildLinkRoot() => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => IsLinked, BuildLinkStatusView(), BuildLinkJoinView()),
    }, Gap: 0f);

    private void OpenLinkWindow() => _linkWindow.SetVisible(true);

    // ---- unlinked: join form ----

    private HudElement BuildLinkJoinView() => new ColumnElement(new HudElement[]
    {
        SectionPanel(null, null,
            new TextElement(() => _loc.T("mst.link.hint"), Color: () => MutedColor()),
            new RowElement(new HudElement[]
            {
                // Sanitized as typed (uppercase, code alphabet, max 6) — Get re-seeds the field with the cleaned value.
                new CellElement(new InputElement(
                    Get:      () => _lkCode,
                    Submit:   s => { _lkCode = SanitizeCode(s); JoinLink(); },
                    Width:    180f,
                    OnChange: s => _lkCode = SanitizeCode(s)), Weight: 1f),
                new CellElement(PrimaryButton(
                    () => _lkPhase == LinkPhase.Connecting ? _loc.T("mst.link.joining") : _loc.T("mst.link.join"),
                    JoinLink, PillPairWidth, CanJoin), Width: 120f),
            }, Gap: 8f),
            new ConditionalElement(() => _lkPhase == LinkPhase.Connecting,
                new TextElement(() => _loc.T("mst.link.connecting"), Color: () => MutedColor())),
            new ConditionalElement(() => _lkPhase == LinkPhase.Off && _lkErrorKey.Length > 0,
                // Red (not muted): a failed join must stand out.
                new TextElement(() => _lkErrorKey.Length > 0 ? _loc.T(_lkErrorKey) : "", Color: () => ErrorTextColor())),
            new TextElement(() => _loc.T("mst.link.consent"), Color: () => MutedColor())),
        BuildLinkHowPanel(),   // Plugin.Link.How.cs
    }, Gap: 8f);

    // ---- linked: status view ----

    private HudElement BuildLinkStatusView() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            LinkTopPanel(BuildLinkRoomPanel()),
            LinkTopPanel(BuildLinkNowPlayingPanel()),
        }, Gap: 8f),
        BuildLinkPlayersPanel(),   // Plugin.Link.Players.cs
        PillPair(
            // Stop / Resume share the slot: two complete pills swapped by a Conditional around the WHOLE pill, both with
            // the same label box, so the swap never moves anything (Primary "Resume" vs amber "Stop", faded when off).
            new ConditionalElement(() => LinkPartButtonState() == LinkPartButton.Resume,
                PrimaryButton(() => _loc.T("mst.link.resumePart"), ResumeMyPart, LinkActionLabelW),
                CautionButton(() => _loc.T("mst.link.stopPart"), StopMyPart, LinkActionLabelW,
                    () => LinkPartButtonState() == LinkPartButton.Stop)),
            DangerButton(() => _loc.T("mst.link.disconnect"), () => EndLink(""), LinkActionLabelW)),
    }, Gap: 8f);

    // One fixed-width top cell whose panel stretches to the row's height, so ROOM and NOW PLAYING are equally tall
    // (NOW PLAYING is the taller one: its status line is always present — LinkStatusLine). A Row never stretches a
    // child's height on its own (childForceExpandHeight = false); a Fill Conditional is the one element that reports flexibleHeight, which makes
    // the Row grow the cell to the row height and the cell + Cond stretch the bubble down to it (always-true When).
    private static HudElement LinkTopPanel(HudElement panel)
        => new CellElement(new ConditionalElement(() => true, panel, Fill: true), Width: LinkTopPanelW);

    // ROOM: ● code · room name / My part.
    private HudElement BuildLinkRoomPanel() => SectionPanel(() => _loc.T("mst.link.cap.room"), LinkPingText,
        new RowElement(new HudElement[]
        {
            new TextElement(() => "●", Color: () => _lkOnline ? StatusGreen : _services.Theme.Colors.Warning, NoWrap: true),
            new TextElement(() => _lkRoomCode, Color: () => TextColor(), NoWrap: true) { Bold = true },
            new CellElement(new TextElement(() => _lkRoomName.Length > 0 ? "· " + _lkRoomName : "",
                Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
        }, Gap: 6f),
        LinkLine(() => _loc.T("mst.link.myPart"), LinkPartText));

    // NOW PLAYING: title (or "Waiting for the room…") / thin progress bar / Next / ONE always-present status line.
    private HudElement BuildLinkNowPlayingPanel() => SectionPanel(() => _loc.T("mst.link.cap.nowPlaying"), LinkTimeText,
        new ConditionalElement(() => _lkRoomSong?.Title is { Length: > 0 },
            new TextElement(() => _lkRoomSong?.Title ?? "", Color: () => TextColor(), NoWrap: true) { Bold = true, Ellipsize = true },
            new TextElement(() => _loc.T("mst.link.waiting"), Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }),
        // Default-style bar = a rounded recessed track (black @0.38) + a left-anchored fill; FillColorFn keeps the fill
        // on the live theme accent (the static White is only the build-time placeholder — no theme read while the
        // plugin constructs). No label → just the track.
        new BarElement(LinkProgressFrac, White) { Height = 5f, FillWidth = true, FillColorFn = () => AccentColor() },
        LinkLine(() => _loc.T("mst.link.next"), LinkNextText),
        LinkStatusLine());

    // The status line: ALWAYS present at a fixed height, so the window never jumps when the count-in starts / ends, the
    // room pauses or a warning appears (house rule: fixed geometry). It used to be three Conditional lines — one of
    // them a 22 px count-in number — that each added height while they applied. Shows the highest priority of:
    //   1. count-in "Starting in {n}…" (bold, accent)  2. "Paused" (bold, muted)  3. LinkWarningText (Warning)  4. empty.
    // A 0-width height strut pins the row (width 0 + height > 0 = non-flexible strut, the chip recipe): bold text is
    // taller than regular, and an empty line would otherwise collapse to 0. NoWrap + Ellipsize keeps a long warning
    // (e.g. "Switching to …") on that one line instead of wrapping to two.
    private const float LinkStatusStrutHeight = 22f;   // = ChipStrutHeight: holds a bold-14 line

    private HudElement LinkStatusLine() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, LinkStatusStrutHeight),
        new CellElement(new ConditionalElement(() => LinkCountInSec() > 0,
            new TextElement(() => _loc.TFormat("mst.link.countIn", LinkCountInSec()), Color: () => AccentColor(), NoWrap: true)
                { Bold = true, Ellipsize = true },
            new ConditionalElement(() => _lkPaused && _lkRoomSong != null,
                new TextElement(() => _loc.T("mst.link.paused"), Color: () => MutedColor(), NoWrap: true) { Bold = true, Ellipsize = true },
                new TextElement(LinkWarningText, Color: () => _services.Theme.Colors.Warning, NoWrap: true) { Ellipsize = true })),
            Weight: 1f),
    }, Gap: 0.01f);

    // Muted label + value on one line (value ellipsized to the panel).
    private HudElement LinkLine(Func<string> label, Func<string> value) => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(label, Color: () => MutedColor(), NoWrap: true) { Ellipsize = true }, Width: LinkLabelW),
        new CellElement(new TextElement(value, Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
    }, Gap: 6f);

    private string LinkPingText()
        => !_lkOnline ? _loc.T("mst.link.reconnecting") : _lkPingMs >= 0 ? _loc.TFormat("mst.link.pingHint", _lkPingMs) : "";

    // "1:22 / 4:21" — blank with no song running.
    private string LinkTimeText()
        => _lkRoomSong == null ? "" : $"{Mmss(LinkPositionMs())} / {Mmss(_lkRoomSong.DurationMs)}";

    // "Piano · Acoustic" — toneId 0 / unknown → the instrument's default tone.
    private string LinkEquipName(int inst, int tone)
    {
        string t = LinkToneName(tone != 0 ? tone : DefaultTone(inst));
        return t.Length > 0 ? $"{LinkInstrumentName(inst)} · {t}" : LinkInstrumentName(inst);
    }

    // Part row: the song's RESOLVED instrument · tone (batch 1) — the running song, else the prepared next one; with
    // no song context, what the character really holds.
    private string LinkPartText()
    {
        var s = _lkRoomSong ?? _lkPrepSong;
        if (s?.InstrumentId is int si) return LinkEquipName(si, s.ToneId ?? 0);
        int inst = _lkInstId;   // what is REALLY held (settings defaults are web pre-fill only)
        if (inst <= 0) return "—";
        int k = KnownTone();   // live tone unknown → instrument only (never a guessed tone)
        return k != 0 ? LinkEquipName(inst, k) : LinkInstrumentName(inst);
    }

    // Next: the live `next` message if one arrived since the last play / welcome, else the running song's `next`, else
    // the prepared (upcoming) song (LinkNext.Select). Set in HandleLinkMessage, cleared on JoinLink / EndLink.
    private bool    _lkNextLive;
    private string? _lkNextTitle;

    private void ClearLinkNext() { _lkNextLive = false; _lkNextTitle = null; }

    private string LinkNextText()
    {
        string? n = LinkNext.Select(_lkNextLive, _lkNextTitle, _lkRoomSong != null, _lkRoomSong?.Next, _lkPrepSong?.Title);
        return string.IsNullOrEmpty(n) ? "—" : n;
    }

    private float LinkProgressFrac()
    {
        int d = _lkRoomSong?.DurationMs ?? 0;
        return d > 0 ? Math.Clamp((float)LinkPositionMs() / d, 0f, 1f) : 0f;
    }

    // Seconds until this play's first position (P) sounds at T + P; 0 = not counting in.
    private int LinkCountInSec()
    {
        var s = _lkRoomSong;
        long now = LinkServerNowMs();
        if (s == null || _lkPaused || now <= 0 || now >= LinkStartInstant(s)) return 0;
        return (int)Math.Ceiling((LinkStartInstant(s) - now) / 1000.0);
    }

    // One warning line, most actionable first. The warning list itself is the 1 Hz cache built for the status message.
    private string LinkWarningText()
    {
        if (_lkEquipBusy) return _loc.TFormat("mst.link.switching", LinkEquipName(_lkEquipTarget.Inst, _lkEquipTarget.Tone));
        string k =
            _lkWarnings.Contains("no_instrument")    ? "mst.link.warn.noInstrument" :
            _lkWarnings.Contains("band_view_closed") ? "mst.link.warn.bandView" :
            _lkWarnings.Contains("not_buffered")     ? "mst.link.warn.notBuffered" :
            _lkWarnings.Contains("not_synced")       ? "mst.link.warn.notSynced" :
            _lkStoppedByPlayer                       ? "mst.link.warn.stopped" : "";
        return k.Length > 0 ? _loc.T(k) : "";
    }
}
