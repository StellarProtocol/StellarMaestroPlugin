using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The main window's QUEUE panel (approved design 2026-11-06): playlist Pill dropdown + New / Rename / ✕ (delete, with
// the house confirmation), the song list (raised 26 px tiles 2 px apart, exactly QueueRowsVisible rows then scrolling),
// and the one-row footer (Auto next / Loop / Shuffle icon toggles, "+ Add"). Rows reorder by DRAG (framework
// VirtualListElement.OnReorder, SDK 2.29.0); the ▲ ▼ buttons are gone. Queue logic is Plugin.Playlist.cs.
public sealed partial class Plugin
{
    private const int   QueueRowsVisible = 8;
    private const float QueueListHeight  = QueueRowsVisible * ListRowPitch - ListRowGap;   // 8 × 28 − 2 = 222
    private const int   QueuePoolSize    = QueueRowsVisible + 3;               // visible rows + margin
    // Index column: a FIXED label box (TextElement Width, right-aligned) wide enough for "999", so every name starts
    // at the same x whatever the number's width (a hugging text in a Cell let the names drift 1–2 px row to row).
    private const float QueueIndexColW   = 26f;
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

    private string QueueHint() => _footerHintKey != null ? FooterHint()
        : Active.Songs.Count == 0 ? "" : SongCountText(Active.Songs.Count);

    // ── playlist row ──────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildPlaylistRow() => new RowElement(new HudElement[]
    {
        new CellElement(new ConditionalElement(() => _renaming,
            new InputElement(Get: () => _renameDraft, Submit: CommitRename, OnChange: s => _renameDraft = s ?? "")
                { SingleLine = true, FocusRequest = () => _renameFocusSeq },
            new RowElement(new HudElement[]
            {
                new DropdownElement(() => _plIdx, PlaylistNames, OnPickPlaylist, Width: PlaylistDropW) { Style = DropdownStyle.Pill, CornerRadius = PillRadius },
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
        {
            ResetScroll = () => { var r = _queueScrollReset; _queueScrollReset = false; return r; },
            // Drag a row to move it (< 6 px = a normal click / select; Esc or a drop outside cancels; the list
            // auto-scrolls at its edges). Remove-then-insert item indices; persisted like the old ▲ ▼.
            OnReorder = ReorderQueue,
        });

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
            bool Playing() => Valid() && Idx() == NowPlayingInView;   // ▶ only when viewing the playing playlist
            string? Id()   => Valid() ? $"{_plIdx}:{Idx()}:{Active.Songs[Idx()]}" : null;

            pool[s] = ListSlot(ListTile(new RowElement(new HudElement[]
            {
                AccentBar(Sel),
                new CellElement(new SelectableElement(new RowElement(new HudElement[]
                    {
                        new SpacerElement(2f, ListTileHeight),   // Bare = no padding: the strut alone is the 26 px tile
                        // One text (no Conditional): "▶" green while playing, else the muted 1-based index.
                        new TextElement(() => Valid() ? (Playing() ? "▶" : (Idx() + 1).ToString()) : "",
                            Color: () => Playing() ? StatusGreen : MutedColor(), Width: QueueIndexColW,
                            Align: TextAlign.Right, NoWrap: true),
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

    // ── footer: ONE row, right-aligned: · spacer · [auto next] [loop] [shuffle] [+ Add] ──────────────────────────────
    // The three play-mode toggles are icon chips (Plugin.Style.Blocks.cs IconChip). Loop cycles Off → All → One. The
    // toolkit has no tooltip (none in SDK 2.28.1 / 2.29.0), so while the pointer is on a toggle its plain-words
    // state ("Loop: all songs") replaces "N songs" in the QUEUE caption hint, and reverts on hover-out.
    private string? _footerHintKey;   // which toggle is hovered: "auto" / "loop" / "shuffle"; null = none

    private void FooterHover(string key, bool over)
    {
        if (over) _footerHintKey = key;
        else if (_footerHintKey == key) _footerHintKey = null;
    }

    private string FooterHint() => _footerHintKey switch
    {
        "auto"    => _loc.T(_autoAdvance ? "mst.q.hint.autoOn" : "mst.q.hint.autoOff"),
        "loop"    => _loc.T(_loopMode == 1 ? "mst.q.hint.loopAll" : _loopMode == 2 ? "mst.q.hint.loopOne" : "mst.q.hint.loopOff"),
        "shuffle" => _loc.T(_shuffle ? "mst.q.hint.shuffleOn" : "mst.q.hint.shuffleOff"),
        _         => "",
    };

    private HudElement BuildQueueControls()
    {
        var autoHover = new HoverFlag();
        var loopHover = new HoverFlag();
        var shufHover = new HoverFlag();
        void OnAuto(bool o) => FooterHover("auto", o);
        void OnLoop(bool o) => FooterHover("loop", o);
        void OnShuf(bool o) => FooterHover("shuffle", o);
        return new RowElement(new HudElement[]
        {
            new SpacerElement(0f),
            new ConditionalElement(() => _autoAdvance,
                IconChip("icon-autonext-on.png",  true,  autoHover, ToggleAutoAdvance, OnAuto),
                IconChip("icon-autonext-off.png", false, autoHover, ToggleAutoAdvance, OnAuto)),
            new ConditionalElement(() => _loopMode == 0,
                IconChip("icon-loop-off.png", false, loopHover, CycleLoop, OnLoop),
                new ConditionalElement(() => _loopMode == 1,
                    IconChip("icon-loop-on.png",     true, loopHover, CycleLoop, OnLoop),
                    IconChip("icon-loop-one-on.png", true, loopHover, CycleLoop, OnLoop))),
            new ConditionalElement(() => _shuffle,
                IconChip("icon-shuffle-on.png",  true,  shufHover, ToggleShuffle, OnShuf),
                IconChip("icon-shuffle-off.png", false, shufHover, ToggleShuffle, OnShuf)),
            PrimaryButton(() => _loc.T("mst.pl.add"), OpenLibrary),
        }, Gap: 6f);
    }
}
