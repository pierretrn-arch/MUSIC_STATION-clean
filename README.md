# Music Station

Music Station is a Windows desktop app for singers and musicians. It listens to your voice or instrument through the microphone, shows the note you are playing and how in tune it is, and writes what you play into an editable piano roll. You can also open audio and MIDI files, edit the notes on several tracks, play them back and export them.

## What you can do

### Live Tuner
- **Tune:** see the note you are singing or playing, its pitch in Hz and how many cents sharp or flat you are.
- **Record:** press Record and your notes appear on the piano roll as you play. Turn on the metronome for a click and a one-bar count-in.
- **Edit:** move, resize, draw, erase and split notes, turn a note into a chord, copy and paste, with Undo and Redo.
- **Review:** the side panel lists every note (start, length, confidence), or shows them as sheet music in the Notation view.

### File Analysis
- **Open a file:** WAV, AIFF, MP3, M4A, AAC, WMA or FLAC audio is analysed into notes. MIDI files (.mid) load directly, one track per part you choose.
- **Tempo:** the tempo is detected for you and can be changed in the Tempo box.
- **Results:** a list of the selected track's notes, with confidence, cents and mean pitch for detected notes.

### Tracks (both tabs)
- Add as many tracks as you need. Each has its own colour, name, mute, solo, volume and pan.
- The piano roll edits the selected track; the other tracks are shown faintly behind it.
- Playback mixes all tracks you can hear (muted tracks are silent; if any track is soloed, only soloed tracks play).

### Playing notes
- Click the keys on the left of the piano roll, use your computer keyboard, or connect a USB MIDI keyboard.
- Sound comes from the built-in synth or from a VST3 instrument plugin you have installed (chosen in Settings).

### Export
- **MIDI** (one track per track, with its name, volume and pan), **JSON** and **CSV**.

### Settings
- Microphone, audio filters and pitch-detection options.
- MIDI keyboard, computer-keyboard octave, master volume.
- Synth engine: built-in synth or a VST3 plugin, including its own editor window.

> **Note:** tracks are not saved yet. The app asks before closing, loading a file or clearing when you have notes, so you can export first.

## Keyboard shortcuts (piano roll)

| Key | Action |
|---|---|
| V / D / E / S | Select, Draw, Erase, Split tool |
| Delete | Delete selected notes |
| Ctrl+A / Ctrl+C / Ctrl+X / Ctrl+V | Select all, copy, cut, paste at the playhead |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Arrow keys | Scroll the piano roll |
| Mouse wheel / Ctrl+wheel / Alt+wheel | Scroll in time / scroll up and down / zoom |
| Alt while dragging | Turn snap-to-grid off |

## System requirements

- Windows 10 or Windows 11 (64-bit)
- .NET 8 Desktop Runtime (included in the installer)
- Microsoft Edge WebView2 Runtime (for the Notation view)
- A microphone for the Live Tuner
- Optional: a USB MIDI keyboard, VST3 instrument plugins

## Install

1. Open the [Releases page](https://github.com/pierretrn-arch/MUSIC_STATION-clean/releases) and download the latest `MusicStation-Setup-x.y.z.exe`.
2. Run it and follow the steps. .NET is included, so nothing else needs installing.
3. Start Music Station from the Start menu.

Windows may show a "Windows protected your PC" warning because the installer isn't code-signed yet. Click **More info**, then **Run anyway**.

To uninstall, use **Settings > Apps > Installed apps > Music Station > Uninstall**. Your settings are kept in `%APPDATA%`.

## Build from source

You need the .NET 8 SDK on Windows.

```
git clone <this repository>
cd <repository folder>
dotnet build ToneDetector.sln
dotnet run --project ToneDetector.App
```

To build the installer, push a version tag (for example `v0.1.0`). The **Build installer** GitHub Actions workflow builds the setup file and attaches it to a new release. The setup script is `installer/MusicStation.iss` (Inno Setup 6).

Run the tests with:

```
dotnet test ToneDetector.sln
```

The VST3 host uses a small native library (`ToneDetector.Audio/Native/VstShim.dll`) that is already included. Its source is in `VstNativeShim/` if you need to rebuild it.

## Licence

To be decided. Until a licence is added, all rights are reserved by the author.

## Third-party components

Music Station uses the following components. Their licences still need to be confirmed and listed here before release.

- NAudio (audio input and output)
- Melanchall DryWetMidi (MIDI files)
- MathNet.Numerics (signal processing)
- VexFlow (notation)
- Microsoft Edge WebView2 (notation view)
- Steinberg VST3 SDK (VST3 plugin hosting)
