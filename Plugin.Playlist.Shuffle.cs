namespace Stellar.Maestro;

// Shuffle = a real shuffle ORDER over the PLAYING playlist (ShuffleOrder.cs): every song once per round, the current
// song first; Loop Off ends after the round, Loop All reshuffles (never repeating the song that just ended first).
// The old behaviour picked a random song each time (only avoiding the current one), so songs repeated and Loop Off
// never ended. Row order is untouched — shuffle off simply continues in row order from the current song.
public sealed partial class Plugin
{
    private ShuffleOrder? _shuffleOrder;
    private PlaylistDto?  _shuffleList;   // the playlist _shuffleOrder was built over

    private ShuffleOrder Shuffle => _shuffleOrder ??= new ShuffleOrder(_rng);

    // The order is usable for `list` only if it was built over it and still covers every row.
    private bool ShuffleValidFor(PlaylistDto list)
        => _shuffle && ReferenceEquals(_shuffleList, list) && Shuffle.Count == list.Songs.Count && Shuffle.Count > 0;

    // (Re)build the order over `list` with row `first` at the front (an explicit Play, or shuffle switched on).
    private void RebuildShuffle(PlaylistDto list, int first)
    {
        Shuffle.Build(list.Songs.Count, first);
        _shuffleList = list;
    }

    private void ClearShuffle() { Shuffle.Clear(); _shuffleList = null; }

    // Shuffle switched on while a song plays → a fresh order starting from it.
    private void OnShuffleChanged()
    {
        if (_shuffle && _playingList != null && _nowPlaying >= 0) RebuildShuffle(_playingList, _nowPlaying);
        else if (!_shuffle) ClearShuffle();
    }

    // ▶▶ / auto next in shuffle: the next song in the order. At the end: auto-next stops unless Loop All (new round);
    // ▶▶ starts a new round (as ▶▶ at the end of row order wraps to the top). -1 = nothing to play.
    private int ShuffleNext(PlaylistDto list, bool auto)
    {
        if (!ShuffleValidFor(list))
        {
            RebuildShuffle(list, _nowPlaying);
            if (_nowPlaying < 0) return Shuffle.Order[0];   // nothing was playing: start at the front of the new order
        }
        int next = Shuffle.Advance(loopAll: _loopMode == 1);
        if (next < 0 && !auto) next = Shuffle.Advance(loopAll: true);
        return next;
    }

    // ◀◀ in shuffle: the previous song in the order; at the start, the current song restarts. -1 = nothing to play.
    private int ShufflePrev(PlaylistDto list)
    {
        if (!ShuffleValidFor(list)) RebuildShuffle(list, _nowPlaying);
        int prev = Shuffle.Back();
        return prev >= 0 ? prev : _nowPlaying;
    }

    // "Next:" in shuffle: the next song in the order (Loop All at the end → the first of the pre-generated next round).
    private int ShufflePeekNext()
    {
        var list = _playingList;
        if (list == null || !ShuffleValidFor(list)) return -1;
        return Shuffle.PeekNext(loopAll: _loopMode == 1);
    }

    // Playlist edits → keep the order on the same songs (only for the playlist it was built over).
    private void ShuffleOnAdded(PlaylistDto list, int row)   { if (ShuffleListIs(list)) Shuffle.OnAdded(row); }
    private void ShuffleOnRemoved(PlaylistDto list, int row) { if (ShuffleListIs(list)) Shuffle.OnRemoved(row); }
    private void ShuffleOnMoved(PlaylistDto list, int from, int to) { if (ShuffleListIs(list)) Shuffle.OnMoved(from, to); }
    private bool ShuffleListIs(PlaylistDto list) => _shuffleList != null && ReferenceEquals(_shuffleList, list);
}
