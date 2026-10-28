using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// MAESTRO-LINK SYNC SPIKE — throwaway feasibility gate, NOT a feature. Proves several clients can play a band together
// WITHOUT the game's ensemble, purely by agreeing on one shared server-time start instant T.
//
// Mechanism = the existing ensemble count-in (Plugin.Ensemble.cs ApplyEnsembleGrid → MidiPlayer.ResetB2): in buffered
// (B2) mode every note is stamped playTime = __b2base + noteOffsetMs, and EnsembleGrid/EnsembleBaseMs/EnsembleClockOffset
// pin __b2base to a chosen server instant with a negative local clock (count-in). Here the base is T from a command file.
//
// Fully DORMANT unless C:\Users\Public\maestro-link-spike\ exists (probed every 2 s; activation lazily installs the diag
// patches). No UI, no user-facing strings (dev diagnostic → log lines + files only). Files in the spike folder:
//   cmd.json            ← { id, op: play|stop, startAtServerMs, parts: { <CharacterName>: path }, default: path }
//   status-<name>.json  → 1 Hz snapshot (clocks, play state, band delay)
//   diag-<name>.tsv     → PLAY/RECV/CMD rows (LinkSpikeDiagPatch), flushed ~1 Hz
// Server time is ALWAYS CombatSnapshot.ServerNowMs — never reflect ZServerTime (Server-Clock-and-Ping-HUD.md: stalls
// ping + freezes day/night).
public sealed partial class Plugin
{
    private const string LinkSpikeDir = @"C:\Users\Public\maestro-link-spike";

    private bool   _lsActive;
    private double _lsProbeAccumMs = 2000;   // probe on the first tick
    private double _lsPollAccumMs, _lsStatusAccumMs;
    private long   _lsLastCmdId = long.MinValue;
    private long   _lsBaseMs;                // T of the spike play that currently owns the player (0 = none)
    private string _lsName = "";
    private int    _lsStatusN;
    private int    _lsBandDelayMs = -1;
    private MethodInfo? _lsGetInstId;

    private void InitLinkSpike() => _services.Framework.Update += LinkSpikeTick;

    private void DisposeLinkSpike()
    {
        try { _services.Framework.Update -= LinkSpikeTick; } catch { }
        try { LinkSpikeDiagPatch.Flush(); LinkSpikeDiagPatch.Reset(); } catch { }
    }

    private void LinkSpikeTick(float dt)
    {
        try
        {
            if (!_lsActive)
            {
                _lsProbeAccumMs += dt * 1000.0;
                if (_lsProbeAccumMs < 2000) return;
                _lsProbeAccumMs = 0;
                if (Directory.Exists(LinkSpikeDir)) ActivateLinkSpike();
                return;
            }
            _lsPollAccumMs   += dt * 1000.0;
            _lsStatusAccumMs += dt * 1000.0;
            if (_lsPollAccumMs >= 250) { _lsPollAccumMs = 0; PollLinkCommand(); }   // ~4 Hz
            if (_lsStatusAccumMs >= 1000)
            {
                _lsStatusAccumMs = 0;
                RefreshLinkIdentity();
                WriteLinkStatus();
                LinkSpikeDiagPatch.Flush();
            }
        }
        catch (Exception ex) { _services.Log.Warning($"[LinkSpike] tick error: {ex.Message}"); }
    }

    private void ActivateLinkSpike()
    {
        _lsActive = true;
        _services.Log.Info($"[LinkSpike] ACTIVE — spike folder present: {LinkSpikeDir}");
        try { LinkSpikeDiagPatch.Install(_services.Harmony.Create("linkspike"), () => _services.CombatSnapshot.ServerNowMs, m => _services.Log.Info(m)); }
        catch (Exception ex) { _services.Log.Warning($"[LinkSpike] diag patch install failed: {ex.Message}"); }

        // Latch whatever cmd.json is already there WITHOUT acting on it — a stale command from a previous session must
        // not fire (a stale play would be refused as "too late" anyway, but a stale stop would kill a manual song).
        var pre = ReadLinkCmd();
        if (pre != null) { _lsLastCmdId = pre.Id; _services.Log.Info($"[LinkSpike] ignoring pre-existing cmd id={pre.Id}"); }
        RefreshLinkIdentity();
    }

    // Own character name (framework PlayerState — the same source MinimalNameplate/MultiTool use for "self"). Also binds
    // the diag log file and seeds the self uuid (LocalEntityId is None until first combat; the diag patch also learns
    // it from the local-audio PlayNote call).
    private void RefreshLinkIdentity()
    {
        var ps = _services.PlayerState;
        string name = ps.IsAvailable ? (ps.Name ?? "") : "";
        if (name.Length > 0 && name != _lsName)
        {
            _lsName = name;
            LinkSpikeDiagPatch.SetLogFile(Path.Combine(LinkSpikeDir, $"diag-{SafeFileName(name)}.tsv"));
            _services.Log.Info($"[LinkSpike] self name = '{name}' (PlayerState.Name)");
        }
        var local = _services.CombatSnapshot.LocalEntityId;
        if (!local.IsNone) LinkSpikeDiagPatch.SelfUuid = local.Value;
    }

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private sealed class LinkCmd
    {
        public long   Id;
        public string Op = "";
        public long   StartAt;
        public string? Default;
        public JsonElement Parts;   // object, or Undefined
    }

    // Returns null on a missing / locked / half-written file — the poller just retries next tick.
    private static LinkCmd? ReadLinkCmd()
    {
        try
        {
            var path = Path.Combine(LinkSpikeDir, "cmd.json");
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);   // strips a UTF-8 BOM (PowerShell 5 writes one)
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            var cmd = new LinkCmd
            {
                Id      = r.GetProperty("id").GetInt64(),
                Op      = r.TryGetProperty("op", out var op) ? (op.GetString() ?? "") : "",
                StartAt = r.TryGetProperty("startAtServerMs", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0,
                Default = r.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            };
            if (r.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Object) cmd.Parts = parts.Clone();
            return cmd;
        }
        catch { return null; }
    }

    private void PollLinkCommand()
    {
        var cmd = ReadLinkCmd();
        if (cmd == null || cmd.Id == _lsLastCmdId) return;
        _lsLastCmdId = cmd.Id;   // act ONCE per id, success or refusal

        long now = _services.CombatSnapshot.ServerNowMs;
        LinkSpikeDiagPatch.Row($"CMD\t{now}\t{cmd.Id}\t{cmd.Op}\t{cmd.StartAt}\t{_lsName}");

        switch (cmd.Op)
        {
            case "play": LinkSpikePlay(cmd); break;
            case "stop":
                StopPlayback();
                _lsBaseMs = 0;
                _services.Log.Info($"[LinkSpike] stop id={cmd.Id}");
                break;
            default: _services.Log.Warning($"[LinkSpike] id={cmd.Id}: unknown op '{cmd.Op}'"); break;
        }
    }

    private void LinkSpikePlay(LinkCmd cmd)
    {
        void Fail(string why) => _services.Log.Warning($"[LinkSpike] play id={cmd.Id} REFUSED: {why}");

        if (!_bandNetPrebuffer) { Fail("buffered network sync (net_prebuffer / B2) is OFF — the spike needs B2"); return; }
        int inst = ReadPlayerInstrumentId();
        if (inst <= 0) { Fail(inst == 0 ? "no instrument equipped (GetPlayerInstrumentId()==0)" : "instrument id unreadable (InstrumentService not captured yet)"); return; }
        if (!IsBandViewOpen()) { Fail("band performance view not open (free-play Normal mode)"); return; }

        string? file = null;
        if (cmd.Parts.ValueKind == JsonValueKind.Object && _lsName.Length > 0)
            foreach (var p in cmd.Parts.EnumerateObject())
                if (string.Equals(p.Name, _lsName, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                { file = p.Value.GetString(); break; }
        file ??= cmd.Default;
        if (string.IsNullOrEmpty(file)) { Fail($"no part for name '{_lsName}' and no default"); return; }
        if (!File.Exists(file)) { Fail($"file not found: {file}"); return; }

        var song = MidiParser.TryParseFile(file, out var perr);
        if (song == null) { Fail($"parse failed: {perr}"); return; }

        long T    = cmd.StartAt;
        long lead = _bandAheadMs + 300;   // same margin ApplyEnsembleGrid uses: note 0 must be sendable a full lookahead early
        long now  = _services.CombatSnapshot.ServerNowMs;
        if (now <= 0) { Fail("server clock not synced (ServerNowMs==0)"); return; }
        if (T - now < lead) { Fail($"T too close/past: T-now={T - now}ms < {lead}ms"); return; }

        // Mirror PlayQueueIndex's player setup, but tempo pinned to 100% and transpose 0 (deterministic click test) and
        // the grid set HERE instead of ApplyEnsembleGrid. We never call PlayQueueIndex/ApplyEnsembleGrid, which is the
        // only code that clears EnsembleGrid — so nothing on the Start path can clobber the spike base. (Load→Stop and
        // Start→ResetB2 only READ EnsembleGrid/BaseMs/ClockOffset.) Caveat: a live seek/resume/slider change during the
        // spike re-anchors __b2base to the local server clock (MidiPlayer.AnchorClock) — don't touch the UI mid-test.
        _gapRemainMs     = -1;
        _ensembleWaiting = false;
        _bandPlayer.HoldOffsetMs   = _bandHoldMs;
        _bandPlayer.ForceSustain   = _bandForceSustain;
        _bandPlayer.TempoPct       = 100;
        _bandPlayer.MaxPolyphony   = _bandMaxPoly;
        _bandPlayer.PreBufferedNet = true;
        _bandPlayer.Load(song);

        now = _services.CombatSnapshot.ServerNowMs;   // re-read right before Start: parse + Load took real time
        _bandPlayer.EnsembleBaseMs      = T;
        _bandPlayer.EnsembleClockOffset = now - T;    // negative → local count-in; Start() restarts the Stopwatch next
        _bandPlayer.EnsembleGrid        = true;
        _bandPlayer.Start(0);
        _lsBaseMs = T;

        _services.Log.Info($"[LinkSpike] play id={cmd.Id} name={_lsName} file={file} T={T} now={now} countIn={T - now}ms inst={inst} notes={song.NoteCount}");
    }

    // PlaylistTick hook: swallow the natural-end completion of a spike song so the playlist doesn't auto-advance into the
    // user's queue. Ownership is "player still anchored to our T" — any normal Play goes through ApplyEnsembleGrid,
    // which resets EnsembleGrid, so a later manual song is never swallowed.
    private bool LinkSpikeSwallowCompletion()
    {
        if (_lsBaseMs == 0 || _bandPlayer.IsPlaying) return false;
        bool ours = _bandPlayer.EnsembleGrid && _bandPlayer.EnsembleBaseMs == _lsBaseMs;
        _lsBaseMs = 0;
        if (!ours) return false;
        bool ended = _bandPlayer.ConsumeCompleted();
        if (ended) _services.Log.Info("[LinkSpike] spike song ended (completion swallowed — no playlist advance)");
        return ended;
    }

    // InstrumentService.GetPlayerInstrumentId() (dump.cs 229262) on the instance EnsemblePatch captures from Tick.
    // 0 = no instrument summoned; -1 = not readable yet.
    private int ReadPlayerInstrumentId()
    {
        var inst = EnsemblePatch.Instance;
        if (inst == null) return -1;
        try
        {
            _lsGetInstId ??= StellarInterop.FindType("Panda.ZGame.InstrumentService")?
                .GetMethod("GetPlayerInstrumentId", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            return _lsGetInstId == null ? -1 : Convert.ToInt32(_lsGetInstId.Invoke(inst, null));
        }
        catch { return -1; }
    }

    private void WriteLinkStatus()
    {
        if (!Directory.Exists(LinkSpikeDir)) return;
        if (_lsStatusN++ % 10 == 0) _lsBandDelayMs = ReadBandDelayMs();   // static reflected read; refresh every ~10 s
        string name = _lsName.Length > 0 ? _lsName : "unknown";
        var json = JsonSerializer.Serialize(new
        {
            name,
            instrumentId = ReadPlayerInstrumentId(),
            serverNowMs  = _services.CombatSnapshot.ServerNowMs,
            utcNowMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            isPlaying    = _bandPlayer.IsPlaying,
            positionMs   = _bandPlayer.PositionMs,   // negative during the count-in (B2 _realMs)
            lastCmdId    = _lsLastCmdId == long.MinValue ? 0 : _lsLastCmdId,
            spikeBaseMs  = _lsBaseMs,
            bandDelayMs  = _lsBandDelayMs,
            netAheadMs   = _bandAheadMs,
            selfUuid     = LinkSpikeDiagPatch.SelfUuid,
        });
        var dst = Path.Combine(LinkSpikeDir, $"status-{SafeFileName(name)}.json");
        var tmp = dst + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, dst, overwrite: true);   // atomic replace — a reader never sees a half-written file
    }
}
