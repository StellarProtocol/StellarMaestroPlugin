using System;
using System.Text.Json;
using Xunit;

namespace Stellar.Maestro.Tests;

// The `players` message parse + the PLAYERS row mapping (LinkPlayers.cs) — precedence: disconnected → warnings (only
// while a running part is failing) → state. (Theory data passes enum NAMES: the enums are internal, and a public test
// method can't take an internal parameter type.)
public class LinkPlayersTests
{
    private static LinkPlayer P(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return LinkPlayer.Parse(doc.RootElement)!;
    }

    private static LinkPlayerRow Map(string json, bool songRunning = false) => LinkPlayerMap.Map(P(json), songRunning);

    [Fact]
    public void Parse_IsTolerant_MissingAndWrongTypedFieldsDefault()
    {
        var p = P("""{ "playerId": "a", "name": 5, "pingMs": null, "warnings": ["not_synced", 3, ""], "part": "yes" }""");
        Assert.Equal("a", p.PlayerId);
        Assert.Equal("", p.Name);
        Assert.False(p.Connected);
        Assert.Null(p.PingMs);
        Assert.Null(p.State);
        Assert.Null(p.Part);
        Assert.Equal(new[] { "not_synced" }, p.Warnings);
    }

    [Fact]
    public void ParseMessage_KeepsJoinOrder_AndSkipsNonObjects()
    {
        using var doc = JsonDocument.Parse("""{ "t": "players", "maxPlayers": 10, "players": [ { "playerId": "b" }, 1, { "playerId": "a" } ] }""");
        var (max, list) = LinkPlayer.ParseMessage(doc.RootElement);
        Assert.Equal(10, max);
        Assert.Equal(new[] { "b", "a" }, list.ConvertAll(x => x.PlayerId));
    }

    [Fact]
    public void ParseMessage_MissingFields_EmptyRoom()
    {
        using var doc = JsonDocument.Parse("""{ "t": "players" }""");
        var (max, list) = LinkPlayer.ParseMessage(doc.RootElement);
        Assert.Equal(0, max);
        Assert.Empty(list);
    }

    [Theory]
    [InlineData(0, "Reconnect", "Away", false)]
    [InlineData(29999, "Reconnect", "Away", false)]
    [InlineData(30000, "Offline", "Offline", true)]
    public void Disconnected_ReconnectThenOffline(int awayMs, string chip, string dot, bool nameMuted)
    {
        // Disconnected wins over warnings and state; ping is never shown.
        var r = Map($$"""{ "connected": false, "awayMs": {{awayMs}}, "state": "playing", "warnings": ["no_instrument"], "pingMs": 40 }""");
        Assert.Equal(Enum.Parse<LinkChip>(chip), r.Chip);
        Assert.Equal(Enum.Parse<LinkDot>(dot), r.Dot);
        Assert.Equal(nameMuted, r.NameMuted);
        Assert.False(r.ShowPing);
    }

    [Fact]
    public void Warnings_Problem_OnlyWhenRunningPartFails_FirstInFixedOrder()
    {
        var r = Map("""{ "connected": true, "state": "idle", "part": true, "warnings": ["not_synced", "no_instrument", "band_view_closed"] }""", true);
        Assert.Equal(LinkChip.Problem, r.Chip);
        Assert.Equal(LinkInstCol.Problem, r.Inst);
        Assert.Equal("no_instrument", r.Problem);
        Assert.Equal(LinkDot.Online, r.Dot);

        // Order = LinkWarningText: no_instrument (root cause — the band view is closed whenever nothing is held)
        // → band_view_closed → not_buffered → not_synced.
        Assert.Equal("band_view_closed", Map("""{ "connected": true, "state": "idle", "part": true, "warnings": ["not_synced", "band_view_closed"] }""", true).Problem);
        Assert.Equal("not_buffered", Map("""{ "connected": true, "state": "ready", "part": true, "warnings": ["not_synced", "not_buffered"] }""", true).Problem);
    }

    [Fact]
    public void NoSong_NoInstrument_IsIdle_MutedNoInstrument()   // Link equips per song → not a problem before one
    {
        var r = Map("""{ "connected": true, "state": "idle", "warnings": ["no_instrument", "band_view_closed"] }""");
        Assert.Equal(LinkChip.Idle, r.Chip);
        Assert.Equal(LinkInstCol.NoInstrument, r.Inst);
        Assert.Null(r.Problem);
    }

    [Theory]   // a prepared / next song (not running) never makes a Problem, even for a player with a part
    [InlineData("ready", "Ready")]
    [InlineData("idle", "Idle")]
    public void PreparedSongOnly_PartNoInstrument_NoProblem(string state, string chip)
    {
        var r = Map($$"""{ "connected": true, "state": "{{state}}", "part": true, "warnings": ["no_instrument"] }""", false);
        Assert.Equal(Enum.Parse<LinkChip>(chip), r.Chip);
        Assert.Equal(LinkInstCol.NoInstrument, r.Inst);
    }

    [Theory]   // already playing / counting in / equipping → the part is not failing, warnings ignored
    [InlineData("playing", "Playing")]
    [InlineData("countin", "Playing")]
    [InlineData("equipping", "Switch")]
    public void Running_PartBusy_IgnoresWarnings(string state, string chip)
        => Assert.Equal(Enum.Parse<LinkChip>(chip),
            Map($$"""{ "connected": true, "state": "{{state}}", "part": true, "instrumentId": 3, "warnings": ["not_buffered"] }""", true).Chip);

    [Fact]
    public void Running_NoPart_IsResting()
    {
        var r = Map("""{ "connected": true, "state": "idle", "part": false, "instrumentId": 3, "warnings": ["band_view_closed"] }""", true);
        Assert.Equal(LinkChip.Resting, r.Chip);
        Assert.Equal(LinkInstCol.NoPart, r.Inst);
    }


    [Theory]
    [InlineData("""{ "connected": true, "state": "ready", "instrumentId": 3 }""", "Equip")]
    [InlineData("""{ "connected": true, "state": "ready", "instrumentId": 0 }""", "NoInstrument")]
    [InlineData("""{ "connected": true, "state": "ready" }""", "NoInstrument")]
    [InlineData("""{ "connected": false, "awayMs": 99999, "instrumentId": 3 }""", "Equip")]
    [InlineData("""{ "connected": false }""", "NoInstrument")]
    public void InstrumentColumn_HeldOrNoInstrument(string json, string inst)
        => Assert.Equal(Enum.Parse<LinkInstCol>(inst), Map(json).Inst);


    [Fact]
    public void UnknownWarning_IsIgnored()
        => Assert.Equal(LinkChip.Ready, Map("""{ "connected": true, "state": "ready", "warnings": ["something_new"] }""").Chip);

    [Theory]
    [InlineData("equipping", "Switch")]
    [InlineData("stopped_by_player", "Stopped")]
    [InlineData("playing", "Playing")]
    [InlineData("countin", "Playing")]
    [InlineData("ready", "Ready")]
    [InlineData("preparing", "Ready")]
    [InlineData("idle", "Idle")]
    [InlineData("whatever", "Idle")]
    public void State_Chip(string state, string chip)
    {
        var r = Map($$"""{ "connected": true, "state": "{{state}}", "part": true, "instrumentId": 3 }""");
        Assert.Equal(Enum.Parse<LinkChip>(chip), r.Chip);
        Assert.Equal(LinkInstCol.Equip, r.Inst);
        Assert.True(r.ShowPing);
    }

    [Fact]
    public void Idle_NoPartInSong_IsResting()
    {
        var r = Map("""{ "connected": true, "state": "idle", "part": false }""");
        Assert.Equal(LinkChip.Resting, r.Chip);
        Assert.Equal(LinkInstCol.NoPart, r.Inst);
    }

    [Fact]
    public void Idle_NoSong_IsIdle()   // part null = no song in play
        => Assert.Equal(LinkChip.Idle, Map("""{ "connected": true, "state": "idle", "part": null }""").Chip);

    [Fact]
    public void OwnFirst_ThenServerOrder()
    {
        using var doc = JsonDocument.Parse("""{ "players": [ { "playerId": "a" }, { "playerId": "b" }, { "playerId": "me" }, { "playerId": "c" } ] }""");
        var (_, list) = LinkPlayer.ParseMessage(doc.RootElement);
        Assert.Equal(new[] { "me", "a", "b", "c" }, LinkPlayerMap.OwnFirst(list, "me").ConvertAll(x => x.PlayerId));
        Assert.Equal(new[] { "a", "b", "me", "c" }, LinkPlayerMap.OwnFirst(list, "").ConvertAll(x => x.PlayerId));
        Assert.Equal(new[] { "a", "b", "me", "c" }, LinkPlayerMap.OwnFirst(list, "zz").ConvertAll(x => x.PlayerId));
    }
}
