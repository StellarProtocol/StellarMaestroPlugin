using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Stellar.Maestro.Tests;

// The shuffle order (ShuffleOrder.cs): each song once per round, current first, Loop Off ends, Loop All reshuffles
// without repeating the last song first, edits keep the order on the same songs.
public class ShuffleOrderTests
{
    private static ShuffleOrder Make(int seed = 1) => new(new Random(seed));

    // Plays to the end of the round from the current position; returns the rows played.
    private static List<int> Drain(ShuffleOrder s)
    {
        var played = new List<int>();
        for (int r; (r = s.Advance(loopAll: false)) >= 0;) played.Add(r);
        return played;
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(42)]
    public void Build_IsPermutation_WithCurrentFirst(int seed)
    {
        var s = Make(seed);
        s.Build(10, first: 7);
        Assert.Equal(7, s.Order[0]);
        Assert.Equal(0, s.Position);
        Assert.Equal(Enumerable.Range(0, 10), s.Order.OrderBy(x => x));
    }

    [Fact]
    public void LoopOff_PlaysEachSongOnce_ThenEnds()
    {
        var s = Make();
        s.Build(8, first: 3);
        var played = new List<int> { 3 };
        played.AddRange(Drain(s));
        Assert.Equal(Enumerable.Range(0, 8), played.OrderBy(x => x));   // every song exactly once
        Assert.Equal(-1, s.Advance(loopAll: false));                    // the round is over
        Assert.Equal(-1, s.PeekNext(loopAll: false));
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(99)] [InlineData(1234)]
    public void LoopAll_NewRound_FirstSongIsNotTheLastPlayed(int seed)
    {
        var s = Make(seed);
        for (int round = 0; round < 50; round++)
        {
            s.Build(3, first: 0);
            Drain(s);
            int last = s.Order[^1];
            int peek = s.PeekNext(loopAll: true);
            int next = s.Advance(loopAll: true);
            Assert.Equal(peek, next);           // "Next:" named exactly the song that then played
            Assert.NotEqual(last, next);
            Assert.Equal(Enumerable.Range(0, 3), s.Order.OrderBy(x => x));   // the new round is a full permutation
        }
    }

    [Fact]
    public void PeekNext_MatchesAdvance_WithinRound()
    {
        var s = Make();
        s.Build(6, first: 2);
        for (int i = 0; i < 5; i++)
        {
            int peek = s.PeekNext(loopAll: false);
            Assert.Equal(peek, s.Advance(loopAll: false));
        }
    }

    [Fact]
    public void Back_StepsToPrevious_AndStopsAtStart()
    {
        var s = Make();
        s.Build(5, first: 4);
        int a = s.Advance(false), b = s.Advance(false);
        Assert.Equal(a, s.Back());
        Assert.Equal(4, s.Back());
        Assert.Equal(-1, s.Back());   // at the start: the caller restarts the current song
        Assert.Equal(0, s.Position);
        Assert.Equal(a, s.Advance(false));
        Assert.Equal(b, s.Advance(false));
    }

    [Fact]
    public void Added_GoesIntoUnplayedPart_AndIsPlayedOnce()
    {
        var s = Make(5);
        s.Build(6, first: 0);
        s.Advance(false); s.Advance(false);   // 3 songs played (positions 0..2)
        var playedSoFar = s.Order.Take(3).ToList();
        s.OnAdded(6);                         // appended as row 6
        Assert.Equal(playedSoFar, s.Order.Take(3));       // the played part is untouched
        Assert.Contains(6, s.Order.Skip(3));
        var rest = Drain(s);
        Assert.Equal(Enumerable.Range(0, 7), playedSoFar.Concat(rest).OrderBy(x => x));
    }

    [Fact]
    public void Added_InTheMiddle_ShiftsRowsBelow()
    {
        var s = Make(3);
        s.Build(4, first: 3);
        s.OnAdded(1);   // inserted at row 1: old rows 1..3 become 2..4
        Assert.Equal(4, s.Order[0]);   // the current song (old row 3) is now row 4
        Assert.Equal(Enumerable.Range(0, 5), s.Order.OrderBy(x => x));
    }

    [Fact]
    public void Removed_UpcomingSong_IsDropped_RowsClosed()
    {
        var s = Make(2);
        s.Build(6, first: 0);
        int upcoming = s.Order[3];
        s.OnRemoved(upcoming);
        Assert.Equal(5, s.Count);
        Assert.Equal(Enumerable.Range(0, 5), s.Order.OrderBy(x => x));
        Assert.Equal(0, s.Position);
    }

    [Fact]
    public void Removed_CurrentSong_NextIsTheOneThatFollowedIt()
    {
        var s = Make(4);
        s.Build(6, first: 2);
        s.Advance(false);
        int current = s.Order[1], following = s.Order[2];
        s.OnRemoved(current);
        int expected = following > current ? following - 1 : following;   // row numbers close the gap
        Assert.Equal(expected, s.Advance(false));
    }

    [Fact]
    public void Removed_PlayedSong_KeepsPosition()
    {
        var s = Make(6);
        s.Build(6, first: 1);
        s.Advance(false); s.Advance(false);   // position 2
        int current = s.Order[2];
        s.OnRemoved(s.Order[0]);              // a song already played
        Assert.Equal(1, s.Position);
        Assert.Equal(current > 1 ? current - 1 : current, s.Order[s.Position]);
    }

    [Fact]
    public void DragMove_DoesNotChangeThePlayOrder()
    {
        var s = Make(8);
        s.Build(6, first: 2);
        s.Advance(false);
        // Play order as SONGS (labels), before and after moving row 0 to row 4 (remove-then-insert).
        var songs = new List<string> { "a", "b", "c", "d", "e", "f" };
        var before = s.Order.Select(r => songs[r]).ToList();
        int pos = s.Position;
        var x = songs[0]; songs.RemoveAt(0); songs.Insert(4, x);
        s.OnMoved(0, 4);
        Assert.Equal(before, s.Order.Select(r => songs[r]));
        Assert.Equal(pos, s.Position);
    }

    [Fact]
    public void SingleSong_LoopAll_RepeatsIt()
    {
        var s = Make();
        s.Build(1, first: 0);
        Assert.Equal(0, s.Advance(loopAll: true));
    }
}
