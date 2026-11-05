using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The main window's QUEUE panel (approved design 2026-11-06): playlist Pill dropdown + New / Rename / ✕ (delete, with
// the house confirmation), the song list (raised 26 px tiles, exactly QueueRowsVisible rows then scrolling), and the
// Auto next / Loop / Shuffle chips + move ▲ ▼ + "Add songs". Queue logic itself is Plugin.Playlist.cs.
public sealed partial class Plugin
{
    private const int   QueueRowsVisible = 8;
    private const float QueueListHeight  = QueueRowsVisible * ListRowPitch;   // 8 × 28 = 224 (last row's 2 px gap at the bottom)
    private const int   QueuePoolSize    = QueueRowsVisible + 3;               // visible rows + margin
    private const float QueueIndexColW   = 22f;
    private const float PlaylistDropW    = 150f;
    private const float RenameLabelW     = 56f;   // "Rename" / "Save" share one label box, so the swap never moves a neighbour

    private int  _queueWindowFirst;
    private bool _queueScrollReset;
    private readonly HoverSlots _queueRemoveHover = new(QueuePoolSize);

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

    // Row = [accent bar (selected) | index or green ▶ (playing) | name | length | ✕] on a raised tile (lighter when
    // selected). A click on the row selects it (Play then plays it); ✕ removes it at once. The ✕ is a SIBLING of the
    // row's selectable, not inside it, so a ✕ click can't also select. Every Func re-resolves the logical index
    // (first + slot) because rows shift under a slot when one is removed.
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

            pool[s] = ListTile(new RowElement(new HudElement[]
            {
                AccentBar(Sel),
                new CellElement(new SelectableElement(new RowElement(new HudElement[]
                    {
                        new SpacerElement(0f, ListTileHeight - 8f),   // + the selectable's 4 + 4 = the 26 px tile
                        // One text (no Conditional): "▶" green while playing, else the muted 1-based index.
                        new CellElement(new TextElement(() => Valid() ? (Playing() ? "▶" : (Idx() + 1).ToString()) : "",
                            Color: () => Playing() ? StatusGreen : MutedColor(), NoWrap: true), Width: QueueIndexColW),
                        new CellElement(new TextElement(() => Valid() ? System.IO.Path.GetFileNameWithoutExtension(Active.Songs[Idx()]) : "",
                            Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
                        new CellElement(new TextElement(() => Valid() ? SongDurationText(Active.Songs[Idx()]) : "",
                            Color: () => MutedColor(), Align: TextAlign.Right, NoWrap: true), Width: DurationColW),
                    }, Gap: 6f),
                    OnClick: () => SelectQueue(Idx())), Weight: 1f),
                RemoveIcon(_queueRemoveHover, slot, Id, () => { if (Valid()) RemoveFromQueue(Idx()); }),
                new SpacerElement(2f, 1f),
            }, Gap: 4f), Sel);
        }
        return pool;
    }

    // ── bottom row: chips + move + Add songs ──────────────────────────────────────────────────────────────────────
    // Gap 4 (not 6): 3 × 72 chips + the two arrow pills + the Add pill just fit the 416 px panel row.
    private HudElement BuildQueueControls() => new RowElement(new HudElement[]
    {
        ChipToggle(() => _loc.T("mst.pl.autoAdvance"), () => _autoAdvance, ToggleAutoAdvance),
        ChipToggle(() => _loc.T("mst.pl.loop") + ": " + LoopName(), () => _loopMode != 0, CycleLoop),
        ChipToggle(() => _loc.T("mst.pl.shuffle"), () => _shuffle, ToggleShuffle),
        new SpacerElement(0f),
        GlyphPill(() => "▲", () => MoveQueue(-1), GlyphLabelW, () => _queueSel > 0 && _queueSel < Active.Songs.Count),
        GlyphPill(() => "▼", () => MoveQueue(+1), GlyphLabelW, () => _queueSel >= 0 && _queueSel < Active.Songs.Count - 1),
        PrimaryButton(() => _loc.T("mst.pl.addSongs"), OpenLibrary),
    }, Gap: 4f);
}
