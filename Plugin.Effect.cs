namespace Stellar.Maestro;

// Wiring for the "Apply tone/technique from MIDI" toggle. The mapping + application lives in MidiPlayer.Effect.cs;
// this just holds the persisted setting and pushes it to the player (which re-applies live if a song is playing).
public sealed partial class Plugin
{
    private bool _bandApplyTone;

    private void OnToggleApplyTone(bool v)
    {
        _bandApplyTone = v;
        _bandPlayer.ApplyToneTechnique = v;   // live: applies immediately if mid-song, else at next play
        _cfg.Set<bool>("apply_tone", v);
        _cfg.Save();
    }
}
