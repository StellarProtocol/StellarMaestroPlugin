using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// Maestro — a MIDI auto-player for the Season-3 "Musician" (band) free-play instrument.
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "Maestro";

    private readonly IPluginServices _services;
    private readonly IConfigSection  _cfg;
    private readonly List<IWindowControl> _windows   = new();
    private readonly List<IDisposable>    _launchers = new();
    private readonly MidiPlayer  _bandPlayer;
    private readonly PreviewSynth _previewSynth;
    private IUpdateRateScope? _rateScope;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _cfg = _services.Config.GetSection("settings");

        _bandPlayer = new MidiPlayer(_services);
        _previewSynth = new PreviewSynth(_services);

        _bandHoldMs       = _cfg.Get<int> ("hold_ms",        0);
        _bandForceSustain = _cfg.Get<bool>("force_sustain",  false);
        _bandTempoPct     = _cfg.Get<int> ("tempo_pct",      100);
        _bandMaxPoly      = _cfg.Get<int> ("max_poly",       0);
        _bandNetPrebuffer = _cfg.Get<bool>("net_prebuffer",  true);
        _bandRestrikeGapMs = _cfg.Get<int> ("restrike_gap_ms", 16);   // default on: separates back-to-back same-pitch notes so they don't drop at the listener
        _bandMuteLocal     = _cfg.Get<bool>("mute_local",      false);
        _bandShowKeyViz    = _cfg.Get<bool>("show_keyviz",     true);
        _bandApplyTone     = _cfg.Get<bool>("apply_tone",      false);
        _bandEnsembleSync       = _cfg.Get<bool>("ensemble_sync",        false);
        _bandEnsembleMatchTempo = _cfg.Get<bool>("ensemble_match_tempo", false);
        _bandAutoAcceptEnsemble = _cfg.Get<bool>("auto_accept_ensemble", false);
        _previewSync            = _cfg.Get<bool>("preview_sync",          false);
        _previewSyncOffsetMs    = _cfg.Get<int> ("preview_sync_offset",   0);
        _monitorVol             = _cfg.Get<int> ("monitor_volume",        0);   // display value only; not pushed to game until dragged

        _bandPlayer.HoldOffsetMs   = _bandHoldMs;
        _bandPlayer.TempoPct       = _bandTempoPct;
        _bandPlayer.MaxPolyphony   = _bandMaxPoly;
        _bandPlayer.PreBufferedNet = _bandNetPrebuffer;
        _bandPlayer.RestrikeGapMs  = _bandRestrikeGapMs;
        _bandPlayer.MuteLocal      = _bandMuteLocal;
        _bandPlayer.ShowKeyViz     = _bandShowKeyViz;
        _bandPlayer.ApplyToneTechnique = _bandApplyTone;

        LoadNetworkConfig();
        LoadPlaylists();
        RescanMidiFolder();   // create the midi/ folder (if missing) and pre-populate the song list at startup

        _networkWindow  = RegisterWindow("maestro.network",  "Maestro — Network",  1, BuildNetworkRoot());
        _settingsWindow = RegisterWindow("maestro.settings", "Maestro — Settings", 3, BuildSettingsRoot());
        _libraryWindow  = RegisterWindow("maestro.library",  "Maestro — Library",  4, BuildLibraryRoot());
        _previewWindow  = RegisterWindow("maestro.preview",  "Maestro — MIDI Preview (Local)",  5, BuildPreviewRoot());
        _tipWindow      = RegisterTipWindow();
        var window = RegisterWindow("maestro.main", "Maestro", 0, BuildBandRoot());
        _launchers.Add(_services.Launcher.Register(new LauncherEntry(
            Title:   "Maestro",
            IconPng: LoadIconPng(),
            IconKey: null,
            OnOpen:  () => { RescanMidiFolder(); window.SetVisible(true); })   // rescan the midi/ folder each open
        { Group = LauncherGroup.Plugin,
          // Band tools are in-world only — hide the launcher tile outside the World phase.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World }));

        _services.Framework.Update += PlaylistTick;   // auto-advance the queue when a song finishes

        // STEP-0 spike: capture InstrumentService to read the ensemble grid from C#.
        try { EnsemblePatch.Install(_services.Harmony.Create("ensemble"), m => _services.Log.Info(m)); }
        catch (Exception ex) { _services.Log.Warning($"[Maestro] ensemble patch install failed: {ex.Message}"); }

        _services.Log.Info("[Maestro] constructed");
    }

    // Registers a hidden, draggable menu window (no launcher tile of its own — the launcher entry opens it).
    private IWindowControl RegisterWindow(string id, string title, int index, HudElement root, float width = 460f)
    {
        IWindowControl w = null!;
        w = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          id,
                Title:       title,
                DefaultRect: new WindowRect(_services.Framework.ScreenWidth - width - 20f, 20f + index * 40f, width, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { Draggable = true, Closable = true, StartVisible = false,
              // Gameplay tool: band/instrument playback only happens in the World phase.
              ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                   && (_services.ClientState.UiState & GameUIState.Loading) == 0 },
            Root: root,
            OnClose: () => w!.SetVisible(false)));
        _windows.Add(w);
        return w;
    }

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.Maestro.maestro-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _services.Framework.Update -= PlaylistTick; } catch { }
        try { _rateScope?.Dispose(); } catch { }
        try { _previewSynth.Dispose(); } catch { }
        try { EnsemblePatch.Uninstall(); } catch { }
        _bandPlayer.Dispose();
        foreach (var l in _launchers) l.Dispose();
        foreach (var w in _windows) w.Remove();
    }
}
