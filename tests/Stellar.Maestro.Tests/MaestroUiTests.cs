using Xunit;

namespace Stellar.Maestro.Tests;

// Main-window live text decisions (MaestroUi.cs): header state, the NOW PLAYING status-line priority, the "Next" song
// and the Maestro Link hub card.
public class MainHeaderTests
{
    [Fact]
    public void EnsembleWaiting_Wins()
        => Assert.Equal(MainHeaderKind.EnsembleWaiting, MainHeader.Pick(false, false, 0, true));

    [Fact]
    public void CountIn_WhilePlaying()
        => Assert.Equal(MainHeaderKind.CountIn, MainHeader.Pick(true, false, 1500, false));

    [Theory]
    [InlineData(true, false, (int)MainHeaderKind.Playing)]
    [InlineData(false, true, (int)MainHeaderKind.Paused)]
    [InlineData(false, false, (int)MainHeaderKind.Stopped)]
    public void Transport(bool playing, bool paused, int want)   // int: xunit theory params must be public types
        => Assert.Equal((MainHeaderKind)want, MainHeader.Pick(playing, paused, 0, false));
}

public class MainStatusLineTests
{
    [Fact]
    public void CountIn_BeatsEverything()
        => Assert.Equal(MainLineKind.CountIn, MainStatusLine.Pick(true, true, true, true, true, 0, true));

    [Fact]
    public void EnsembleWaiting_BeatsGapAndMessages()
        => Assert.Equal(MainLineKind.EnsembleWaiting, MainStatusLine.Pick(false, true, true, true, true, 0, true));

    [Fact]
    public void GapCountdown_BeatsMessages()
        => Assert.Equal(MainLineKind.NextIn, MainStatusLine.Pick(false, false, true, true, true, 0, true));

    [Fact]
    public void FreshProblem_BeatsNext()
        => Assert.Equal(MainLineKind.Problem, MainStatusLine.Pick(false, false, false, true, true, 5000, true));

    [Fact]
    public void Message_ExpiresBeforeProblem()
    {
        Assert.Equal(MainLineKind.Next, MainStatusLine.Pick(false, false, false, true, false, 5000, true));
        Assert.Equal(MainLineKind.Message, MainStatusLine.Pick(false, false, false, true, false, 1000, true));
    }

    [Fact]
    public void ExpiredProblem_FallsBackToNone_WhenNothingNext()
        => Assert.Equal(MainLineKind.None, MainStatusLine.Pick(false, false, false, true, true, 9000, false));
}

public class QueueNextTests
{
    [Fact] public void NothingPlaying() => Assert.Equal(-1, QueueNext.Index(-1, 5, 0, false, true));
    [Fact] public void Sequential()     => Assert.Equal(3, QueueNext.Index(2, 5, 0, false, true));
    [Fact] public void EndNoLoop()      => Assert.Equal(-1, QueueNext.Index(4, 5, 0, false, true));
    [Fact] public void EndLoopAll()     => Assert.Equal(0, QueueNext.Index(4, 5, 1, false, true));
    [Fact] public void LoopOneRepeats_EvenWithoutAutoNext() => Assert.Equal(2, QueueNext.Index(2, 5, 2, false, false));
    [Fact] public void ShuffleUnknown()  => Assert.Equal(-1, QueueNext.Index(2, 5, 1, true, true));
    [Fact] public void AutoNextOff()     => Assert.Equal(-1, QueueNext.Index(2, 5, 1, false, false));
}

public class LinkCardTests
{
    [Theory]
    [InlineData(false, false, false, (int)LinkCardKind.NotInRoom)]
    [InlineData(true, false, false, (int)LinkCardKind.Joining)]
    [InlineData(false, true, true, (int)LinkCardKind.InRoom)]
    [InlineData(false, true, false, (int)LinkCardKind.Reconnecting)]
    public void Pick(bool connecting, bool linked, bool online, int want)
        => Assert.Equal((LinkCardKind)want, LinkCard.Pick(connecting, linked, online));
}
