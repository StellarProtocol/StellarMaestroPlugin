using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Maestro;

// UI + wiring for the local game-sound preview (PreviewSynth): auditions the selected queue song's stems through the
// real game instrument timbres, locally, with no summon and no network. Per-instrument mute + master volume.
public sealed partial class Plugin
{
    private bool  _previewSync;          // follow the band player instead of the preview's own clock
    private int   _previewSyncOffsetMs;  // in sync: delay the backing by this many ms (tune by ear to match a networked monitor; 0 = local)
    private const int GameBufferDefaultMs = 500;   // fallback if the game's band delay can't be read

    private void SetPreviewSyncOffset(int ms)
    {
        _previewSyncOffsetMs = Math.Clamp(ms, 0, 2000);
        _cfg.Set<int>("preview_sync_offset", _previewSyncOffsetMs); _cfg.Save();
    }

    // Read the game's band sync-delay slider (band listen settings, 500..2000ms) — used as a "= band delay" preset.
    private int ReadBandDelayMs()
    {
        try
        {
            var t = StellarInterop.FindType("Panda.Utility.LocalUserDataManager") ?? StellarInterop.FindType("LocalUserDataManager");
            var m = t?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                     .FirstOrDefault(x => x.Name == "GetFloat" && x.GetParameters().Length >= 3 && x.GetParameters()[1].ParameterType == typeof(string));
            if (m == null) return GameBufferDefaultMs;
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            args[0] = Enum.ToObject(ps[0].ParameterType, 0);   // LocalUserDataType.Device
            args[1] = "BKL_BAND_SYNC_DELAY";
            args[2] = (float)GameBufferDefaultMs;              // default 500
            for (int i = 3; i < ps.Length; i++) args[i] = 0;   // version
            float v = Convert.ToSingle(m.Invoke(null, args));
            return v > 0 ? (int)Math.Round(v) : GameBufferDefaultMs;
        }
        catch { return GameBufferDefaultMs; }
    }

    // User-chosen instrument per stem key (overrides filename detection) — for files with no "( )" suffix, or a wrong
    // guess. In-memory (per session); applied in StemsForSong and live via SetStemMode.
    private readonly Dictionary<string, string> _previewModeOverride = new();
    private static readonly IReadOnlyList<string> InstrumentNames = new[] { "Piano", "Guitar", "Bass", "Drum" };  // == PreviewSynth.Slots order

    private static int InstrumentIndex(string mode)
    {
        for (int i = 0; i < PreviewSynth.Slots.Length; i++) if (PreviewSynth.Slots[i].mode == mode) return i;
        return 0;
    }

    // Set the stem's instrument from the dropdown. Invalidates the stem cache so the row re-reads the new mode, and
    // re-rents the voice live if a preview is playing.
    private void SelectInstrument(string key, int index)
    {
        if (string.IsNullOrEmpty(key) || index < 0 || index >= PreviewSynth.Slots.Length) return;
        string mode = PreviewSynth.Slots[index].mode;
        _previewModeOverride[key] = mode;
        _previewStemsKey = "\0";                         // force PreviewStems() to recompute with the override
        _previewSynth.SetStemMode(key, mode);            // live re-rent if currently playing
    }

    // The selected queue song's stems (key + instrument mode), in a stable order — cached per resolved path (no per-frame scan).
    private List<(string key, string mode)> _previewStems = new();
    private string _previewStemsKey = "\0";

    private List<(string key, string mode)> PreviewStems()
    {
        string path = CurrentPreviewSongPath();
        if (path != _previewStemsKey)
        {
            _previewStemsKey = path;
            _previewStems = new List<(string, string)>();
            if (!string.IsNullOrEmpty(path))
                foreach (var (key, mode, _) in StemsForSong(path)) _previewStems.Add((key, mode));
        }
        return _previewStems;
    }

    private string CurrentPreviewSongPath()
    {
        int i = _queueSel >= 0 ? _queueSel : (NowPlayingInView >= 0 ? NowPlayingInView : 0);
        if (i < 0 || i >= Active.Songs.Count) return "";
        var path = Path.Combine(BandMidiDir(), Active.Songs[i]);
        return File.Exists(path) ? path : "";
    }

    private string PedalName(string m) => _previewSynth.GetPedalMode(m) switch
    {
        PreviewSynth.PedalHold => _loc.T("mst.pedal.hold"),
        PreviewSynth.PedalOff  => _loc.T("mst.pedal.off"),
        _                      => _loc.T("mst.pedal.file"),
    };
    private void CyclePedal(string m) => _previewSynth.SetPedalMode(m, _previewSynth.GetPedalMode(m) + 1);

    private void PreviewPlayPause()
    {
        if (_previewSynth.IsPlaying) { if (_previewSynth.IsSyncing) _previewSynth.Stop(); else _previewSynth.Pause(); return; }
        if (_previewSynth.IsPaused)  { _previewSynth.Resume(); return; }
        PreviewCurrentSong();
    }

    private void StopPreview() => _previewSynth.Stop();

    private void SetPreviewSync(bool v)
    {
        _previewSync = v;
        _cfg.Set<bool>("preview_sync", v); _cfg.Save();
        if (!v && _previewSynth.IsSyncing) _previewSynth.Stop();   // turning sync off while armed unsyncs
    }

    // Debounced seek (same pattern as the band player): the slider Set fires continuously while dragging, so we stash
    // the target and only Seek once it settles — the thumb follows the drag meanwhile.
    private bool  _pvSeekPending;
    private float _pvPendingFrac;
    private long  _pvLastSeekMs;

    private float PreviewProgressFrac()
    {
        if (_pvSeekPending)
        {
            if (Environment.TickCount64 - _pvLastSeekMs >= 180)
            {
                _pvSeekPending = false;
                int d = _previewSynth.DurationMs;
                if (d > 0) _previewSynth.Seek((int)(_pvPendingFrac * d));
            }
            else return Math.Clamp(_pvPendingFrac, 0f, 1f);
        }
        int dur = _previewSynth.DurationMs;
        return dur > 0 ? Math.Clamp((float)_previewSynth.PositionMs / dur, 0f, 1f) : 0f;
    }

    private void PreviewSeekFrac(float frac)
    {
        if (_previewSynth.IsSyncing) return;   // position is driven by the band player in sync mode
        _pvPendingFrac = frac;
        _pvLastSeekMs  = Environment.TickCount64;
        _pvSeekPending = true;
    }

    // Gather the selected queue song's sibling stems and start the local game-sound preview.
    private void PreviewCurrentSong()
    {
        int i = _queueSel >= 0 ? _queueSel : (NowPlayingInView >= 0 ? NowPlayingInView : 0);
        if (i < 0 || i >= Active.Songs.Count) { SetBandStatus(_loc.T("mst.status.queueEmptyPreview"), true); return; }
        var path = Path.Combine(BandMidiDir(), Active.Songs[i]);
        if (!File.Exists(path)) { SetBandStatus(_loc.TFormat("mst.status.missingFile", Active.Songs[i]), true); return; }

        var stems = StemsForSong(path);
        if (stems.Count == 0) { SetBandStatus(_loc.T("mst.status.noStems"), true); return; }

        _previewSynth.Stop();
        _previewSynth.Load(stems);
        bool ok = _previewSync
            ? _previewSynth.PlaySynced(() => (_bandPlayer.IsPlaying, Math.Max(0, _bandPlayer.PositionMs - _previewSyncOffsetMs)))   // follow the band player (optionally offset)
            : _previewSynth.Play();
        if (!ok) SetBandStatus(_loc.T("mst.status.previewFailed"), true);
        else SetBandStatus(_previewSync ? _loc.T("mst.status.previewArmed")
                                        : _loc.TFormat("mst.status.previewing", PreviewKeysText()));
    }

    // Every stem file sharing this song's base name (part before " (" / " ["), one entry per stem KEY (the label in the
    // parentheses) — so "Bass" and "Bass 2" are BOTH returned. Ordered by instrument (piano/guitar/bass/drum), then key.
    private List<(string key, string mode, string path)> StemsForSong(string path)
    {
        var byKey = new Dictionary<string, (string mode, string sortMode, string path)>();
        var dir = Path.GetDirectoryName(path) ?? BandMidiDir();
        string baseName = StemBase(Path.GetFileNameWithoutExtension(path));
        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
        {
            var ext = Path.GetExtension(f);
            if (!ext.Equals(".mid", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".midi", StringComparison.OrdinalIgnoreCase)) continue;
            if (!StemBase(Path.GetFileNameWithoutExtension(f)).Equals(baseName, StringComparison.OrdinalIgnoreCase)) continue;
            var (key, detMode) = DetectStem(f);
            string mode = _previewModeOverride.TryGetValue(key, out var ov) ? ov : detMode;   // override wins for timbre/display
            if (!byKey.ContainsKey(key)) byKey[key] = (mode, detMode, f);   // first file per stem key wins
        }
        // Sort by the DETECTED instrument (stable per file), NOT the override — so changing a row's instrument in the
        // dropdown doesn't re-order the rows and make them jump.
        var keys = byKey.Keys.ToList();
        keys.Sort((a, b) =>
        {
            int ra = ModeRank(byKey[a].sortMode), rb = ModeRank(byKey[b].sortMode);
            return ra != rb ? ra.CompareTo(rb) : string.Compare(a, b, StringComparison.Ordinal);
        });
        var outp = new List<(string, string, string)>();
        foreach (var k in keys) outp.Add((k, byKey[k].mode, byKey[k].path));
        return outp;
    }

    private static int ModeRank(string mode)
    {
        for (int i = 0; i < PreviewSynth.Slots.Length; i++) if (PreviewSynth.Slots[i].mode == mode) return i;
        return 99;
    }

    // Name before the first " (" or " [" — e.g. "Song (Piano)" / "Song [Drum] (game)" → "Song".
    private static string StemBase(string name)
    {
        int a = name.IndexOf(" (", StringComparison.Ordinal);
        int b = name.IndexOf(" [", StringComparison.Ordinal);
        int cut = a < 0 ? b : b < 0 ? a : Math.Min(a, b);
        return (cut >= 0 ? name.Substring(0, cut) : name).Trim();
    }

    // Stem key = the label inside the last "(...)" or "[...]" (e.g. "Bass 2"), lowercased; instrument mode = its type.
    private static (string key, string mode) DetectStem(string filePath)
    {
        string name  = Path.GetFileNameWithoutExtension(filePath);
        string label = StemSuffix(name);
        string basis = (label.Length > 0 ? label : name).ToLowerInvariant();
        string mode  = basis.Contains("drum") || basis.Contains("perc") ? "drum"
                     : basis.Contains("bass")   ? "bass"
                     : basis.Contains("guitar") ? "guitar"
                     : "piano";
        string key = label.Length > 0 ? label.ToLowerInvariant() : "main";   // no "( )" suffix → generic key (user can pick the instrument)
        return (key, mode);
    }

    // Text inside the last "(...)" or "[...]" of a name — the instrument suffix. "" if none.
    private static string StemSuffix(string name)
    {
        int o = name.LastIndexOf('(');
        if (o >= 0) { int c = name.IndexOf(')', o); if (c > o) return name.Substring(o + 1, c - o - 1).Trim(); }
        o = name.LastIndexOf('[');
        if (o >= 0) { int c = name.IndexOf(']', o); if (c > o) return name.Substring(o + 1, c - o - 1).Trim(); }
        return "";
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
