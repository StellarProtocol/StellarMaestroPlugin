using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// The Library window: the midi/ folder scan, the search filter and the click-to-add song list.
public sealed partial class Plugin
{
    // The Library window — browse the midi/ folder and click a song to add it to the queue.
    private HudElement BuildLibraryRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("mst.lib.header"), Emphasis: true),
        new RowElement(new HudElement[]
        {
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.lib.rescan"), OnClick: RescanMidiFolder), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.lib.locate"),        OnClick: OpenMidiFolder),   Weight: 1f),
        }, Gap: 4f),
        new SeparatorElement(),
        new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _loc.T("mst.lib.search"), Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted), Width: 56f),
            new CellElement(new InputElement(Get: () => _librarySearch, Submit: SetLibrarySearch, OnChange: SetLibrarySearch), Weight: 1f),
            new CellElement(new ButtonElement(Label: () => _loc.T("mst.lib.clear"), OnClick: () => SetLibrarySearch("")), Width: 56f),
        }, Gap: 6f),
        new TextElement(() => _bandSongs.Length == 0
                ? _loc.T("mst.lib.empty")
                : _songView.Length == _bandSongs.Length
                    ? _loc.TFormat("mst.lib.count", _bandSongs.Length)
                    : _loc.TFormat("mst.lib.countFiltered", _songView.Length, _bandSongs.Length),
            Color: () => (ColorRgba?)_services.Theme.Colors.TextMuted),
        new VirtualListElement(
            Count:     () => _songView.Length,
            RowHeight: 22f,
            Pool:      BuildSongPool(),
            OnWindow:  first => _songWindowFirst = first,
            Height:    240f)
        { ResetScroll = () => { var r = _songScrollReset; _songScrollReset = false; return r; } },
    }, Gap: 8f);

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
            _bandMidiStatus = _bandSongs.Length == 0 ? $"no .mid/.midi files in {dir}" : $"{_bandSongs.Length} file(s) found";
            _services.Log.Info($"[Maestro] midi dir={dir} files={_bandSongs.Length}");
        }
        catch (Exception ex) { _bandMidiStatus = $"scan error: {ex.Message}"; }
    }

    // Virtualized song list: a small fixed pool of rows scrolls over the full song list. The framework calls
    // OnWindow(first) before polling, so each pool slot maps to logical index (first + slot) — no blank rows,
    // and the scrollbar is sized to the true count.
    private const int SongPoolSize = 12;   // ≈ visible rows (176/22 ≈ 8) + margin
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

    private HudElement[] BuildSongPool()
    {
        var pool = new HudElement[SongPoolSize];
        for (int s = 0; s < SongPoolSize; s++)
        {
            int slot = s;
            pool[s] = new SelectableElement(
                new TextElement(() => SongSlotLabel(slot), NoWrap: true),   // single line; viewport RectMask2D hard-clips long names
                OnClick: () => AddLibraryToQueue(_songWindowFirst + slot));
        }
        return pool;
    }

    private string SongSlotLabel(int slot)
    {
        int i = _songWindowFirst + slot;
        return i >= 0 && i < _songView.Length ? System.IO.Path.GetFileNameWithoutExtension(_songView[i]) : "";
    }

    // Clicking a library row appends that file to the active playlist's queue (stores the bare filename).
    private void AddLibraryToQueue(int libIdx)
    {
        if (libIdx < 0 || libIdx >= _songView.Length) return;
        AddToActive(System.IO.Path.GetFileName(_songView[libIdx]));
    }

    // Rescans the midi/ folder, then opens the Library window — so newly-dropped files show up without a manual rescan.
    private void OpenLibrary()
    {
        RescanMidiFolder();
        _libraryWindow.SetVisible(true);
    }

    // Opens the midi/ folder in the OS file explorer (creating it first if missing) so the user can drop files in.
    private void OpenMidiFolder()
    {
        try
        {
            var dir = BandMidiDir();
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            _bandMidiStatus = $"opened {dir}";
        }
        catch (Exception ex) { _bandMidiStatus = $"open failed: {ex.Message}"; }
    }
}
