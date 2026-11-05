using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The Library window: the midi/ folder scan, the search filter and the click-to-add song list.
public sealed partial class Plugin
{
    // The Library window (house style, approved design 2026-11-06): search field + Rescan / Open folder, then the SONGS
    // panel — raised 26 px tiles, exactly LibraryRowsVisible rows tall then scrolling — and a muted "Adds to" footer.
    private const int   LibraryRowsVisible = 15;
    private const float LibraryListHeight  = LibraryRowsVisible * ListRowPitch - ListRowGap;   // 15 × 28 − 2 = 418
    private readonly HoverSlots _searchClearHover = new(1);

    private HudElement BuildLibraryRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(new InputElement(Get: () => _librarySearch, Submit: SetLibrarySearch, OnChange: SetLibrarySearch)
                { SingleLine = true, Placeholder = () => _loc.T("mst.lib.search") }, Weight: 1f),
            // Clear: an icon-only ✕ after the field while it holds text; an equal-width spacer otherwise, so the
            // buttons never move. (The toolkit can't overlay a control INSIDE an InputElement.)
            new CellElement(new ConditionalElement(() => _librarySearch.Length > 0,
                new RowElement(new HudElement[]
                {
                    RemoveIcon(_searchClearHover, 0, () => "search", () => SetLibrarySearch("")),
                }, Justify: RowJustify.Center),
                new SpacerElement(1f, 1f)), Width: 26f),
            NeutralButton(() => _loc.T("mst.lib.rescan"), RescanMidiFolder),
            NeutralButton(() => _loc.T("mst.lib.locate"), OpenMidiFolder),
        }, Gap: 6f),
        SectionPanel(() => _loc.T("mst.cap.songs"), LibraryHint,
            new ConditionalElement(() => _songView.Length == 0,
                new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, LibraryListHeight),
                    new CellElement(new TextElement(() => _loc.T(_bandSongs.Length == 0 ? "mst.lib.empty" : "mst.lib.noMatch"),
                        Color: () => MutedColor(), Align: TextAlign.Center), Weight: 1f),
                }, Gap: 0.01f),
                new VirtualListElement(
                    Count:     () => _songView.Length,
                    RowHeight: ListRowPitch,
                    Pool:      BuildSongPool(),
                    OnWindow:  first => _songWindowFirst = first,
                    Height:    LibraryListHeight)
                { ResetScroll = () => { var r = _songScrollReset; _songScrollReset = false; return r; } })),
        BuildLibraryFooter(),   // "Adds to: …" / the green "✓ Added …" confirmation (Plugin.Library.Feedback.cs)
    }, Gap: 8f);

    private string LibraryHint()
    {
        if (_bandSongs.Length == 0) return "";
        return _songView.Length == _bandSongs.Length
            ? _loc.TFormat("mst.lib.count", _bandSongs.Length)
            : _loc.TFormat("mst.lib.countFiltered", _songView.Length, _bandSongs.Length);
    }

    private static string BandMidiDir()
    {
        string root = AppContext.BaseDirectory;   // game_mini (the running exe's dir) under BepInEx
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
        {
            try
            {
                var dll = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                if (!string.IsNullOrEmpty(dll))
                    root = System.IO.Path.GetFullPath(System.IO.Path.Combine(dll, "..", "..", "..")); // …/stellar/plugins/maestro → game_mini
            }
            catch { }
        }
        return System.IO.Path.Combine(root, "midi");
    }

    private void RescanMidiFolder()
    {
        try
        {
            var dir = BandMidiDir();
            System.IO.Directory.CreateDirectory(dir);
            _bandSongs = System.IO.Directory.EnumerateFiles(dir, "*.*", System.IO.SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    var e = System.IO.Path.GetExtension(f);
                    return e.Equals(".mid", StringComparison.OrdinalIgnoreCase) || e.Equals(".midi", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ApplyLibraryFilter();
            _songDurMs.Clear();   // files may have changed on disk
            if (_bandSongs.Length == 0) SetBandStatus(_loc.T("mst.status.noFiles"));
            _services.Log.Info($"[Maestro] midi dir={dir} files={_bandSongs.Length}");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Maestro] midi scan failed: {ex.Message}");
            SetBandStatus(_loc.T("mst.status.scanFailed"), true);
        }
    }

    // Virtualized song list: a small fixed pool of rows scrolls over the full song list. The framework calls
    // OnWindow(first) before polling, so each pool slot maps to logical index (first + slot) — no blank rows,
    // and the scrollbar is sized to the true count.
    private const int SongPoolSize = LibraryRowsVisible + 3;   // visible rows + margin
    private int      _songWindowFirst;
    private bool     _songScrollReset;
    private string   _librarySearch = "";
    private string[] _songView = Array.Empty<string>();   // _bandSongs filtered by the search box

    // Recompute the visible song list from the search text. Called on rescan and whenever the search changes.
    private void ApplyLibraryFilter()
    {
        var q = _librarySearch.Trim();
        _songView = string.IsNullOrEmpty(q)
            ? _bandSongs
            : _bandSongs.Where(f => System.IO.Path.GetFileNameWithoutExtension(f)
                    .Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        _songScrollReset = true;
    }

    private void SetLibrarySearch(string s)
    {
        _librarySearch = s ?? "";
        ApplyLibraryFilter();
    }

    // Row = [name (ellipsized) | length (muted) | green "+"] on a raised rounded tile 2 px from the next; a click anywhere
    // adds the song. Bare selectable + a tile-drawn hover, so the hover keeps the tile's rounded shape.
    private readonly HoverSlots _songRowHover = new(SongPoolSize);

    private HudElement[] BuildSongPool()
    {
        var pool = new HudElement[SongPoolSize];
        for (int s = 0; s < SongPoolSize; s++)
        {
            int slot = s;
            pool[s] = ListSlot(ListTile(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(TilePadX, ListTileHeight),   // Bare = no padding: the strut alone is the 26 px tile
                    new CellElement(new TextElement(() => SongSlotLabel(slot), Color: () => TextColor(), NoWrap: true)
                        { Ellipsize = true }, Weight: 1f),
                    // "Added" (bold green) while this row's add-flash lasts; nothing otherwise.
                    new ConditionalElement(() => SongSlotFlashing(slot),
                        new TextElement(() => _loc.T("mst.lib.added"), Color: () => StatusGreen, NoWrap: true, FontSize: 12) { Bold = true }),
                    new CellElement(new TextElement(() => SongSlotDuration(slot), Color: () => MutedColor(),
                        Align: TextAlign.Right, NoWrap: true), Width: DurationColW),
                    new TextElement(() => "+", Color: () => StatusGreen, NoWrap: true, FontSize: 16) { Bold = true },
                    new SpacerElement(1f, 1f),   // + the 6 px row gap = the right margin (Bare has no padding)
                }, Gap: 6f),
                OnClick: () => AddLibraryToQueue(_songWindowFirst + slot))
                {
                    Bare = true,
                    OnHover = over => { _songRowHover.Over[slot] = over; _songRowHover.Id[slot] = SongSlotLabel(slot); },
                }, hovered: () => _songRowHover.Get(slot, SongSlotLabel(slot)), flashing: () => SongSlotFlashing(slot)));
        }
        return pool;
    }

    private string SongSlotLabel(int slot)
    {
        int i = _songWindowFirst + slot;
        return i >= 0 && i < _songView.Length ? System.IO.Path.GetFileNameWithoutExtension(_songView[i]) : "";
    }

    private string SongSlotDuration(int slot)
    {
        int i = _songWindowFirst + slot;
        return i >= 0 && i < _songView.Length ? SongDurationText(System.IO.Path.GetFileName(_songView[i])) : "";
    }

    // ── song lengths (queue + library rows) ──────────────────────────────────────────────────────────────────────
    // Parsed on demand the first time a row shows the file and cached by file name (cleared on rescan). The row Funcs
    // are polled every refresh, so a new file costs ONE MidiParser pass, and at most DurParsesPerTick per frame
    // (PlaylistTick resets the budget) — a long list scrolled fast fills in over a few frames instead of hitching.
    private const float DurationColW     = 40f;
    private const int   DurParsesPerTick = 2;
    private readonly System.Collections.Generic.Dictionary<string, int> _songDurMs = new(StringComparer.OrdinalIgnoreCase);
    private int _durParseBudget = DurParsesPerTick;

    private string SongDurationText(string fileName)
    {
        if (!_songDurMs.TryGetValue(fileName, out int ms))
        {
            if (_durParseBudget <= 0) return "";
            _durParseBudget--;
            var song = MidiParser.TryParseFile(System.IO.Path.Combine(BandMidiDir(), fileName), out _);
            ms = song?.DurationMs ?? -1;
            _songDurMs[fileName] = ms;
        }
        return ms > 0 ? Mmss(ms) : "";
    }

    // Clicking a library row appends that file to the active playlist's queue (stores the bare filename).
    private void AddLibraryToQueue(int libIdx)
    {
        if (libIdx < 0 || libIdx >= _songView.Length) return;
        AddToActive(System.IO.Path.GetFileName(_songView[libIdx]));
        MarkLibraryAdded(_songView[libIdx]);   // row flash + footer confirmation
    }

    // Rescans the midi/ folder, then opens the Library window centred on the main window — so newly-dropped files show
    // up without a manual rescan.
    private void OpenLibrary()
    {
        RescanMidiFolder();
        _libraryCenterer.OpenCentred();
    }

    // Opens the midi/ folder in the OS file explorer (creating it first if missing) so the user can drop files in.
    private void OpenMidiFolder()
    {
        try
        {
            var dir = BandMidiDir();
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Maestro] open midi folder failed: {ex.Message}");
            SetBandStatus(_loc.T("mst.status.openFailed"), true);
        }
    }
}
