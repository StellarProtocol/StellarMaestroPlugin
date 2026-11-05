using Xunit;

namespace Stellar.Maestro.Tests;

// NOW PLAYING "Next" source selection (LinkNext.Select): the live `next` message wins until the next play / welcome;
// without one, the pre-`next` behaviour (running song's `next`, else the prepared song's title).
public class LinkNextTests
{
    [Fact]
    public void Live_Wins_OverRunningAndPrepared()
        => Assert.Equal("Until Then", LinkNext.Select(true, "Until Then", true, "Old Next", "Prep"));

    [Fact]
    public void Live_Null_MeansNothingNext()   // {t:"next", title:null} → "—", never the stale fallback
        => Assert.Null(LinkNext.Select(true, null, true, "Old Next", "Prep"));

    [Fact]
    public void NoLive_Running_UsesSongNext()   // servers that never send `next` behave exactly as before
        => Assert.Equal("Old Next", LinkNext.Select(false, null, true, "Old Next", "Prep"));

    [Fact]
    public void NoLive_NothingRunning_UsesPreparedTitle()
        => Assert.Equal("Prep", LinkNext.Select(false, null, false, null, "Prep"));
}
