using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The main "Maestro" window (house style, approved design 2026-11-06): ToolHeader with the live play state, the NOW
// PLAYING panel (title, round-knob seek, transport, one fixed-height status line), the QUEUE panel
// (Plugin.Main.Queue.cs) and a 2×2 grid of hub cards opening the child windows. The live-text decisions are the pure
// MaestroUi.cs (unit-tested); this file only turns them into strings and colours.
public sealed partial class Plugin
{
    private HudElement BuildBandRoot() => new ColumnElement(new HudElement[]
    {
        ToolHeader(() => CachedPng("maestro-icon.png"), () => _loc.T("mst.title"),
            new TextElement(MainHeaderText, Color: MainHeaderColor, NoWrap: true) { Ellipsize = true }),
        BuildNowPlayingPanel(),
        BuildQueuePanel(),   // Plugin.Main.Queue.cs
        BuildHub(),
    }, Gap: 8f);

    // ── header status ─────────────────────────────────────────────────────────────────────────────────────────────
    private MainHeaderKind HeaderKind()
        => MainHeader.Pick(_bandPlayer.IsPlaying, _bandPlayer.IsPaused, _bandPlayer.CountInRemainMs, _ensembleWaiting);

    private string BandTimeText() => $"{Mmss(_bandPlayer.PositionMs)} / {Mmss(_bandPlayer.DurationMs)}";
    private string CountInText()  => _loc.TFormat("mst.play.countIn", (_bandPlayer.CountInRemainMs / 1000.0).ToString("F1"));

    private string MainHeaderText() => HeaderKind() switch
    {
        MainHeaderKind.Playing         => _loc.TFormat("mst.hdr.playing", BandTimeText()),
        MainHeaderKind.Paused          => _loc.TFormat("mst.hdr.paused", BandTimeText()),
        MainHeaderKind.CountIn         => CountInText(),
        MainHeaderKind.EnsembleWaiting => _loc.T("mst.play.waitEnsemble"),
        _                              => _loc.T("mst.hdr.stopped"),
    };

    private ColorRgba? MainHeaderColor() => HeaderKind() switch
    {
        MainHeaderKind.Playing => StatusGreen,
        MainHeaderKind.CountIn => AccentColor(),
        _                      => MutedColor(),
    };

    // ── NOW PLAYING ───────────────────────────────────────────────────────────────────────────────────────────────
    private const float TimeColW   = 92f;   // "12:34 / 56:78"
    private const float StopLabelW = 60f;   // Caution "Stop" pill = 60 + 2×6 strut + 2×6 selectable = 84 px

    private bool HasBandSong() => (_bandPlayer.IsPlaying || _bandPlayer.IsPaused) && _bandSongTitle.Length > 0;

    private HudElement BuildNowPlayingPanel() => SectionPanel(() => _loc.T("mst.link.cap.nowPlaying"), NotesHint,
        new ConditionalElement(HasBandSong,
            new TextElement(() => _bandSongTitle, Color: () => TextColor(), NoWrap: true) { Bold = true, Ellipsize = true },
            new TextElement(() => "—", Color: () => MutedColor(), NoWrap: true) { Bold = true }),
        new RowElement(new HudElement[]
        {
            new CellElement(RoundSlider(ProgressFrac, SeekFrac, 0f, 1f), Weight: 1f),
            new CellElement(new TextElement(BandTimeText, Color: () => TextColor(), Align: TextAlign.Right, NoWrap: true), Width: TimeColW),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            GlyphPill(() => "◀◀", PlayPrev, GlyphWideLabelW),
            new CellElement(PrimaryButton(PlayPauseLabel, PlayPause), Weight: 1f),
            GlyphPill(() => "▶▶", () => PlayNext(false), GlyphWideLabelW),
            CautionButton(() => _loc.T("mst.stop"), StopPlayback, StopLabelW),
        }, Gap: 6f),
        FixedLine(new TextElement(MainLineText, Color: MainLineColor, NoWrap: true) { Ellipsize = true }));

    private string NotesHint() => _bandPlayer.IsPlaying || _bandPlayer.IsPaused
        ? _loc.TFormat("mst.play.notes", _bandPlayer.SentNotes, _bandPlayer.SongNotes) : "";

    // ── the NOW PLAYING status line ───────────────────────────────────────────────────────────────────────────────
    private MainLineKind MainLineKindNow() => MainStatusLine.Pick(
        countIn:          _bandPlayer.IsPlaying && _bandPlayer.CountInRemainMs > 0,
        ensembleWaiting:  _ensembleWaiting,
        gapCountdown:     _gapRemainMs >= 0,
        hasMessage:       _bandStatusAtMs >= 0 && _bandMidiStatus.Length > 0,
        messageIsProblem: _bandStatusProblem,
        messageAgeMs:     _bandStatusAtMs >= 0 ? Environment.TickCount64 - _bandStatusAtMs : -1,
        hasNext:          NextQueueIndex() >= 0);

    private int NextQueueIndex() => _bandPlayer.IsPlaying || _bandPlayer.IsPaused
        ? QueueNext.Index(_nowPlaying, PlayingSongs.Count, _loopMode, _shuffle, _autoAdvance) : -1;   // the PLAYING list

    private string MainLineText()
    {
        switch (MainLineKindNow())
        {
            case MainLineKind.CountIn:         return CountInText();
            case MainLineKind.EnsembleWaiting: return _loc.T("mst.play.waitEnsemble");
            case MainLineKind.NextIn:          return _loc.TFormat("mst.nextIn", (int)Math.Ceiling(_gapRemainMs / 1000.0));
            case MainLineKind.Problem:
            case MainLineKind.Message:         return _bandMidiStatus;
            case MainLineKind.Next:
                int n = NextQueueIndex();
                return n >= 0 ? _loc.TFormat("mst.next", System.IO.Path.GetFileNameWithoutExtension(PlayingSongs[n])) : "";
            default:                           return "";
        }
    }

    private ColorRgba? MainLineColor() => MainLineKindNow() switch
    {
        MainLineKind.CountIn => AccentColor(),
        MainLineKind.Problem => _services.Theme.Colors.Warning,
        MainLineKind.Message => TextColor(),
        _                    => MutedColor(),
    };

    // ── hub: 2×2 cards ────────────────────────────────────────────────────────────────────────────────────────────
    // Two fixed-width cells per row: (460 window − 2×12 body − 8 gap) / 2. Fixed, because weighted cells only split
    // equally when both cards PREFER the same width (WindowBuilder-Patterns.md), and the status text differs.
    private const float HubCardW = 214f;

    private HudElement BuildHub() => new ColumnElement(new HudElement[]
    {
        HubRow(
            HubCard(() => CachedPng("icon-library.png"), () => _loc.T("mst.hub.library"), () => SongCountText(_bandSongs.Length), OpenLibrary),
            HubCard(() => CachedPng("icon-link.png"), () => _loc.T("mst.win.link"), LinkCardText, OpenLinkWindow)),
        HubRow(
            HubCard(() => CachedPng("icon-preview.png"), () => _loc.T("mst.hub.preview"), PreviewCardText, () => _previewCenterer.OpenCentred()),
            HubCard(() => CachedPng("icon-settings.png"), () => _loc.T("mst.hub.settings"), SettingsCardText, () => _settingsCenterer.OpenCentred())),
    }, Gap: 8f);

    private static HudElement HubRow(HudElement left, HudElement right) => new RowElement(new HudElement[]
    {
        new CellElement(left,  Width: HubCardW),
        new CellElement(right, Width: HubCardW),
    }, Gap: 8f);

    private string SongCountText(int n) => n == 1 ? _loc.T("mst.q.count1") : _loc.TFormat("mst.q.count", n);

    private string LinkCardText() => LinkCard.Pick(_lkPhase == LinkPhase.Connecting, IsLinked, _lkOnline) switch
    {
        LinkCardKind.InRoom       => _loc.TFormat("mst.hub.inRoom", _lkRoomCode),
        LinkCardKind.Reconnecting => _loc.T("mst.link.reconnecting"),
        LinkCardKind.Joining      => _loc.T("mst.link.joining"),
        _                         => _loc.T("mst.hub.notInRoom"),
    };

    private string PreviewCardText() => _previewSynth.IsPlaying || _previewSynth.IsPaused
        ? _loc.T("mst.hub.previewing") : _loc.T("mst.hub.previewSub");

    // Short enough for the half-width card in every language: "Sync on" at the default tempo, "75% · Sync on" otherwise
    // (the old "Tempo 100% · Sync on" was cut off in-game).
    private string SettingsCardText()
    {
        string sync = _loc.T(_bandNetPrebuffer ? "mst.hub.syncOn" : "mst.hub.syncOff");
        return _bandTempoPct == 100 ? sync : _loc.TFormat("mst.hub.settingsSub", _bandTempoPct, sync);
    }

    // ── opening child windows ─────────────────────────────────────────────────────────────────────────────────────
    // House rule (Stellar-Design-Language §5): a child opens centred on the window it was opened from, and in front.
    // OpenCentred ALWAYS fronts (ZFront is sticky across hide/show — CLAUDE.md Rule 5 exception), so every other open
    // of a Maestro window (the launcher tile, the Link hand-over in Plugin.Link.cs) fronts too.
    private WindowCenterer _libraryCenterer  = null!;
    private WindowCenterer _settingsCenterer = null!;
    private WindowCenterer _previewCenterer  = null!;
    private WindowCenterer _linkCenterer     = null!;

    private void InitCenterers()
    {
        var onMain = WindowCenterer.CentreOf(_mainWindow);
        _libraryCenterer  = new WindowCenterer(_services.Framework, _libraryWindow,  onMain);
        _settingsCenterer = new WindowCenterer(_services.Framework, _settingsWindow, onMain);
        _previewCenterer  = new WindowCenterer(_services.Framework, _previewWindow,  onMain);
        _linkCenterer     = new WindowCenterer(_services.Framework, _linkWindow,     onMain);
    }

    private void DisposeCenterers()
    {
        foreach (var c in new[] { _libraryCenterer, _settingsCenterer, _previewCenterer, _linkCenterer })
            try { c?.Stop(); } catch { }
    }

    // Show + front without re-centring (the launcher tile, the Link hand-over): the window keeps its own position.
    private static void ShowInFront(IWindowControl w)
    {
        w.SetVisible(true);
        w.BringToFront();
    }
}
