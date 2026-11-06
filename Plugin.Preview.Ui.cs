using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The Preview window (house style, approved design 2026-11-06): PREVIEWING (selected queue song, round-knob seek,
// Play/Pause + Stop, one fixed-height status line), SYNC WITH BAND (Instrument Sync + offset), and STEMS (one raised
// row per stem file; the file-naming guide lives behind the panel's "?"). Wiring/logic is Plugin.Preview.cs.
public sealed partial class Plugin
{
    private const float PvOffsetLabelW  = 96f;   // shorter than SliderLabelW: the row also holds the "= Band delay" pill
    // Stem row widths: the NAME takes whatever is left (it was a fixed 72 px and cut "Piano pla…"): tile inner 404 −
    // toggle 30 − instrument 80 − Sustain pill 108 − toggle 30 − "Tone" 32 − 5 gaps × 4 ≈ 104 px for the name.
    private const float StemInstW       = 80f;   // Pill dropdown (total width; "Guitar" + ▾ still fits)
    private const float StemSustainLblW = 84f;   // "Sustain: Hold" label box → a 108 px pill
    private const float StemToneLblW    = 32f;

    private HudElement BuildPreviewRoot() => new ColumnElement(new HudElement[]
    {
        SectionPanel(() => _loc.T("mst.cap.previewing"), () => _loc.T("mst.pv.fromQueue"),
            new ConditionalElement(() => PreviewSongTitle().Length > 0,
                new TextElement(PreviewSongTitle, Color: () => TextColor(), NoWrap: true) { Bold = true, Ellipsize = true },
                new TextElement(() => "—", Color: () => MutedColor(), NoWrap: true) { Bold = true }),
            new RowElement(new HudElement[]
            {
                new CellElement(RoundSlider(PreviewProgressFrac, PreviewSeekFrac, 0f, 1f), Weight: 1f),
                new CellElement(new TextElement(() => $"{Mmss(_previewSynth.PositionMs)} / {Mmss(_previewSynth.DurationMs)}",
                    Color: () => TextColor(), Align: TextAlign.Right, NoWrap: true), Width: TimeColW),
            }, Gap: 6f),
            new RowElement(new HudElement[]
            {
                new CellElement(PrimaryButton(PreviewPlayLabel, PreviewPlayPause), Weight: 1f),
                CautionButton(() => _loc.T("mst.stop"), StopPreview, StopLabelW),
            }, Gap: 6f),
            FixedLine(new TextElement(PreviewLineText, Color: PreviewLineColor, NoWrap: true) { Ellipsize = true })),

        SectionPanel(() => _loc.T("mst.cap.syncBand"), null,
            HelpToggle("pv_sync", () => _previewSync, SetPreviewSync,
                () => _loc.T("mst.pv.instSync"), () => _loc.T("mst.pv.instSync.help")),
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => _loc.T("mst.pv.offset"), Color: () => TextColor(), NoWrap: true)
                    { Ellipsize = true }, Width: PvOffsetLabelW),
                new CellElement(RoundSlider(() => _previewSyncOffsetMs, v => SetPreviewSyncOffset((int)MathF.Round(v)), 0f, 2000f), Weight: 1f),
                new CellElement(new TextElement(() => $"{_previewSyncOffsetMs}ms", Color: () => TextColor(),
                    Align: TextAlign.Right, NoWrap: true), Width: SliderValueW),
                NeutralButton(() => _loc.T("mst.pv.bandDelay"), () => SetPreviewSyncOffset(ReadBandDelayMs())),
                HelpDot("pv_offset", () => _loc.T("mst.pv.offset"), () => _loc.T("mst.pv.offset.desc")),
            }, Gap: 6f)),

        SectionPanelWithTrailing(() => _loc.T("mst.cap.stems"), StemsHint,
            HelpDot("pv_naming", () => _loc.T("mst.pv.fileNaming"), FileNamingHelp),
            new ConditionalElement(() => PreviewStems().Count == 0,
                MutedLine(() => _loc.T("mst.pv.noStemsHint")),
                new ListElement(
                    VisibleCount: () => Math.Min(PreviewStems().Count, PreviewSlotCount),
                    Slots:        BuildPreviewSlots()))),
    }, Gap: 8f);

    // ── PREVIEWING ────────────────────────────────────────────────────────────────────────────────────────────────
    private string PreviewSongTitle()
    {
        var path = CurrentPreviewSongPath();
        return path.Length > 0 ? StemBase(System.IO.Path.GetFileNameWithoutExtension(path)) : "";
    }

    // Plain words: the label sits in a bold pill, whose face has no ▶ / ❚❚ / ⟳ glyphs.
    private string PreviewPlayLabel()
    {
        if (_previewSynth.IsPlaying)
        {
            if (_previewSynth.IsSyncing) return _previewSynth.SyncWaiting ? _loc.T("mst.pv.waitingBand") : _loc.T("mst.pv.syncedStop");
            return _loc.T("mst.pause");
        }
        if (_previewSynth.IsPaused) return _loc.T("mst.resume");
        return _previewSync ? _loc.T("mst.pv.syncToBand") : _loc.T("mst.pv.preview");
    }

    private string PreviewKeysText() => string.Join(" · ", _previewSynth.LoadedKeys.Select(Cap));

    // The status line: synced / waiting for the band → what's loaded while playing → a fresh problem from the last
    // Preview press (no stems, missing file…) → the stopped hint.
    private enum PvLine { Waiting, Synced, Loaded, Problem, Hint }

    private PvLine PreviewLine()
    {
        if (_previewSynth.IsSyncing) return _previewSynth.SyncWaiting ? PvLine.Waiting : PvLine.Synced;
        if (_previewSynth.IsPlaying || _previewSynth.IsPaused) return PvLine.Loaded;
        bool freshProblem = _bandStatusProblem && _bandStatusAtMs >= 0
                            && Environment.TickCount64 - _bandStatusAtMs < MainStatusLine.ProblemShowMs;
        return freshProblem ? PvLine.Problem : PvLine.Hint;
    }

    private string PreviewLineText() => PreviewLine() switch
    {
        PvLine.Waiting => _loc.T("mst.pv.waitBand"),
        PvLine.Synced  => _loc.T("mst.pv.synced"),
        PvLine.Loaded  => _loc.TFormat("mst.pv.loaded", PreviewKeysText()),
        PvLine.Problem => _bandMidiStatus,
        _              => _loc.T("mst.pv.stoppedHint"),
    };

    private ColorRgba? PreviewLineColor() => PreviewLine() switch
    {
        PvLine.Synced  => StatusGreen,
        PvLine.Loaded  => TextColor(),
        PvLine.Problem => _services.Theme.Colors.Warning,
        _              => MutedColor(),
    };

    // ── STEMS ─────────────────────────────────────────────────────────────────────────────────────────────────────
    private string StemsHint()
    {
        int n = PreviewStems().Count;
        return n == 0 ? "" : n == 1 ? _loc.T("mst.pv.files1") : _loc.TFormat("mst.pv.files", n);
    }

    // The old in-window "File naming" block, now the STEMS "?" tip.
    private string FileNamingHelp() =>
        _loc.T("mst.pv.fileNaming.desc") + "\n\n" +
        "   Song (Piano).mid   ·   Song (Guitar).mid\n" +
        "   Song (Bass).mid   ·   Song (Bass 2).mid   ·   Song (Drum).mid\n\n" +
        _loc.T("mst.pv.fileNaming.dup") + "\n\n" +
        _loc.T("mst.pv.fileNaming.select");

    // Dynamic track list: a fixed pool of rows, the first N (= the selected song's stem count) shown. Each slot reads
    // the stem at its index from PreviewStems(), so rows follow whatever files the selected song actually has.
    private const int PreviewSlotCount = 10;

    private HudElement[] BuildPreviewSlots()
    {
        var pool = new HudElement[PreviewSlotCount];
        for (int i = 0; i < PreviewSlotCount; i++) { int idx = i; pool[i] = PreviewTrackSlot(idx); }
        return pool;
    }

    // One raised row for the stem at list index `idx`: [audible | name | instrument | Sustain: X | Tone], all keyed by
    // the stem's key. The instrument mode (drum/piano) still gates sustain/tone; the gated controls grey out in place
    // (same geometry), so rows line up whatever the instrument.
    private HudElement PreviewTrackSlot(int idx)
    {
        string KeyAt()  { var s = PreviewStems(); return idx < s.Count ? s[idx].key  : ""; }
        string ModeAt() { var s = PreviewStems(); return idx < s.Count ? s[idx].mode : "piano"; }
        bool NoSus()  => ModeAt() == "drum";                          // drums are one-shots — no sustain
        bool NoTone() => ModeAt() == "drum" || ModeAt() == "piano";   // piano & drum have a single fixed timbre + technique
        bool Audible() => !_previewSynth.IsMuted(KeyAt());
        return new BubbleElement(new RowElement(new HudElement[]
        {
            new ToggleElement(Label: () => "", Get: Audible, Set: v => _previewSynth.SetMute(KeyAt(), !v)),
            new CellElement(new TextElement(() => Cap(KeyAt()), Color: () => Audible() ? TextColor() : MutedColor(), NoWrap: true)
                { Ellipsize = true }, Weight: 1f),
            new DropdownElement(Selected: () => InstrumentIndex(ModeAt()), Options: () => InstrumentNames,
                OnSelect: i => SelectInstrument(KeyAt(), i), Width: StemInstW) { Style = DropdownStyle.Pill, CornerRadius = PillRadius },   // overrides filename detection
            NeutralButton(() => _loc.T("mst.pv.sustain") + ": " + (NoSus() ? "—" : PedalName(KeyAt())),
                () => CyclePedal(KeyAt()), StemSustainLblW, () => !NoSus()),
            new ToggleElement(Label: () => "", Get: () => !NoTone() && _previewSynth.GetApplyTone(KeyAt()),
                Set: v => { if (!NoTone()) _previewSynth.SetApplyTone(KeyAt(), v); }, Enabled: () => !NoTone()),
            new CellElement(new TextElement(() => _loc.T("mst.pv.tone"), Color: () => NoTone() ? MutedColor() : TextColor(), NoWrap: true),
                Width: StemToneLblW),
        }, Gap: 4f))
        {
            FillColorFn = () => TileColor(), CornerRadius = 6f, PaddingX = TilePadX, PaddingY = 3f,
        };
    }
}
