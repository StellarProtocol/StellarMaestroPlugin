using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Library "added" feedback (user-approved 2026-11-06, options 1 + 2; no "already in the playlist" marker):
//   1. the clicked row flashes for AddFlashSec — a green-tinted tile with a 1 px green ring and a bold green "Added"
//      before the length;
//   2. the footer ("Adds to: <playlist>") shows "✓ Added “song” to <playlist> · N songs" in green for AddFooterSec.
// The flash is keyed by the song's PATH (not the pool slot), so a re-render / scroll keeps it on the right row, and
// timed with IFramework.TimeNow (monotonic seconds; deltas only). Another click restarts both with the latest song.
public sealed partial class Plugin
{
    private const float AddFlashSec  = 1f;
    private const float AddFooterSec = 3f;
    private const float AddFlashMix  = 0.18f;   // green wash over the tile while flashing
    // ✓ prefix kept apart from the translated text so it can be dropped in one place if the game font lacks it.
    private const string AddedMark = "✓ ";

    private string? _addFlashPath;
    private float   _addFlashAt = float.NegativeInfinity;
    private string  _addedSong  = "";
    private string  _addedList  = "";
    private int     _addedCount;
    private float   _addedAt    = float.NegativeInfinity;

    private float FwNow => _services.Framework.TimeNow;

    // Called after a Library click added `path` to the viewed playlist.
    private void MarkLibraryAdded(string path)
    {
        float now = FwNow;
        _addFlashPath = path;
        _addFlashAt   = now;
        _addedSong    = System.IO.Path.GetFileNameWithoutExtension(path);
        _addedList    = Active.Name;
        _addedCount   = Active.Songs.Count;
        _addedAt      = now;
    }

    // Is the Library row in pool `slot` the song that was just added (and still within the flash)?
    private bool SongSlotFlashing(int slot)
    {
        if (_addFlashPath == null || FwNow - _addFlashAt >= AddFlashSec) return false;
        int i = _songWindowFirst + slot;
        return i >= 0 && i < _songView.Length && _songView[i] == _addFlashPath;
    }

    private bool AddedFooterShown() => FwNow - _addedAt < AddFooterSec;

    private ColorRgba FlashTileColor() => Over(StatusGreen with { A = AddFlashMix }, TileColor());

    // The Library footer: the "added" confirmation while it lasts, else "Adds to: <playlist>". One fixed-height line
    // (NoWrap + ellipsis), so a long song name never makes the window jump.
    private HudElement BuildLibraryFooter() => FixedLine(new TextElement(
        () => AddedFooterShown()
            ? AddedMark + _loc.TFormat("mst.lib.addedFooter", _addedSong, _addedList, _addedCount)
            : _loc.TFormat("mst.lib.addsTo", Active.Name),
        Color: () => AddedFooterShown() ? StatusGreen : MutedColor(), NoWrap: true) { Ellipsize = true });
}
