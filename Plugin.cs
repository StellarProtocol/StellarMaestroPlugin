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
    private readonly ILocalization   _loc;
    private readonly IConfigSection  _cfg;
    private readonly List<IWindowControl> _windows   = new();
    private readonly List<IDisposable>    _launchers = new();
    private readonly MidiPlayer  _bandPlayer;
    private readonly PreviewSynth _previewSynth;
    private IUpdateRateScope? _rateScope;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _cfg = _services.Config.GetSection("settings");

        _bandPlayer = new MidiPlayer(_services);
        _previewSynth = new PreviewSynth(_services);

        _bandHoldMs       = _cfg.Get<int> ("hold_ms",        0);
        _bandForceSustain = _cfg.Get<bool>("force_sustain",  false);
        _bandTempoPct     = _cfg.Get<int> ("tempo_pct",      100);
        _bandMaxPoly      = _cfg.Get<int> ("max_poly",       0);
        _bandNetPrebuffer = _cfg.Get<bool>("net_prebuffer",  true);
        _bandRestrikeGapMs = _cfg.Get<int> ("restrike_gap_ms", 33);   // default on: separates back-to-back same-pitch notes so they don't drop at the listener.
        // 33, not 16: the release and the next same-pitch press must land in DIFFERENT receiver frames — within one frame the
        // listener runs the press first and the release kills the new note. 16 ms clears a 60 fps listener only; a 30 fps one
        // (33 ms frames) still silences the repeat (sweep 2026-11-02, Band-Instrument-Playback.md).
        _bandMuteLocal     = _cfg.Get<bool>("mute_local",      false);
        _bandShowKeyViz    = _cfg.Get<bool>("show_keyviz",     true);
        _bandApplyTone     = _cfg.Get<bool>("apply_tone",      false);
        _bandRelayTone     = _cfg.Get<bool>("relay_tone",      false);   // experimental; off until 2-client validated
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
        _bandPlayer.RelayToneToListeners = _bandRelayTone;

        LoadNetworkConfig();
        LoadPlaylists();
        RescanMidiFolder();   // create the midi/ folder (if missing) and pre-populate the song list at startup

        _networkWindow  = RegisterWindow("maestro.network",  _loc.T("mst.win.network"),  1, BuildNetworkRoot());
        _settingsWindow = RegisterWindow("maestro.settings", _loc.T("mst.win.settings"), 3, BuildSettingsRoot());
        _libraryWindow  = RegisterWindow("maestro.library",  _loc.T("mst.win.library"),  4, BuildLibraryRoot());
        _previewWindow  = RegisterWindow("maestro.preview",  _loc.T("mst.win.preview"),  5, BuildPreviewRoot());
        _tipWindow      = RegisterTipWindow();
        // 540 wide for the players table (Plugin.Link.Players.cs column math). A GlassMenu window that isn't Resizable
        // takes its width from this spec at mount — a saved layout restores position only — so no stale 420 can win.
        _linkWindow     = RegisterWindow("maestro.link",     _loc.T("mst.win.link"),     2, BuildLinkRoot(), 540f);
        var window = _mainWindow = RegisterWindow("maestro.main", _loc.T("mst.title"), 0, BuildBandRoot());
        // Title stays the fixed literal "Maestro" — the stable pin-identity key (ILauncher.cs:49-50) —
        // so a pinned tile survives a language change; TitleProvider carries the live-localized display.
        _launchers.Add(_services.Launcher.Register(new LauncherEntry(
            Title:   "Maestro",
            IconPng: LoadIconPng(),
            IconKey: null,
            // While linked, Maestro opens as the Link status view (it replaces the main window — Plugin.Link.Ui.cs).
            OnOpen:  () => { if (IsLinked) { _linkWindow.SetVisible(true); return; } RescanMidiFolder(); window.SetVisible(true); })   // rescan the midi/ folder each open
        { Group = LauncherGroup.Plugin,
          // Re-localize the tile DISPLAY on a language change; Title above never changes (pin identity).
          TitleProvider = () => _loc.T("mst.title"),
          // Band tools are in-world only — hide the launcher tile outside the World phase.
          ShouldShow = () => _services.ClientState.Phase == GamePhase.World }));

        _services.Framework.Update += PlaylistTick;   // auto-advance the queue when a song finishes

        // Capture InstrumentService (its Tick) so the ensemble grid (Sync to ensemble) and the Link instrument check can read it.
        try { EnsemblePatch.Install(_services.Harmony.Create("ensemble"), m => _services.Log.Info(m)); }
        catch (Exception ex) { _services.Log.Warning($"[Maestro] ensemble patch install failed: {ex.Message}"); }

        InitLink();        // Maestro Link (website-run band room) — Plugin.Link*.cs
        InitLinkDiag();    // dev-only sync diagnostics, DEBUG builds only — dormant unless its folder exists (Plugin.LinkDiag.cs)

        _services.Log.Info("[Maestro] constructed");
    }

    // Dev diagnostics hooks, implemented in Plugin.LinkDiag.cs / Plugin.SweepCmd.cs, which compile in DEBUG builds only
    // (csproj). Without an implementation the compiler removes these calls, arguments included — so keep the arguments
    // plain values (no diag-only types) or a Release build won't bind them.
    partial void InitLinkDiag();
    partial void DisposeLinkDiag();
    partial void LinkDiagLinkStart(long now, string songId, long songStartMs, int startPos);   // Link part START/CFG rows
    partial void LinkDiagPlainStart(string entry);                                             // plain-playback START/CFG rows

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
        DisposeLink();
        DisposeLinkDiag();
        try { _rateScope?.Dispose(); } catch { }
        try { _previewSynth.Dispose(); } catch { }
        try { EnsemblePatch.Uninstall(); } catch { }
        _bandPlayer.Dispose();
        foreach (var l in _launchers) l.Dispose();
        foreach (var w in _windows) w.Remove();
    }
}
