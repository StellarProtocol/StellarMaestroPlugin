using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// MAESTRO LINK — a website-run room drives this character's instrument (Standalone\MaestroLinkWeb\docs\design.md +
// protocol.md are the binding contract). The room broadcasts ONE start instant T (game-server ms) per song; every
// performer stamps its notes against that T, so all listeners hear the band on the same frame without the game's
// ensemble (KB Band-Instrument-Playback.md → "Band sync WITHOUT the game ensemble", measured 0 ms per-note delta).
//
// Threads: LinkClient owns the socket on a background task and only queues events; EVERYTHING here (Lua, player,
// config, UI state) runs on the main thread in LinkTick. Server time is always CombatSnapshot.ServerNowMs — never
// reflect ZServerTime (Server-Clock-and-Ping-HUD.md: stalls the ping HUD + freezes day/night).
//
// Linked settings are in-memory overrides on the MidiPlayer only (Plugin.Link.Settings.cs ApplyLinkPlayerSettings); the
// player's saved config / Plugin fields are never written, and Disconnect restores them (RestoreLocalPlayerSettings).
//
// Partials: Plugin.Link.cs (session + status) · Plugin.Link.Song.cs (prepare/play/stop, part lifecycle) ·
//           Plugin.Link.Settings.cs (linked overrides + status helpers) ·
//           Plugin.Link.Equip.cs (re-equip recipe) · Plugin.Link.Ui.cs (join form + status view).
public sealed partial class Plugin
{
    private enum LinkPhase { Off, Connecting, Linked }

    private const string LinkCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private LinkPhase   _lkPhase;
    private LinkClient? _lk;
    private bool   _lkOnline;              // socket open AND welcomed (false while reconnecting)
    private string _lkCode = "";           // join-form code (persisted as the last code)
    private string _lkErrorKey = "";       // localized error line on the join form ("" = none)
    private string _lkRoomCode = "";
    private string _lkPlayerId = "", _lkClientToken = "";
    private Uri    _lkApiBase = new("https://maestro.stellarresonance.app/api/");
    private readonly Stopwatch _lkHelloClock = new();
    private int    _lkPingMs = -1;
    private double _lkStatusAccumMs, _lkWarnAccumMs, _lkStatusCheckMs;
    private string _lkLastStatus = "";
    private readonly List<string> _lkWarnings = new();
    private bool   _lkBandViewOpen;
    private int    _lkInstId = -1;         // live GetPlayerInstrumentId (1 Hz cache); 0 = none, -1 = unreadable
    private int    _lkServerTone;          // DIAG: server-player tone read (attr 1201 path), 1 Hz; 0 = none — not reported until verified
    private string _lkCharName = "";       // character that joined (logout safety net: a different name while linked = left)
    private int    _lkMainThread;          // Unity main thread id (Logout thread check)
    private volatile bool _lkLogoutPending; // off-thread Logout seen → LinkTick ends the link BEFORE draining the inbox

    private bool IsLinked => _lkPhase == LinkPhase.Linked;

    private void InitLink()
    {
        _lkCode = _cfg.Get<string>("link_last_code", "") ?? "";
        _lkMainThread = Environment.CurrentManagedThreadId;
        _services.Framework.Update += LinkTick;
        _services.ClientState.Logout += OnLinkLogout;
    }

    private void DisposeLink()
    {
        try { _services.Framework.Update -= LinkTick; } catch { }
        try { _services.ClientState.Logout -= OnLinkLogout; } catch { }
        if (_lkPhase != LinkPhase.Off) EndLink("", showMain: false);
    }

    // ---- join / leave ----

    private static string SanitizeCode(string s)
    {
        var sb = new System.Text.StringBuilder(6);
        foreach (var ch in (s ?? "").ToUpperInvariant())
            if (LinkCodeAlphabet.IndexOf(ch) >= 0 && sb.Length < 6) sb.Append(ch);
        return sb.ToString();
    }

    private bool CanJoin() => _lkPhase == LinkPhase.Off && _lkCode.Length > 0;

    private void JoinLink()
    {
        if (!CanJoin()) return;
        _lkErrorKey = "";
        Uri ws = LinkServerUri();
        _lkApiBase = LinkProtocol.ApiBase(ws);

        var name = _services.PlayerState.IsAvailable ? (_services.PlayerState.Name ?? "") : "";
        _lkCharName = name;
        string hello = LinkProtocol.Hello(_lkCode, LinkClientId(name), name, PluginVersion());
        _lkPhase  = LinkPhase.Connecting;
        _lkOnline = false;
        _lkPingMs = -1;
        _lk = new LinkClient(ws, hello);
        _lk.Start();
        _services.Log.Info($"[MaestroLink] joining {_lkCode} via {ws} as '{name}'");
    }

    // The Link server is BUILT IN (LinkProtocol.DefaultServer) — not a config setting, so a player can't be pointed at
    // another server through their config file (a leftover `link_server` key is simply ignored). Dev-only override:
    // the MAESTRO_LINK_SERVER environment variable (a ws:// or wss:// URL), read on every Join, never shown in the UI.
    private string _lkOverrideLogged = "";

    private Uri LinkServerUri()
    {
        var env = Environment.GetEnvironmentVariable("MAESTRO_LINK_SERVER");
        if (!string.IsNullOrWhiteSpace(env)
            && Uri.TryCreate(env.Trim(), UriKind.Absolute, out var o) && (o.Scheme == "ws" || o.Scheme == "wss"))
        {
            if (_lkOverrideLogged != o.AbsoluteUri)
            {
                _lkOverrideLogged = o.AbsoluteUri;
                _services.Log.Info($"[MaestroLink] server override from MAESTRO_LINK_SERVER: {o.AbsoluteUri}");
            }
            return o;
        }
        return new Uri(LinkProtocol.DefaultServer);
    }

    // Stable per game install + character (protocol §2): a GUID persisted under the character's name, so a reconnect
    // (or a later session) gets the same playerId/slot back.
    private string LinkClientId(string name)
    {
        string key = "link_client_id." + (name.Length > 0 ? name : "_");
        var id = _cfg.Get<string>(key, "") ?? "";
        if (id.Length == 0) { id = "c-" + Guid.NewGuid().ToString("N"); _cfg.Set<string>(key, id); _cfg.Save(); }
        return id;
    }

    private static string PluginVersion()
    {
        var v = typeof(Plugin).Assembly.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    // Leave the room (Disconnect button, server error/closed, logout, or plugin dispose): stop our part, drop the socket,
    // undo every linked override, and give the normal Maestro window back. Dropping `_lk` also discards its Inbox — any
    // room command still queued (e.g. piled up while logged out, when LinkTick doesn't run) dies with the client.
    // Each teardown step is guarded on its own: on logout the world is already tearing down, so a Lua-backed step (the
    // listener note/pedal release, the play-range restore) may throw — that must not skip the in-memory resets after it.
    private void EndLink(string errorKey, bool showMain = true)
    {
        bool wasLinked = _lkPhase == LinkPhase.Linked;
        var lk = _lk; _lk = null;
        try { lk?.Dispose(); } catch { }
        _lkPhase  = LinkPhase.Off;
        _lkOnline = false;
        _lkErrorKey = errorKey;
        _lkLogoutPending = false;
        if (wasLinked)
        {
            try { StopLinkedPart(); }
            catch (Exception ex) { _services.Log.Warning($"[MaestroLink] stop part on leave: {ex.Message}"); try { _bandPlayer.StopReset(); } catch { } }
            LinkResetSongState();
            try { RestoreLocalPlayerSettings(); } catch (Exception ex) { _services.Log.Warning($"[MaestroLink] restore settings on leave: {ex.Message}"); }
            try { CancelLinkEquip(); } catch (Exception ex) { _services.Log.Warning($"[MaestroLink] cancel equip on leave: {ex.Message}"); }
            if (showMain)
            {
                // SetVisible only flips the window's flag — the Phase==World ShouldRender gate still decides the mount,
                // so on a logout this takes effect (status line included) once the player is back in the world.
                _linkWindow.SetVisible(false);
                _mainWindow.SetVisible(true);
                // The join form is hidden now, so a room-closed / kicked / expired reason goes on the main status line.
                if (errorKey.Length > 0) _bandMidiStatus = _loc.T(errorKey);
            }
        }
        _services.Log.Info($"[MaestroLink] left (error='{errorKey}')");
    }

    // ---- logout ----

    // IClientState.Logout (Game-Phases-and-Tick-Gating.md §4): fires once, immediately, on title screen / char select /
    // character swap — NOT PhaseChanged leaving World (the phase is held at World through a logout, and an Account
    // Switcher swap never leaves it). The framework raises it from its Game.OnLogout Harmony postfix, i.e. on the Unity
    // main thread, so we tear down right here: the socket must close NOW — LinkTick (IFramework.Update) is world-gated,
    // so a deferred close would leave the web room showing us connected-but-frozen until the next login. No auto-rejoin:
    // the join form keeps the room code, so rejoining is one click.
    private void OnLinkLogout()
    {
        if (_lkPhase == LinkPhase.Off) return;
        if (Environment.CurrentManagedThreadId == _lkMainThread) { EndLinkLoggedOut(); return; }
        // Off-thread (not expected): Post marshals, but it drains on this plugin's next Update tick — world-gated too —
        // so also flag it: LinkTick checks the flag BEFORE draining the inbox, so no stale room command replays first,
        // whichever of the two runs first.
        _lkLogoutPending = true;
        _services.Framework.Post(() => { if (_lkLogoutPending) EndLinkLoggedOut(); });
    }

    private void EndLinkLoggedOut()
    {
        _services.Log.Info($"[MaestroLink] logged out (character '{_lkCharName}') — leaving the room");
        EndLink("mst.link.err.loggedOut");
    }

    // Safety net (in-world, 1 Hz): a different character name while linked = a logout/swap slipped past the event.
    // Empty name = the player record isn't loaded yet (it's cleared on logout), never a change. SDK 2.3.0's IPlayerState
    // has no CharId, so the name is the only identity available here.
    private bool LinkCharacterChanged()
    {
        if (_lkCharName.Length == 0 || !_services.ClientState.IsWorldActive || !_services.PlayerState.IsAvailable) return false;
        var now = _services.PlayerState.Name ?? "";
        return now.Length > 0 && now != _lkCharName;
    }

    // ---- main-thread tick ----

    private void LinkTick(float dt)
    {
        _lkMainThread = Environment.CurrentManagedThreadId;
        if (_lk == null) return;
        try
        {
            if (_lkLogoutPending) { EndLinkLoggedOut(); return; }
            while (_lk != null && _lk.Inbox.TryDequeue(out var ev)) HandleLinkEvent(ev);
            if (_lk == null) return;
            DrainStemResults();
            LinkEquipTick();
            LinkPartTick(dt);

            if (!IsLinked) return;
            _lkWarnAccumMs   += dt * 1000.0;
            _lkStatusAccumMs += dt * 1000.0;
            _lkStatusCheckMs += dt * 1000.0;
            if (_lkWarnAccumMs >= 1000)
            {
                _lkWarnAccumMs = 0;
                if (LinkCharacterChanged()) { EndLinkLoggedOut(); return; }
                RefreshLinkLiveState();
            }
            // Change detection at ~10 Hz (a JSON serialize per frame is needless GC churn in the game process).
            if (_lkStatusCheckMs < 100 && _lkStatusAccumMs < 1000 && _lkLastStatus.Length > 0) return;
            _lkStatusCheckMs = 0;
            string st = BuildStatusCore();
            if (_lkStatusAccumMs >= 1000 || st != _lkLastStatus) { _lkStatusAccumMs = 0; _lkLastStatus = st; SendStatus(st); }
        }
        catch (Exception ex) { _services.Log.Warning($"[MaestroLink] tick error: {ex.Message}"); }
    }

    private void HandleLinkEvent(LinkClient.Event ev)
    {
        switch (ev.Kind)
        {
            case LinkClient.Kind.Opened:  _lkHelloClock.Restart(); break;
            case LinkClient.Kind.Dropped: if (_lkOnline) _services.Log.Info($"[MaestroLink] connection dropped ({ev.Text}) — reconnecting"); _lkOnline = false; break;
            case LinkClient.Kind.Fatal:
                EndLink(ev.Text == "unreachable" ? "mst.link.err.unreachable" : ErrorKeyFor(ev.Text));
                break;
            case LinkClient.Kind.Message:
                try { using var doc = JsonDocument.Parse(ev.Text); HandleLinkMessage(doc.RootElement); }
                catch (Exception ex) { _services.Log.Warning($"[MaestroLink] bad message: {ex.Message}"); }
                break;
        }
    }

    private static string ErrorKeyFor(string code) => code switch
    {
        "room_not_found" or "room_closed" => "mst.link.err.notFound",
        "room_full"      => "mst.link.err.full",
        // bad_request = the server rejected our hello as malformed → an incompatible plugin build, same advice.
        "proto_mismatch" or "bad_request" => "mst.link.err.version",
        _                => "mst.link.err.unreachable",
    };

    // `closed {reason}` (protocol §2): closed_by_owner / expired / kicked / replaced (same character joined again
    // from another session — the newer connection wins, so this one must NOT reconnect).
    private static string ClosedKeyFor(string reason) => reason switch
    {
        "expired"  => "mst.link.err.expired",
        "kicked"   => "mst.link.err.kicked",
        "replaced" => "mst.link.err.replaced",
        _          => "mst.link.err.closed",
    };

    private void HandleLinkMessage(JsonElement m)
    {
        string t = LinkSettings.Str(m, "t") ?? "";
        switch (t)
        {
            case "welcome":  OnLinkWelcome(m); break;
            case "error":    EndLink(ErrorKeyFor(LinkSettings.Str(m, "code") ?? "")); break;
            case "closed":   EndLink(ClosedKeyFor(LinkSettings.Str(m, "reason") ?? "")); break;
            case "settings":
                if (m.TryGetProperty("settings", out var s))
                    OnLinkSettings(LinkSettings.Parse(s));   // `apply` ignored: settings never equip (§7.2)
                break;
            case "prepare":  { var song = LinkSong.Parse(m); if (song != null) OnLinkPrepare(song); } break;
            case "play":     { var song = LinkSong.Parse(m); if (song != null) OnLinkPlay(song); } break;
            case "stop":     // scope part = our stem re-assigned away; room (or missing) = stop, or with paused:true = pause
                if (LinkSettings.Str(m, "scope") == "part") OnLinkStopPart();
                else OnLinkStop(LinkSettings.Bool(m, "paused") == true, LinkSettings.Int(m, "positionMs") ?? 0);
                break;
            case "equip":    // owner "equip now" (§7.3)
                if (LinkSettings.Int(m, "instrumentId") is int ei) OnLinkEquip(ei, LinkSettings.Int(m, "toneId"), LinkSettings.Str(m, "playRange"));
                else SendAck("equip", null, false, "bad_request");
                break;
            case "ping":     { var p = LinkSettings.Int(m, "pingMs"); if (p != null) _lkPingMs = p.Value; } break;   // RTT from the previous pong
        }
    }

    private void OnLinkWelcome(JsonElement m)
    {
        bool first = _lkPhase != LinkPhase.Linked;
        if (_lkHelloClock.IsRunning) { _lkPingMs = (int)_lkHelloClock.ElapsedMilliseconds; _lkHelloClock.Reset(); }   // hello→welcome ≈ one RTT
        _lkOnline      = true;
        _lkPlayerId    = LinkSettings.Str(m, "playerId") ?? "";
        _lkClientToken = LinkSettings.Str(m, "clientToken") ?? "";
        _lkLastStatus  = "";   // force a status right away

        if (first)
        {
            _lkPhase = LinkPhase.Linked;
            _lkRoomCode = _lkCode;
            _cfg.Set<string>("link_last_code", _lkCode); _cfg.Save();
            // Joining: local playback stops, the normal Maestro windows give way to the Link status view, and the
            // player is put into linked mode (buffered, tempo 100 %, keep-base).
            StopPlayback();
            _bandPlayer.StopReset();
            _mainWindow.SetVisible(false);
            _settingsWindow.SetVisible(false);
            _networkWindow.SetVisible(false);
            _previewWindow.SetVisible(false);
            _libraryWindow.SetVisible(false);
            _linkWindow.SetVisible(true);
            _services.Log.Info($"[MaestroLink] linked: room={_lkRoomCode} player={_lkPlayerId}");
        }
        else _services.Log.Info("[MaestroLink] reconnected");

        // Settings first (in memory — settings never equip, §7.2) so the song's equip uses the room's play range; then the
        // song, whose concrete equip (§7.4) is what we switch to.
        LinkSettings? set = m.TryGetProperty("settings", out var s) ? LinkSettings.Parse(s) : null;
        if (set != null) _lkSet = set;
        ApplyLinkPlayerSettings();
        // welcome.song is the ONLY rejoin message on (re)connect: the running song with its T and P (stemUrl null = no
        // part). OnLinkPlay downloads the stem and late-joins honouring startPositionMs; the same song + T + P we're
        // still playing is idempotent. No song on a RE-connect = the room stopped / paused / moved on while we were
        // away → end the stale part. While the room is PAUSED, `song` is null and `paused` = {songId, title, positionMs,
        // next} → show the Paused state (the resume arrives as a normal `play`).
        if (m.TryGetProperty("song", out var sg) && LinkSong.Parse(sg) is { } song) OnLinkPlay(song);
        else if (m.TryGetProperty("paused", out var pz) && LinkSong.Parse(pz) is { } ps) OnLinkWelcomePaused(ps, LinkSettings.Int(pz, "positionMs") ?? 0);
        else if (!first && _lkRoomSong != null) OnLinkStop();
        if (set != null) OnLinkSettings(set, fromWelcome: true);
    }

    // ---- status (protocol §2 ClientStatus) ----

    // 1 Hz: the Lua-backed reads (band view, instrument, tone) — kept off the per-frame path.
    private void RefreshLinkLiveState()
    {
        _lkBandViewOpen = IsBandViewOpen();
        _lkInstId = ReadPlayerInstrumentId();
        // DIAG (protocol §7.5 research): EntityInstrumentGetTone(ent, isLocal:FALSE) reads the SERVER InstrumentPlayer,
        // which applyInstrumentSettingsFromAttr seeds from the replicated AttrInstrumentSettings(1201) map — verified on
        // REMOTE performers (KB Band-Instrument-Playback "server relays RAW tone ids…"), NOT yet on our own entity.
        // Maestro's MIDI tone writes are isLocal:TRUE, so this read is not tainted by them. Logged on change only, not
        // reported/decided on until in-game verified (the status must never guess). One Lua call per 1 Hz tick.
        _services.Lua.DoString("pcall(function() rawset(_G,'__ml_stone',nil) local svc=Z.DIServiceMgr.InstrumentService local ent=Z.EntityMgr.PlayerEnt " +
                               "if svc~=nil and ent~=nil and svc:GetPlayerInstrumentId()~=0 then rawset(_G,'__ml_stone', svc:EntityInstrumentGetTone(ent,false)) end end)");
        int st = _services.Lua.TryReadGlobalNumber("__ml_stone", out double tone) ? (int)tone : 0;
        if (st != _lkServerTone)
        {
            _lkServerTone = st;
            _services.Log.Info($"[MaestroLink] diag server-player tone={st} inst={_lkInstId} knownEquipTone={KnownTone()}");
        }

        _lkWarnings.Clear();
        if (!_lkBandViewOpen) _lkWarnings.Add("band_view_closed");
        if (_lkInstId == 0) _lkWarnings.Add("no_instrument");
        if (!_bandPlayer.PreBufferedNet) _lkWarnings.Add("not_buffered");
        if (_services.CombatSnapshot.ServerNowMs <= 0) _lkWarnings.Add("not_synced");
    }

    // Everything except serverNowMs/positionMs — compared each frame to send a status "immediately on any state change".
    // §7.5 REAL equip: instrumentId = live GetPlayerInstrumentId (0 = nothing); toneId = our last successful equip's
    // tone while still holding that instrument, else null (unknown — never a guess); equipTarget while equipping.
    private string BuildStatusCore()
    {
        int known = KnownTone();
        return JsonSerializer.Serialize(new
        {
            state = LinkStateName(),
            instrumentId = Math.Max(0, _lkInstId),
            toneId = known != 0 ? known : (int?)null,
            equipTarget = _lkEquipBusy ? new { instrumentId = _lkEquipTarget.Inst, toneId = _lkEquipTarget.Tone } : null,
            bandViewOpen = _lkBandViewOpen,
            bufferedMode = _bandPlayer.PreBufferedNet,
            songId = LinkStatusSongId(),
            warnings = _lkWarnings,
        });
    }

    private void SendStatus(string core)
    {
        if (_lk == null || !_lkOnline) return;
        long now = _services.CombatSnapshot.ServerNowMs;
        // Splice the volatile fields in front of the cached core (core is a JSON object: "{...}").
        string head = "{\"t\":\"status\",\"serverNowMs\":" + Math.Max(0, now) + ",\"positionMs\":" + LinkPositionMs() + ",";
        _lk.Send(head + core.Substring(1));
    }

    private void SendAck(string of, string? songId, bool ok, string? error = null)
    {
        if (_lk == null) return;
        _lk.Send(LinkProtocol.Ack(of, songId, ok, error));
        if (!ok) _services.Log.Info($"[MaestroLink] ack {of} song={songId} FAILED: {error}");
    }

    // InstrumentService.GetPlayerInstrumentId() (dump.cs 229262) on the instance EnsemblePatch captures from Tick.
    // 0 = no instrument summoned; -1 = not readable yet.
    private System.Reflection.MethodInfo? _getInstId;
    private int ReadPlayerInstrumentId()
    {
        var inst = EnsemblePatch.Instance;
        if (inst == null) return -1;
        try
        {
            _getInstId ??= StellarInterop.FindType("Panda.ZGame.InstrumentService")?
                .GetMethod("GetPlayerInstrumentId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null, Type.EmptyTypes, null);
            return _getInstId == null ? -1 : Convert.ToInt32(_getInstId.Invoke(inst, null));
        }
        catch { return -1; }
    }
}
