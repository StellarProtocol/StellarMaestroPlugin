namespace Stellar.Maestro;

// Pure decisions behind the main window's live text (no game / framework types, so tests/ compiles this file in
// directly). The Plugin turns each kind into a localized string and a colour.

// The header status line under "Maestro".
internal enum MainHeaderKind { Stopped, Playing, Paused, CountIn, EnsembleWaiting }

internal static class MainHeader
{
    // Waiting for the ensemble wins (nothing is playing yet), then the count-in (the player already runs but no note
    // has sounded), then the transport state.
    public static MainHeaderKind Pick(bool playing, bool paused, int countInRemainMs, bool ensembleWaiting)
    {
        if (ensembleWaiting) return MainHeaderKind.EnsembleWaiting;
        if (playing && countInRemainMs > 0) return MainHeaderKind.CountIn;
        if (playing) return MainHeaderKind.Playing;
        if (paused) return MainHeaderKind.Paused;
        return MainHeaderKind.Stopped;
    }
}

// The ONE fixed-height status line at the bottom of NOW PLAYING.
internal enum MainLineKind { None, CountIn, EnsembleWaiting, NextIn, Problem, Message, Next }

internal static class MainStatusLine
{
    public const long MessageShowMs = 4000;   // an info message ("Added …") shows this long
    public const long ProblemShowMs = 8000;   // a problem ("Summon an instrument first") stays a little longer

    // Highest priority first: count-in → waiting for the ensemble → the gap countdown before the next song → a recent
    // problem / message → "Next: <song>" → nothing.
    public static MainLineKind Pick(bool countIn, bool ensembleWaiting, bool gapCountdown,
        bool hasMessage, bool messageIsProblem, long messageAgeMs, bool hasNext)
    {
        if (countIn) return MainLineKind.CountIn;
        if (ensembleWaiting) return MainLineKind.EnsembleWaiting;
        if (gapCountdown) return MainLineKind.NextIn;
        if (hasMessage && messageAgeMs >= 0 && messageAgeMs < (messageIsProblem ? ProblemShowMs : MessageShowMs))
            return messageIsProblem ? MainLineKind.Problem : MainLineKind.Message;
        return hasNext ? MainLineKind.Next : MainLineKind.None;
    }
}

internal static class QueueNext
{
    // The queue index that auto-advance will play after `nowPlaying`, or -1 when it can't be named: nothing playing,
    // shuffle (random pick), the end of the list without Loop All, or auto-next off (Loop One still repeats).
    // loopMode: 0 = off, 1 = all, 2 = one (Plugin.Playlist.cs).
    public static int Index(int nowPlaying, int count, int loopMode, bool shuffle, bool autoAdvance)
    {
        if (nowPlaying < 0 || nowPlaying >= count) return -1;
        if (loopMode == 2) return nowPlaying;
        if (!autoAdvance || shuffle) return -1;
        int next = nowPlaying + 1;
        if (next < count) return next;
        return loopMode == 1 ? 0 : -1;
    }
}

// The playing song's index ("cursor") in the PLAYING playlist, which is tracked separately from the playlist being
// VIEWED in the queue (switching the dropdown never touches playback). Edits apply to the viewed playlist; they only
// move the cursor when the viewed playlist IS the playing one (`sameList`).
internal static class QueueCursor
{
    // After removing row `removed` from the viewed playlist: the removed song itself → -1 (no longer in the list).
    public static int AfterRemove(int nowPlaying, int removed, bool sameList)
    {
        if (!sameList || nowPlaying < 0) return nowPlaying;
        if (nowPlaying == removed) return -1;
        return nowPlaying > removed ? nowPlaying - 1 : nowPlaying;
    }

    // After a drag-reorder of the viewed playlist: REMOVE-THEN-INSERT (VirtualListElement.OnReorder) — the item at
    // `from` is removed, then inserted so it ends up at index `to`. The moved song follows to `to`; a song between the
    // two shifts by one toward the gap. Also used for the row SELECTION (always `sameList` — it lives in the view).
    public static int AfterMove(int index, int from, int to, bool sameList)
    {
        if (!sameList || index < 0 || from == to) return index;
        if (index == from) return to;
        int i = index > from ? index - 1 : index;   // after the remove
        return i >= to ? i + 1 : i;                  // after the insert
    }

    // Row index that shows the green "▶" in the viewed list: only when viewing the playing playlist.
    public static int MarkerInView(int nowPlaying, bool viewingPlaying) => viewingPlaying ? nowPlaying : -1;

    // ◀◀ within the playing playlist: the previous song, wrapping from the first (or "none") to the last.
    public static int Prev(int nowPlaying, int count) => count <= 0 ? -1 : nowPlaying <= 0 ? count - 1 : nowPlaying - 1;

    // Is the viewed-list selection a DIFFERENT song from the one playing? Then Play switches to it (and its playlist).
    public static bool SelectionDiffers(int selected, int nowPlaying, bool viewingPlaying)
        => selected >= 0 && !(viewingPlaying && selected == nowPlaying);
}

// The Maestro Link hub card's status.
internal enum LinkCardKind { NotInRoom, Joining, InRoom, Reconnecting }

internal static class LinkCard
{
    public static LinkCardKind Pick(bool connecting, bool linked, bool online)
    {
        if (linked) return online ? LinkCardKind.InRoom : LinkCardKind.Reconnecting;
        return connecting ? LinkCardKind.Joining : LinkCardKind.NotInRoom;
    }
}
