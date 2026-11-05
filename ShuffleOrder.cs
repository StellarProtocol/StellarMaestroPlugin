using System;
using System.Collections.Generic;

namespace Stellar.Maestro;

// The shuffle ORDER of the playing playlist (pure: no game / framework types, so tests/ compiles it in directly).
//
// A random permutation of the playlist's rows, played front to back: every song once per round, the current song
// first. Loop Off ends after the last song of the order; Loop All starts a NEW round (a fresh permutation whose first
// song is never the one just played). The next round is pre-generated on demand so "Next:" can name its first song.
//
// Entries are ROW INDICES of the playing playlist, kept pointing at the same SONG as the list is edited: removes and
// drag-moves remap them (so a reorder never changes the shuffle order), and an added song goes to a random spot in the
// not-yet-played part. This is identity tracking without per-song ids (a playlist may hold the same file twice).
internal sealed class ShuffleOrder
{
    private readonly Random _rng;
    private List<int> _order = new();
    private int _pos = -1;             // index into _order of the current song; -1 = before the first
    private List<int>? _nextRound;     // the pre-generated Loop All round (null = not generated yet)

    public ShuffleOrder(Random rng) { _rng = rng; }

    public IReadOnlyList<int> Order => _order;
    public int Position => _pos;
    public int Count => _order.Count;

    public void Clear() { _order = new(); _pos = -1; _nextRound = null; }

    // A new order over rows 0..count-1 with `first` at the front (-1 / out of range = fully random); position = 0.
    public void Build(int count, int first)
    {
        _order = Permutation(count, first, avoidFirst: -1);
        _pos = _order.Count > 0 ? 0 : -1;
        _nextRound = null;
    }

    // The song after the current one: the next in the order, or (at the end) the first of the next round with Loop
    // All, or -1 (Loop Off: the shuffle round is over).
    public int PeekNext(bool loopAll)
    {
        if (_pos + 1 < _order.Count) return _order[_pos + 1];
        if (!loopAll || _order.Count == 0) return -1;
        return NextRound()[0];
    }

    // Move to the next song and return its row; -1 at the end without Loop All (position unchanged).
    public int Advance(bool loopAll)
    {
        if (_pos + 1 < _order.Count) return _order[++_pos];
        if (!loopAll || _order.Count == 0) return -1;
        _order = NextRound();
        _nextRound = null;
        _pos = 0;
        return _order[0];
    }

    // Step back to the previous song in the order; -1 at the start (position unchanged).
    public int Back() => _pos > 0 ? _order[--_pos] : -1;

    // A song was inserted at row `row` of the playing playlist (rows from `row` on shift down by one): it joins a random
    // place among the songs still to come in this round.
    public void OnAdded(int row)
    {
        Shift(_order, r => r >= row ? r + 1 : r);
        int at = _rng.Next(_pos + 1, _order.Count + 1);   // after the current song .. the very end
        _order.Insert(at, row);
        _nextRound = null;   // regenerate the next round over the new song set
    }

    // Row `row` was removed: drop it from the order and close the gap in the row numbers. If it was the CURRENT song,
    // the position steps back one, so the next Advance plays the song that followed it.
    public void OnRemoved(int row)
    {
        int at = _order.IndexOf(row);
        if (at >= 0)
        {
            _order.RemoveAt(at);
            if (at <= _pos) _pos--;
        }
        Shift(_order, r => r > row ? r - 1 : r);
        _nextRound = null;
    }

    // A drag-reorder (remove-then-insert) moved row `from` to `to`: every entry follows its song, so the ORDER of play
    // is unchanged.
    public void OnMoved(int from, int to)
    {
        Shift(_order, r => QueueCursor.AfterMove(r, from, to, sameList: true));
        if (_nextRound != null) Shift(_nextRound, r => QueueCursor.AfterMove(r, from, to, sameList: true));
    }

    private List<int> NextRound()
    {
        if (_nextRound == null)
        {
            int last = _order.Count > 0 ? _order[_order.Count - 1] : -1;
            _nextRound = Permutation(_order.Count, first: -1, avoidFirst: last);
        }
        return _nextRound;
    }

    private static void Shift(List<int> list, Func<int, int> map)
    {
        for (int i = 0; i < list.Count; i++) list[i] = map(list[i]);
    }

    // Fisher–Yates over 0..count-1; `first` (if valid) is moved to the front; otherwise `avoidFirst` (if valid and
    // count > 1) is kept OFF the front, so a new round never repeats the song that just ended.
    private List<int> Permutation(int count, int first, int avoidFirst)
    {
        var p = new List<int>(count);
        for (int i = 0; i < count; i++) p.Add(i);
        for (int i = count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        if (first >= 0 && first < count)
        {
            int at = p.IndexOf(first);
            (p[0], p[at]) = (p[at], p[0]);
        }
        else if (avoidFirst >= 0 && avoidFirst < count && count > 1 && p[0] == avoidFirst)
        {
            int j = 1 + _rng.Next(count - 1);
            (p[0], p[j]) = (p[j], p[0]);
        }
        return p;
    }
}
