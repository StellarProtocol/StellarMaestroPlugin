using System;

namespace Stellar.Maestro;

// Auto-select the instrument's TONE (timbre) + TECHNIQUE (performance method) from the MIDI's channel instrument,
// and switch it DYNAMICALLY as the song's program changes (0xC0) go by. See Band-Instrument-Playback.md.
//
//   • Local (B1): fire HandleInstrumentTone/Technique at each boundary (game does local set + live broadcast).
//   • Buffered (B2): NO live toggle — set the tone/technique LOCALLY at play time (EntityInstrumentSetTone/Technique,
//     like EntityInstrumentPlayNote), AND send a TIMESTAMPED {syncType=Tone/Technique, playTime=__b2base+ms} record
//     ahead of time through AsyncSendInstrumentSyncData — the same pipe/playTime scheduling as notes, so remotes get
//     the change on-beat instead of lagging behind the pre-sent notes.
//
// Toggle OFF → every boundary resolves to Clean + Sustained (no effect). The tone/technique id is resolved against the
// CURRENTLY-summoned instrument in Lua (adapts + clamps: bass has no Distortion → Overdrive; guitar has no Slap → 1).
public sealed partial class MidiPlayer
{
    private bool _applyTone;
    public bool ApplyToneTechnique
    {
        get => _applyTone;
        set { _applyTone = value; if (_playing) SeekInstrumentEffect(); }   // live: re-resolve from the current position
    }

    private int _effPlayCur;   // next program-change to apply LOCALLY (at play time)
    private int _effSendCur;   // next program-change to SEND over the network (buffered, ahead)

    // GM program → (toneCat 0/1/2 = Clean/Overdrive/Distortion, techKind 1/2/3/6 = Sustained/Muffled/Harmonics/Slap).
    private static (int toneCat, int techKind) EffectFromProgram(int program)
    {
        if (program >= 24 && program <= 31)   // guitar family
            return (program == 30 ? 2 : program == 29 ? 1 : 0,
                    program == 28 ? 2 : program == 31 ? 3 : 1);
        if (program >= 32 && program <= 39)   // bass family
            return ((program == 38 || program == 39) ? 1 : 0,
                    (program == 36 || program == 37) ? 6 : 1);
        return (0, 1);                          // piano / drums / unknown / no program-change
    }

    // (toneCat, techKind) to apply for a program — Clean/Sustained when the feature is off.
    private (int toneCat, int techKind) EffectAt(int program) => _applyTone ? EffectFromProgram(program) : (0, 1);

    // Recompute cursors from the current position and apply the tone/technique that should be active NOW.
    // Called at Start, on seek/resume, and when the toggle flips mid-song.
    private void SeekInstrumentEffect()
    {
        var pcs = _song?.ProgramChanges;
        double nowMs = Math.Max(0, PositionMs);
        int active = _song?.Program ?? -1;
        _effPlayCur = 0;
        if (pcs != null && pcs.Count > 0)
        {
            active = pcs[0].program;
            while (_effPlayCur < pcs.Count && pcs[_effPlayCur].ms <= nowMs) { active = pcs[_effPlayCur].program; _effPlayCur++; }
        }
        _effSendCur = _effPlayCur;
        var (tc, tk) = EffectAt(active);
        if (_netMode)
        {
            EmitLocalSet(tc, tk);
            EmitNetSend(tc, tk, (long)Math.Round(nowMs));
        }
        else EmitLiveApply(tc, tk);
    }

    // Fire any program-change boundaries now due. Local at play time; buffered also sends the record ~lookahead ahead.
    private void TickInstrumentEffect()
    {
        var pcs = _song?.ProgramChanges;
        if (pcs == null || pcs.Count == 0) return;
        if (_netMode)
        {
            while (_effPlayCur < pcs.Count && pcs[_effPlayCur].ms <= _realMs)
            {
                var (tc, tk) = EffectAt(pcs[_effPlayCur].program);
                EmitLocalSet(tc, tk); _effPlayCur++;
            }
            while (_effSendCur < pcs.Count && pcs[_effSendCur].ms <= _realMs + _lookaheadMs)
            { var (tc, tk) = EffectAt(pcs[_effSendCur].program); EmitNetSend(tc, tk, (long)Math.Round((double)pcs[_effSendCur].ms)); _effSendCur++; }
        }
        else
        {
            while (_effPlayCur < pcs.Count && pcs[_effPlayCur].ms <= _elapsedMs)
            { var (tc, tk) = EffectAt(pcs[_effPlayCur].program); EmitLiveApply(tc, tk); _effPlayCur++; }
        }
    }

    // Resolves toneCat/techKind against the summoned instrument's config (adapts + clamps per instrument).
    private const string RESOLVE =
        "local v=Z.UIMgr:GetView('band_performance_main_pc') if v==nil then return end " +
        "local cfg=v:GetCurInstrumentConfig() if cfg==nil then return end local id=(math.floor)(cfg.ID) " +
        "local TT={[10001]={1000000,1000000,1000000},[10002]={1000001,1000002,1000003}," +
        "[10003]={1000006,1000006,1000006},[10004]={1000004,1000005,1000005}} " +
        "local MM={[10001]={[1]=true},[10002]={[1]=true,[2]=true,[3]=true}," +
        "[10003]={[1]=true},[10004]={[1]=true,[2]=true,[3]=true,[6]=true}} " +
        "local trow=TT[id] if trow==nil then return end ";

    private string Pick(int toneCat, int techKind) =>
        "local timbre=trow[" + (toneCat + 1) + "] local kind=" + techKind + " if not (MM[id] or {})[kind] then kind=1 end ";

    // B1: game's own path (local set + live broadcast).
    private void EmitLiveApply(int toneCat, int techKind)
        => _callLua("pcall(function() " + RESOLVE + Pick(toneCat, techKind) +
            "local vm=Z.VMMgr.GetVM('band') local tok=(v.cancelSource or Z.DataMgr.Get('band_data').CancelSource):CreateToken() " +
            "vm:HandleInstrumentTone(timbre, tok) vm:HandleInstrumentTechnique(kind, tok) end)");

    // B2: local audio only (no network) — set the tone/technique for the local render via the confirmed local
    // primitives (no lock gate, no broadcast). EntityInstrumentSetTone is the sticky local-only tone setter;
    // EntityInstrumentSetTechnique is the matching persistent-switch setter.
    private void EmitLocalSet(int toneCat, int techKind)
        => _callLua("pcall(function() " + RESOLVE + Pick(toneCat, techKind) +
            "local svc=Z.DIServiceMgr.InstrumentService local ent=Z.EntityMgr.PlayerEnt " +
            "svc:EntityInstrumentSetTone(ent, timbre, true) " +
            "svc:EntityInstrumentSetTechnique(ent, (Panda.ZAudio.EPlayingTechnique.IntToEnum)(kind), true) end)");

    // B2: send the change as a TIMESTAMPED record (playTime = __b2base+ms), the same pipe/scheduling as notes.
    private void EmitNetSend(int toneCat, int techKind, long ms)
        => _callLua("pcall(function() " + RESOLVE + Pick(toneCat, techKind) +
            "local vm=Z.VMMgr.GetVM('band') local tok=(v.cancelSource or Z.DataMgr.Get('band_data').CancelSource):CreateToken() " +
            "local recs={{syncType=E.EInstrumentSyncType.Tone,playTime=__b2base+" + ms + ",playParam=timbre,playType=E.EInstrumentPlayType.Press,expectedSyncTime=0}," +
            "{syncType=E.EInstrumentSyncType.Technique,playTime=__b2base+" + ms + ",playParam=kind,playType=E.EInstrumentPlayType.Press,expectedSyncTime=0}} " +
            "Z.CoroUtil.create_coro_xpcall(function() vm:AsyncSendInstrumentSyncData(recs, tok) end)() end)");
}
