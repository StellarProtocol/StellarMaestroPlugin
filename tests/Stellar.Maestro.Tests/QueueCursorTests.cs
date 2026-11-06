using Xunit;

namespace Stellar.Maestro.Tests;

// Playing vs viewed playlist (MaestroUi.cs QueueCursor + QueueNext): browsing / editing another playlist never moves
// the playing song's cursor; next / prev walk the PLAYING playlist.
public class QueueCursorTests
{
    [Fact]
    public void Remove_InOtherPlaylist_LeavesCursor() => Assert.Equal(3, QueueCursor.AfterRemove(3, 0, sameList: false));

    [Fact]
    public void Remove_BeforeCursor_InPlayingList_Shifts() => Assert.Equal(2, QueueCursor.AfterRemove(3, 0, sameList: true));

    [Fact]
    public void Remove_AfterCursor_InPlayingList_Keeps() => Assert.Equal(3, QueueCursor.AfterRemove(3, 5, sameList: true));

    [Fact]
    public void Remove_PlayingSong_ClearsCursor() => Assert.Equal(-1, QueueCursor.AfterRemove(3, 3, sameList: true));

    // Drag-reorder = remove-then-insert (VirtualListElement.OnReorder).
    [Fact]
    public void Move_InOtherPlaylist_LeavesCursor() => Assert.Equal(3, QueueCursor.AfterMove(3, 0, 5, sameList: false));

    [Fact]
    public void Move_PlayingSong_FollowsIt()
    {
        Assert.Equal(7, QueueCursor.AfterMove(3, 3, 7, sameList: true));   // dragged down
        Assert.Equal(0, QueueCursor.AfterMove(3, 3, 0, sameList: true));   // dragged up
    }

    [Fact]
    public void Move_FromAboveToBelow_ShiftsCursorUp() => Assert.Equal(2, QueueCursor.AfterMove(3, 1, 5, sameList: true));

    [Fact]
    public void Move_FromBelowToAbove_ShiftsCursorDown() => Assert.Equal(4, QueueCursor.AfterMove(3, 6, 1, sameList: true));

    [Fact]
    public void Move_OntoCursorFromAbove_ShiftsCursorUp()     // item 1 inserted at 3 → old 3 moves to 2
        => Assert.Equal(2, QueueCursor.AfterMove(3, 1, 3, sameList: true));

    [Fact]
    public void Move_OntoCursorFromBelow_ShiftsCursorDown()   // item 5 inserted at 3 → old 3 moves to 4
        => Assert.Equal(4, QueueCursor.AfterMove(3, 5, 3, sameList: true));

    [Fact]
    public void Move_EntirelyAboveOrBelow_LeavesCursor()
    {
        Assert.Equal(5, QueueCursor.AfterMove(5, 0, 2, sameList: true));
        Assert.Equal(1, QueueCursor.AfterMove(1, 4, 7, sameList: true));
    }

    [Fact]
    public void Move_NoSong_StaysNone() => Assert.Equal(-1, QueueCursor.AfterMove(-1, 2, 4, sameList: true));

    // Cross-check against a real list for every (from, to, cursor) in a 6-song playlist.
    [Fact]
    public void Move_MatchesListSemantics()
    {
        for (int from = 0; from < 6; from++)
        for (int to = 0; to < 6; to++)
        for (int cur = 0; cur < 6; cur++)
        {
            var list = new System.Collections.Generic.List<int> { 0, 1, 2, 3, 4, 5 };
            var x = list[from]; list.RemoveAt(from); list.Insert(to, x);
            Assert.Equal(list.IndexOf(cur), QueueCursor.AfterMove(cur, from, to, sameList: true));
        }
    }

    [Fact]
    public void Marker_OnlyWhenViewingPlayingList()
    {
        Assert.Equal(2, QueueCursor.MarkerInView(2, viewingPlaying: true));
        Assert.Equal(-1, QueueCursor.MarkerInView(2, viewingPlaying: false));
    }

    [Fact]
    public void Selection_InOtherPlaylist_AtSameIndex_Differs()   // same row number, but a different playlist → Play switches
        => Assert.True(QueueCursor.SelectionDiffers(2, 2, viewingPlaying: false));

    [Fact]
    public void Selection_OfPlayingSong_DoesNotDiffer() => Assert.False(QueueCursor.SelectionDiffers(2, 2, viewingPlaying: true));

    [Fact]
    public void NoSelection_NeverDiffers() => Assert.False(QueueCursor.SelectionDiffers(-1, 2, viewingPlaying: false));

    [Fact]
    public void Prev_WrapsInPlayingList()
    {
        Assert.Equal(4, QueueCursor.Prev(0, 5));
        Assert.Equal(1, QueueCursor.Prev(2, 5));
        Assert.Equal(-1, QueueCursor.Prev(0, 0));
    }

    // Playing list has 3 songs, the viewed one 10: at the end of the PLAYING list, next stops (Loop off) — it must not
    // run on into the viewed list's rows.
    [Fact]
    public void Next_UsesPlayingListLength()
    {
        const int playingCount = 3, viewedCount = 10;
        Assert.Equal(-1, QueueNext.Index(2, playingCount, 0, false, true));
        Assert.Equal(3, QueueNext.Index(2, viewedCount, 0, false, true));   // what the old (viewed-list) code would pick
        Assert.Equal(0, QueueNext.Index(2, playingCount, 1, false, true));  // Loop all wraps within the playing list
    }
}
