using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Stellar.Maestro;

// Maestro Link `players` message (protocol: the room's roster, broadcast on every change, in JOIN order) and the
// pure row mapping the Link window's PLAYERS panel draws from (approved design 2026-11-05). Kept free of any UI /
// game type so the mapping is unit-testable (tests/Stellar.Maestro.Tests) — the window only turns the result into
// localized text and colours.
//
// Parsing is tolerant like LinkSettings: a missing / null / wrong-typed field = its default, an unknown warning or
// state string is kept but maps to nothing special.
internal sealed class LinkPlayer
{
    public string  PlayerId = "";
    public string  Name     = "";
    public bool    Connected;
    public int     AwayMs;          // how long a disconnected player has been gone
    public int?    PingMs;
    public string? State;           // idle | preparing | ready | countin | playing | equipping | stopped_by_player | null
    public int?    InstrumentId;
    public int?    ToneId;
    public bool?   Part;            // has a stem in the current song; null = no song
    public readonly List<string> Warnings = new();

    public static LinkPlayer? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var p = new LinkPlayer
        {
            PlayerId     = LinkSettings.Str(e, "playerId") ?? "",
            Name         = LinkSettings.Str(e, "name") ?? "",
            Connected    = LinkSettings.Bool(e, "connected") ?? false,
            AwayMs       = Math.Max(0, LinkSettings.Int(e, "awayMs") ?? 0),
            PingMs       = LinkSettings.Int(e, "pingMs"),
            State        = LinkSettings.Str(e, "state"),
            InstrumentId = LinkSettings.Int(e, "instrumentId"),
            ToneId       = LinkSettings.Int(e, "toneId"),
            Part         = LinkSettings.Bool(e, "part"),
        };
        if (e.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
            foreach (var x in w.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) p.Warnings.Add(s);
        return p;
    }

    // The whole message: maxPlayers (0 = not sent) + the rows in server (join) order. Rows that aren't objects are
    // skipped; a missing `players` array = an empty room.
    public static (int MaxPlayers, List<LinkPlayer> Players) ParseMessage(JsonElement m)
    {
        var list = new List<LinkPlayer>();
        if (m.TryGetProperty("players", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (Parse(e) is { } p) list.Add(p);
        return (Math.Max(0, LinkSettings.Int(m, "maxPlayers") ?? 0), list);
    }
}

// The state chip words (one localized key each, mst.link.chip.*).
internal enum LinkChip { Playing, Ready, Switch, Stopped, Resting, Idle, Problem, Reconnect, Offline }

// Status dot colour class.
internal enum LinkDot { Online, Away, Offline }

// What the instrument column shows: Equip = "Instrument · Tone" (one is held), NoInstrument = muted "No instrument"
// (nothing held — normal before a song, since Link equips per song), Problem = the red reason, NoPart = muted "no part".
internal enum LinkInstCol { Equip, NoInstrument, Problem, NoPart, None }

internal readonly record struct LinkPlayerRow(LinkChip Chip, LinkDot Dot, LinkInstCol Inst, string? Problem, bool NameMuted, bool ShowPing);

internal static class LinkPlayerMap
{
    // A disconnected player this recent is "Reconnect" (amber), older is "Offline" (grey).
    public const int ReconnectWindowMs = 30000;

    // Reason shown when a row IS a problem, in this order — the SAME precedence as the plugin's own warning line
    // (LinkWarningText). no_instrument beats band_view_closed: with nothing held the band view is naturally closed too,
    // so the root cause must win.
    private static readonly string[] ProblemOrder = { "no_instrument", "band_view_closed", "not_buffered", "not_synced" };

    // Precedence top-down: 1. disconnected, 2. a known warning — but ONLY while the part is actually failing right now,
    // 3. the state. "Failing" = a song is RUNNING in the room (started + not paused; a prepared / next song does not
    // count — songRunning, from the plugin), this player has a part in it (part == true), and is NOT already
    // playing / counting in / equipping, and has not chosen to stop ("Stop my part" = always the amber Stopped chip).
    // Link auto-equips per song (equipping opens the band view), so warnings before
    // a song — or while the equip is underway — are normal and ignored (user-agreed rule 2026-11-06).
    public static LinkPlayerRow Map(LinkPlayer p, bool songRunning)
    {
        var equip = p.InstrumentId is int inst && inst > 0 ? LinkInstCol.Equip : LinkInstCol.NoInstrument;
        if (!p.Connected)
            return p.AwayMs < ReconnectWindowMs
                ? new LinkPlayerRow(LinkChip.Reconnect, LinkDot.Away, equip, null, false, false)
                : new LinkPlayerRow(LinkChip.Offline, LinkDot.Offline, equip, null, true, false);

        if (songRunning && p.Part == true && p.State is not ("playing" or "countin" or "equipping" or "stopped_by_player"))
            foreach (var w in ProblemOrder)
                if (p.Warnings.Contains(w))
                    return new LinkPlayerRow(LinkChip.Problem, LinkDot.Online, LinkInstCol.Problem, w, false, true);

        var chip = p.State switch
        {
            "equipping"         => LinkChip.Switch,
            "stopped_by_player" => LinkChip.Stopped,
            "playing" or "countin" => LinkChip.Playing,
            "ready" or "preparing" => LinkChip.Ready,
            // part == false only exists while a song is in play (null = no song) → this player sits this one out.
            _ => p.Part == false ? LinkChip.Resting : LinkChip.Idle,
        };
        return new LinkPlayerRow(chip, LinkDot.Online, chip == LinkChip.Resting ? LinkInstCol.NoPart : equip,
                                 null, false, true);
    }

    // Own row first, everyone else in server (join) order.
    public static List<LinkPlayer> OwnFirst(List<LinkPlayer> players, string myId)
    {
        var r = new List<LinkPlayer>(players.Count);
        if (myId.Length > 0)
            foreach (var p in players) if (p.PlayerId == myId) { r.Add(p); break; }
        foreach (var p in players) if (myId.Length == 0 || p.PlayerId != myId) r.Add(p);
        return r;
    }
}
