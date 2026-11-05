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

    [Fact]
    public void Swap_InOtherPlaylist_LeavesCursor() => Assert.Equal(3, QueueCursor.AfterSwap(3, 3, 4, sameList: false));

    [Fact]
    public void Swap_PlayingSong_FollowsIt() => Assert.Equal(4, QueueCursor.AfterSwap(3, 3, 4, sameList: true));

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
