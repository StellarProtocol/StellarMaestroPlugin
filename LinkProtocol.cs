using System;
using System.Text.Json;

namespace Stellar.Maestro;

// Maestro Link wire shapes (Standalone\MaestroLinkWeb\docs\protocol.md — the binding contract; don't drift from it).
// Parsing is tolerant: a missing or null field = "not sent" (PlayerSettings: null means "plugin default").

// PlayerSettings §3. Every field nullable: null = absent / "plugin default".
internal sealed class LinkSettings
{
    public int? InstrumentId, ToneId, Transpose, MaxPoly, HoldMs, RestrikeGapMs, NetAheadMs, NetBatchMs, ToneLeadMs, MonitorVolume;
    public string? PlayRange;
    public bool? ForceSustain, RelayTone, ApplyTone, MuteLocal;

    public static LinkSettings Parse(JsonElement e)
    {
        var s = new LinkSettings();
        if (e.ValueKind != JsonValueKind.Object) return s;
        s.InstrumentId  = Int(e, "instrumentId");
        s.ToneId        = Int(e, "toneId");
        s.Transpose     = Int(e, "transpose");
        s.MaxPoly       = Int(e, "maxPoly");
        s.HoldMs        = Int(e, "holdMs");
        s.RestrikeGapMs = Int(e, "restrikeGapMs");
        s.NetAheadMs    = Int(e, "netAheadMs");
        s.NetBatchMs    = Int(e, "netBatchMs");
        s.ToneLeadMs    = Int(e, "toneLeadMs");
        s.MonitorVolume = Int(e, "monitorVolume");
        s.PlayRange     = Str(e, "playRange");
        s.ForceSustain  = Bool(e, "forceSustain");
        s.RelayTone     = Bool(e, "relayTone");
        s.ApplyTone     = Bool(e, "applyTone");
        s.MuteLocal     = Bool(e, "muteLocal");
        return s;
    }

    internal static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)
            ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;
    internal static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;
    internal static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    internal static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}

// PlaySong (`play`, `welcome.song`) and the subset carried by `prepare`.
internal sealed class LinkSong
{
    public string SongId = "";
    public string Title  = "";
    public string? StemUrl;        // null = this character has no part in the song (stays silent, still in sync)
    public int    DurationMs;
    public long   StartAtServerMs; // T — 0 for a `prepare`
    public string? Next;

    public static LinkSong? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = LinkSettings.Str(e, "songId");
        if (string.IsNullOrEmpty(id)) return null;
        return new LinkSong
        {
            SongId          = id,
            Title           = LinkSettings.Str(e, "title") ?? "",
            StemUrl         = LinkSettings.Str(e, "stemUrl"),
            DurationMs      = LinkSettings.Int(e, "durationMs") ?? 0,
            StartAtServerMs = LinkSettings.Long(e, "startAtServerMs") ?? 0,
            Next            = LinkSettings.Str(e, "next"),
        };
    }
}

internal static class LinkProtocol
{
    public const int Proto = 1;
    public const string DefaultServer = "wss://maestro.stellarresonance.app/api/ws/client";

    // The REST base the stem URLs are relative to: ws(s)://host/api/ws/client → http(s)://host/api/. stemUrl in the
    // messages is "/api/stems/…" (root-relative) today; a bare "stems/…" would resolve under /api/ — both work.
    public static Uri ApiBase(Uri ws)
    {
        var b = new UriBuilder(ws) { Scheme = ws.Scheme == "ws" ? "http" : "https", Port = ws.IsDefaultPort ? -1 : ws.Port, Query = "" };
        string path = ws.AbsolutePath;
        int i = path.IndexOf("/ws/", StringComparison.Ordinal);
        b.Path = (i >= 0 ? path.Substring(0, i) : "/api") + "/";
        return b.Uri;
    }

    public static string Hello(string code, string clientId, string name, string version)
        => JsonSerializer.Serialize(new { t = "hello", proto = Proto, code, clientId, name, pluginVersion = version });

    public static string Ack(string of, string? songId, bool ok, string? error)
        => JsonSerializer.Serialize(new { t = "ack", of, songId, ok, error });
}
