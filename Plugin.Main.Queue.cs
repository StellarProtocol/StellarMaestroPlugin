using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The main window's QUEUE panel (approved design 2026-11-06): playlist Pill dropdown + New / Rename / ✕ (delete, with
// the house confirmation), the song list (raised 26 px tiles 2 px apart, exactly QueueRowsVisible rows then scrolling),
// the Auto next / Loop / Shuffle chips, and move ▲ ▼ + "Add songs". Queue logic itself is Plugin.Playlist.cs.
public sealed partial class Plugin
{
    private const int   QueueRowsVisible = 8;
    private const float QueueListHeight  = QueueRowsVisible * ListRowPitch - ListRowGap;   // 8 × 28 − 2 = 222
    private const int   QueuePoolSize    = QueueRowsVisible + 3;               // visible rows + margin
    private const float QueueIndexColW   = 22f;
    private const float PlaylistDropW    = 150f;
    private const float RenameLabelW     = 56f;   // "Rename" / "Save" share one label box, so the swap never moves a neighbour

    private int  _queueWindowFirst;
    private bool _queueScrollReset;
    private readonly HoverSlots _queueRemoveHover = new(QueuePoolSize);
    private readonly HoverSlots _queueRowHover    = new(QueuePoolSize);

    private bool   _renaming;          // the dropdown is replaced by a name field until Save / Enter
    private string _renameDraft = "";
    private int    _renameFocusSeq;    // bumped to focus the name field when renaming starts
    private bool   _confirmDelete;     // the list area shows the delete confirmation

    // Dropdown options = the playlist names; rebuilt (new list instance) whenever SavePlaylists runs.
    private IReadOnlyList<string>? _playlistNames;
    private IReadOnlyList<string> PlaylistNames() => _playlistNames ??= _playlists.ConvertAll(p => p.Name);

    private HudElement BuildQueuePanel() => SectionPanel(() => _loc.T("mst.cap.queue"), QueueHint,
        BuildPlaylistRow(),
        // Confirmation and list share the SAME fixed height, so asking never moves the controls below.
        new ConditionalElement(() => _confirmDelete, BuildDeleteConfirm(), BuildQueueList()),
        BuildQueueControls());

    private string QueueHint() => Active.Songs.Count == 0 ? "" : SongCountText(Active.Songs.Count);

    // ── playlist row ──────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildPlaylistRow() => new RowElement(new HudElement[]
    {
        new CellElement(new ConditionalElement(() => _renaming,
            new InputElement(Get: () => _renameDraft, Submit: CommitRename, OnChange: s => _renameDraft = s ?? "")
                { SingleLine = true, FocusRequest = () => _renameFocusSeq },
            new RowElement(new HudElement[]
            {
                new DropdownElement(() => _plIdx, PlaylistNames, OnPickPlaylist, Width: PlaylistDropW) { Style = DropdownStyle.Pill },
                new SpacerElement(0f),   // flexible: the buttons sit at the right edge
            }, Gap: 6f)), Weight: 1f),
        NeutralButton(() => _loc.T("mst.pl.new"), OnNewPlaylist),
        NeutralButton(() => _loc.T(_renaming ? "mst.pl.save" : "mst.pl.rename"), ToggleRename, RenameLabelW),
        GlyphPill(() => "✕", () => { _renaming = false; _confirmDelete = true; }, GlyphLabelW),
    }, Gap: 6f);

    private void OnPickPlaylist(int i) { _confirmDelete = false; SelectPlaylistIndex(i); }
    private void OnNewPlaylist()       { _renaming = false; _confirmDelete = false; NewPlaylist(); }

    private void ToggleRename()
    {
        if (_renaming) { CommitRename(_renameDraft); return; }
        _renameDraft   = Active.Name;
        _confirmDelete = false;
        _renaming      = true;
        _renameFocusSeq++;
    }

    private void CommitRename(string name)
    {
        RenameActivePlaylist(name);   // blank keeps the old name
        _renaming = false;
    }

    // ── delete confirmation (house pattern: bold question, muted consequence, Danger / Neutral pair) ─────────────
    private HudElement BuildDeleteConfirm() => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, QueueListHeight),   // the list's height, so the panel doesn't change size
        new CellElement(new BubbleElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _loc.TFormat("mst.pl.delQ", Active.Name), Color: () => TextColor(), FontSize: 14) { Bold = true },
                MutedLine(() => _loc.T("mst.pl.delInfo")),
                PillPair(
                    DangerButton(() => _loc.T("mst.pl.delete"), () => { _confirmDelete = false; DeleteActivePlaylist(); }, PillPairWidth),
                    NeutralButton(() => _loc.T("mst.cancel"), () => _confirmDelete = false, PillPairWidth)),
            }, Gap: 8f))
            { FillColorFn = () => TileColor(), CornerRadius = 6f, PaddingX = 10f, PaddingY = 10f }, Weight: 1f),
    }, Gap: 0.01f);

    // ── song list ─────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildQueueList() => new ConditionalElement(() => Active.Songs.Count == 0,
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f, QueueListHeight),
            new CellElement(new TextElement(() => _loc.T("mst.q.empty"), Color: () => MutedColor(), Align: TextAlign.Center), Weight: 1f),
        }, Gap: 0.01f),
        new VirtualListElement(
            Count:     () => Active.Songs.Count,
            RowHeight: ListRowPitch,
            Pool:      BuildQueuePool(),
            OnWindow:  first => _queueWindowFirst = first,
            Height:    QueueListHeight)
        { ResetScroll = () => { var r = _queueScrollReset; _queueScrollReset = false; return r; } });

    // Row = [accent bar (selected) | index or green ▶ (playing) | name | length | ✕] on ONE raised tile (lighter when
    // selected or hovered). A click on the row selects it (Play then plays it); ✕ removes it at once. The ✕ sits inside
    // the tile but is a SIBLING of the row's selectable, not inside it, so a ✕ click can't also select. The selectable
    // is Bare (no wash of its own, which used to stop short of the ✕ and made it look outside the row): the tile draws
    // the hover instead. Every Func re-resolves the logical index (first + slot) because rows shift under a slot when
    // one is removed; the hover flags are keyed per slot + item for the same reason.
    private HudElement[] BuildQueuePool()
    {
        var pool = new HudElement[QueuePoolSize];
        for (int s = 0; s < QueuePoolSize; s++)
        {
            int slot = s;
            int  Idx()     => _queueWindowFirst + slot;
            bool Valid()   => Idx() >= 0 && Idx() < Active.Songs.Count;
            bool Sel()     => Valid() && Idx() == _queueSel;
            bool Playing() => Valid() && Idx() == _nowPlaying;
            string? Id()   => Valid() ? $"{_plIdx}:{Idx()}:{Active.Songs[Idx()]}" : null;

            pool[s] = ListSlot(ListTile(new RowElement(new HudElement[]
            {
                AccentBar(Sel),
                new CellElement(new SelectableElement(new RowElement(new HudElement[]
                    {
                        new SpacerElement(2f, ListTileHeight),   // Bare = no padding: the strut alone is the 26 px tile
                        // One text (no Conditional): "▶" green while playing, else the muted 1-based index.
                        new CellElement(new TextElement(() => Valid() ? (Playing() ? "▶" : (Idx() + 1).ToString()) : "",
                            Color: () => Playing() ? StatusGreen : MutedColor(), NoWrap: true), Width: QueueIndexColW),
                        new CellElement(new TextElement(() => Valid() ? System.IO.Path.GetFileNameWithoutExtension(Active.Songs[Idx()]) : "",
                            Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
                        new CellElement(new TextElement(() => Valid() ? SongDurationText(Active.Songs[Idx()]) : "",
                            Color: () => MutedColor(), Align: TextAlign.Right, NoWrap: true), Width: DurationColW),
                    }, Gap: 6f),
                    OnClick: () => SelectQueue(Idx()))
                    {
                        Bare = true,
                        OnHover = over => { _queueRowHover.Over[slot] = over; _queueRowHover.Id[slot] = Id(); },
                    }, Weight: 1f),
                RemoveIcon(_queueRemoveHover, slot, Id, () => { if (Valid()) RemoveFromQueue(Idx()); }),
                new SpacerElement(2f, 1f),
            }, Gap: 4f), Sel, () => _queueRowHover.Get(slot, Id())));
        }
        return pool;
    }

    // ── under the list: chips row, then move + Add songs ──────────────────────────────────────────────────────────
    // Each chip has a FIXED width = its longest label in any language and (Loop) any state + chip padding + margin,
    // so cycling Loop Off → All → One never resizes it. Measured with the Windows UI fonts at bold 14 (Arial / Yu
    // Gothic / Malgun / Leelawadee — estimates of the game face): Auto next 67 (en/fil), Loop 100 (th "วนซ้ำ: เพลงเดียว"),
    // Shuffle 70 (ja "シャッフル"). 84 + 116 + 84 + the arrows (~38 each) + "+ Tambah lagu" (~117) + gaps ≈ 500 > the
    // 416 px panel row, so the chips get their own row and ▲ ▼ + Add songs sit right-aligned on the next.
    private const float ChipAutoW    = 84f;
    private const float ChipLoopW    = 116f;
    private const float ChipShuffleW = 84f;

    private HudElement BuildQueueControls() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            ChipToggle(() => _loc.T("mst.pl.autoAdvance"), () => _autoAdvance, ToggleAutoAdvance, ChipAutoW),
            ChipToggle(() => _loc.T("mst.pl.loop") + ": " + LoopName(), () => _loopMode != 0, CycleLoop, ChipLoopW),
            ChipToggle(() => _loc.T("mst.pl.shuffle"), () => _shuffle, ToggleShuffle, ChipShuffleW),
        }, Gap: 6f),
        new RowElement(new HudElement[]
        {
            new SpacerElement(0f),
            GlyphPill(() => "▲", () => MoveQueue(-1), GlyphLabelW, () => _queueSel > 0 && _queueSel < Active.Songs.Count),
            GlyphPill(() => "▼", () => MoveQueue(+1), GlyphLabelW, () => _queueSel >= 0 && _queueSel < Active.Songs.Count - 1),
            PrimaryButton(() => _loc.T("mst.pl.addSongs"), OpenLibrary),
        }, Gap: 6f),
    }, Gap: 6f);
}
