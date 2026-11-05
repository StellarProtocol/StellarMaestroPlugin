using System;
using Xunit;

namespace Stellar.Maestro.Tests;

// The linked view's Stop / Resume slot (LinkPartButtonMap.Decide). Theory data passes enum NAMES (the enum is internal).
public class LinkPartButtonTests
{
    [Theory]
    //          partActive stopped running hasStem partDone  → shows
    [InlineData(true,  false, true,  true,  false, "Stop")]          // our part is playing
    [InlineData(false, true,  true,  true,  false, "Resume")]        // stopped by us, song still running with our stem
    [InlineData(false, true,  false, true,  false, "StopDisabled")]  // song ended (cleared) or paused
    [InlineData(false, true,  true,  false, false, "StopDisabled")]  // no part in this song
    [InlineData(false, true,  true,  true,  true,  "StopDisabled")]  // our stem already ran out
    [InlineData(false, false, true,  true,  false, "StopDisabled")]  // not stopped by us (starting / waiting)
    [InlineData(false, false, false, false, false, "StopDisabled")]  // no song
    public void Decide(bool partActive, bool stopped, bool running, bool hasStem, bool partDone, string expected)
        => Assert.Equal(Enum.Parse<LinkPartButton>(expected),
                        LinkPartButtonMap.Decide(partActive, stopped, running, hasStem, partDone));
}
