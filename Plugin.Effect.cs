namespace Stellar.Maestro;

// Wiring for the "Apply tone/technique from MIDI" toggle. The mapping + application lives in MidiPlayer.Effect.cs;
// this just holds the persisted setting and pushes it to the player (which re-applies live if a song is playing).
public sealed partial class Plugin
{
    private bool _bandApplyTone;
    private bool _bandRelayTone;   // EXPERIMENTAL: relay the buffered (B2) Tone record as a small int so the server passes it to listeners

    private void OnToggleApplyTone(bool v)
    {
        _bandApplyTone = v;
        _bandPlayer.ApplyToneTechnique = v;   // live: applies immediately if mid-song, else at next play
        _cfg.Set<bool>("apply_tone", v);
        _cfg.Save();
    }

    private void OnToggleRelayTone(bool v)
    {
        _bandRelayTone = v;
        _bandPlayer.RelayToneToListeners = v;   // affects the next Tone sync record only; local audio keeps the raw id
        _cfg.Set<bool>("relay_tone", v);
        _cfg.Save();
    }
}
