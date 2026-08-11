# Maestro

**MIDI auto-player for Star Resonance's Season-3 band (Musician) instrument** — playlists, network & ensemble sync, and a local game-sound preview.

Drop your `.mid` files in a folder, pick a song, and Maestro performs it for you on your summoned band instrument — solo or in a group.

---

## Features

- 🎹 **Auto-play any MIDI** on piano, guitar, bass, or drums — no manual key-mashing.
- 📂 **Library & playlists** — browse your MIDI folder, build named playlists and a now-playing queue, with auto-advance, loop (off / all / one), shuffle, and a gap between tracks.
- 🌐 **Network Sync (buffered)** — notes are timestamped and streamed ahead so listeners around you hear a steadier, more reliable performance under dense note streams.
- 🎼 **Ensemble support** — lock playback to your group's shared beat (count-in to the downbeat), optionally match the ensemble tempo, and auto-accept ensemble invites so everyone starts together.
- 🎧 **Local Preview** — audition a song through the *real* in-game instrument timbres with **no summon and no network**: one row per stem, per-part mute, sustain mode, tone-from-MIDI, and an instrument dropdown to override detection. Can also follow ("Instrument Sync") the live band player.
- 🎚 **Per-song controls** — Transpose, Note hold, Tempo %, Max notes (polyphony cap), Restrike gap, Monitor volume, Force sustain, and Apply tone/technique from the MIDI.
- 🎸 **Multi-part songs** — separate Piano / Guitar / Bass / Drum stems, including duplicates (e.g. a second bass), each on its own track.
- ⚡ **Self-managed update rate** — holds a higher tick rate while playing for tighter note timing (requires rate-control permission; safe no-op otherwise).

---

## Requirements

- Star Resonance (BlueProtocol), build `release_3.7` / Season 3.
- The **Stellar** mod framework / launcher (BepInEx-based). Maestro is a Stellar plugin (SDK **2.0.0**, `net6.0`).

---

## Install

**From a release:** drop `Stellar.Maestro.dll` into your game's plugin folder:

```
<GameInstallDir>\game_mini\stellar\plugins\maestro\Stellar.Maestro.dll
```

Launch the game and open **Maestro** from the Stellar launcher (Plugins group).

**From source:** see [Build](#build-from-source).

---

## Usage

1. Open **Maestro** from the launcher (band tools are in-world only).
2. Put your `.mid` / `.midi` files in the game's `midi\` folder — Maestro creates it and shows the path in the **Library** window (use **Rescan folder** / **Locate**).
3. Summon a free-play instrument in-game (Free Play), then add songs from the **Library** to the queue and hit ▶.
4. For groups, turn on **Network Sync**, and (optionally) **Sync to ensemble** so playback locks to the band's beat.
5. Use **Preview** to hear a song through the real instrument sounds before you play it — no summon required.

### MIDI file naming (stems)

Give a song's parts the same base name, ending in the instrument in parentheses:

```
Song (Piano).mid
Song (Guitar).mid
Song (Bass).mid
Song (Bass 2).mid    ← duplicates are their own track (same bass sound)
Song (Drum).mid
```

Select any one of them in the queue and Preview loads the whole set. All stems should be the same length (padded to the End-of-Track).

---

## Known limitation

**Overdrive / Distortion do not render in Network Sync mode.** This is a game/server limitation: guitar/bass *tone* is a Wwise event-swap that only renders through the game's live play path, not the buffered note path — so it plays Clean in net mode. *Techniques* (Muffled, Harmonic, Slap) **do** work everywhere. For distortion-critical songs, play with Network Sync **off**.

---

## Build from source

```sh
# 1. Point the build at your game install:
cp Local.props.example Local.props
#    then edit Local.props → set <GameInstallDir> to your ...\game_mini path

# 2. Build (packages come from nuget.org):
dotnet build -c Release
```

`Local.props` is gitignored. Its `DeployPlugin` target copies the built DLL to
`<GameInstallDir>\stellar\plugins\maestro\` after each build. Omit `Local.props` (or leave `GameInstallDir` blank) to build without deploying.

Config persists as `stellar.maestro.config.json` in the game directory.
