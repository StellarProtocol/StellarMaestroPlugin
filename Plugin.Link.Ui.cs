using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Maestro Link window (approved prototype 2026-10-28). ONE window, two faces:
//   • unlinked → the join form (code + Join, Connecting / error lines, consent text);
//   • linked   → the compact Link status view, which REPLACES the main Maestro window while linked (the launcher tile
//                opens this instead — see Plugin.cs OnOpen). Disconnect hands the main window back (EndLink).
// Opening from hidden remounts on top within its ZCat, so no BringToFront (Rule 5).
public sealed partial class Plugin
{
    private IWindowControl _linkWindow = null!;
    private IWindowControl _mainWindow = null!;

    private static readonly ColorRgba LinkDotOnline = new(0.36f, 0.84f, 0.52f, 1f);

    private ColorRgba? Muted() => _services.Theme.Colors.TextMuted;
    private ColorRgba? Warn()  => _services.Theme.Colors.Warning;

    private HudElement BuildLinkRoot() => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => IsLinked, BuildLinkStatusView(), BuildLinkJoinView()),
    }, Gap: 0f);

    private void OpenLinkWindow() => _linkWindow.SetVisible(true);

    // ---- unlinked: join form ----

    private HudElement BuildLinkJoinView() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("mst.link.hint"), Color: Muted),
        new RowElement(new HudElement[]
        {
            // Sanitized as typed (uppercase, code alphabet, max 6) — Get re-seeds the field with the cleaned value.
            new CellElement(new InputElement(
                Get:      () => _lkCode,
                Submit:   s => { _lkCode = SanitizeCode(s); JoinLink(); },
                Width:    180f,
                OnChange: s => _lkCode = SanitizeCode(s)), Weight: 1f),
            new CellElement(new ButtonElement(
                Label:   () => _lkPhase == LinkPhase.Connecting ? _loc.T("mst.link.joining") : _loc.T("mst.link.join"),
                OnClick: JoinLink,
                Enabled: CanJoin,
                Style:   MenuButtonStyle.Filled), Width: 120f),
        }, Gap: 6f),
        new ConditionalElement(() => _lkPhase == LinkPhase.Connecting,
            new TextElement(() => _loc.T("mst.link.connecting"), Color: Muted)),
        new ConditionalElement(() => _lkPhase == LinkPhase.Off && _lkErrorKey.Length > 0,
            new TextElement(() => _lkErrorKey.Length > 0 ? _loc.T(_lkErrorKey) : "", Color: Warn)),
        new TextElement(() => _loc.T("mst.link.consent"), Color: Muted),
    }, Gap: 8f);

    // ---- linked: status view ----

    private HudElement LinkRow(Func<string> label, Func<string> value) => new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(label, Color: Muted), Width: 56f),
        new CellElement(new TextElement(value, NoWrap: true), Weight: 1f),
    }, Gap: 6f);

    private HudElement BuildLinkStatusView() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => "●", Color: () => _lkOnline ? LinkDotOnline : _services.Theme.Colors.Warning), Width: 16f),
            new TextElement(() => _loc.TFormat("mst.link.room", _lkRoomCode), Emphasis: true),
            new SpacerElement(),
            new TextElement(LinkPingText, Color: Muted),
        }, Gap: 6f),
        LinkRow(() => _loc.T("mst.link.part"), LinkPartText),
        new ConditionalElement(() => LinkResting, new TextElement(() => _loc.T("mst.link.resting"), Color: Muted)),
        LinkRow(() => _loc.T("mst.song"), () => _lkRoomSong?.Title is { Length: > 0 } t ? t : _loc.T("mst.link.waiting")),
        new RowElement(new HudElement[]
        {
            new CellElement(new SliderElement(Get: LinkProgressFrac, Set: _ => { }, Min: 0f, Max: 1f, Enabled: () => false), Weight: 1f),
            new CellElement(new TextElement(() => $"{Mmss(LinkPositionMs())} / {Mmss(_lkRoomSong?.DurationMs ?? 0)}"), Width: 92f),
        }, Gap: 6f),
        LinkRow(() => _loc.T("mst.link.next"), LinkNextText),
        new ConditionalElement(() => LinkCountInSec() > 0,
            new TextElement(() => _loc.TFormat("mst.link.countIn", LinkCountInSec()), Emphasis: true, Align: TextAlign.Center, FontSize: 22)),
        new ConditionalElement(() => _lkPaused && _lkRoomSong != null,
            new TextElement(() => _loc.T("mst.link.paused"), Emphasis: true, Align: TextAlign.Center)),
        new ConditionalElement(() => LinkWarningText().Length > 0,
            new TextElement(LinkWarningText, Color: Warn)),
        new SeparatorElement(),
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => "■ " + _loc.T("mst.link.stopPart"), OnClick: StopMyPart,
                                              Enabled: () => _lkPartActive), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.link.disconnect"), OnClick: () => EndLink("")), Weight: 1f),
        }, Gap: 6f),
    }, Gap: 8f);

    private string LinkPingText()
        => !_lkOnline ? _loc.T("mst.link.reconnecting") : _lkPingMs >= 0 ? _loc.TFormat("mst.link.ping", _lkPingMs) : "";

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

    // Next: the running song's `next`; with nothing running, the prepared (upcoming) song.
    private string LinkNextText()
    {
        string? n = _lkRoomSong != null ? _lkRoomSong.Next : _lkPrepSong?.Title;
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
        long now = _services.CombatSnapshot.ServerNowMs;
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
