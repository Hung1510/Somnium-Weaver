# somnium weaver

a living desktop overlay that weaves your machine's vitals into a tapestry of light.
cpu, ram and network get turned into drifting cyan motes; when your cpu spikes or
suddenly goes quiet, a burst of gold-and-pink butterflies spirals out across the screen.
inspired by the shorekeeper from *wuthering waves*.

built with wpf (.net 8) + skiasharp. runs fully local, costs nothing, phones nowhere.

![placeholder — drop a gif here once it's running](docs/preview.gif)

## why

i wanted an ambient performance monitor that feels alive instead of a boring graph.
it sits on the desktop as a transparent, click-through overlay -> you forget it's a
monitor until the butterflies tell you something just woke your cpu up.

## the mapping (echoes -> weaving)

| signal            | what it does                                              |
|-------------------|----------------------------------------------------------|
| cpu %             | emission rate. more load -> more motes, shifted to bright cyan |
| ram pressure      | drift speed. tighter memory -> faster rising motes         |
| network KB/s      | shown on the HUD (drives a future renderer, see roadmap)  |
| cpu > 80% or < 15%| butterfly burst -> 30 gold/pink motes spiral out for ~2s   |
| **audio (opt-in)**| **motes react to system sound: bass swells them, loudness speeds them, treble brightens, and detected beats fire butterfly bursts** |

## controls

toolbar (top-right) and global hotkeys:

| action              | hotkey            |
|---------------------|-------------------|
| start / stop        | ctrl+alt+space    |
| toggle HUD          | ctrl+alt+d        |
| audio-reactive mode | ctrl+alt+a        |
| butterfly burst     | ctrl+alt+b        |
| click-through on/off| ctrl+alt+s        |
| settings panel      | ctrl+alt+o        |
| quit                | ctrl+alt+q        |

drag the overlay by grabbing anywhere on the canvas (when click-through is off).
settings persist to `%AppData%\SomniumWeaver\settings.json`.

> click-through makes the whole window ignore the mouse so you can work "through" it.
> the toolbar goes dead in that mode on purpose -> use **ctrl+alt+s** to get it back.

## run it

needs the .net 8 sdk (free) and windows.

```
git clone https://github.com/Hung1510/SomniumWeaver
cd SomniumWeaver
dotnet run
```

or open `SomniumWeaver.csproj` in visual studio / rider and hit run.

### build a standalone exe

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

pushing a `v*` tag runs `.github/workflows/release.yml`, which does exactly this on a
windows runner and attaches the zip to a github release.

## audio-reactive mode

toggle with the **♫** button or **ctrl+alt+a**. it captures whatever your speakers are
playing via **wasapi loopback** (no mic, no routing setup), runs a 1024-point fft, and
splits the spectrum into bass / mid / treble + overall loudness. those drive the motes:

- **loudness** -> more motes, faster rise
- **bass** -> bigger motes
- **treble** -> brighter cyan
- **beat detected** (bass energy vs its rolling average) -> a butterfly burst

per-band adaptive gain keeps it responsive whether the track is loud or quiet. one gotcha
from wasapi: during **total silence** the capture stops sending data, so the motes settle
back to cpu-driven behavior until sound returns. that's expected, not a bug.

> credit where due: the wasapi-loopback + fft approach is the same one
> [spectrumnet](https://github.com/diqezit/SpectrumNet) uses — a good reference if you
> want to go deeper on the capture side.

## demo gif

see [`docs/DEMO.md`](docs/DEMO.md) for a repeatable recipe (shot list + ffmpeg palette
pipeline). tl;dr: dark wallpaper, quality=high, record ~12s hitting ctrl+alt+b for a burst
and ctrl+alt+a with music, then two-pass palette gif at 720px / 24fps.

## how it stays smooth

- particles are **pooled** (free-list) -> no per-frame allocations
- `SKPaint` objects are **reused**, never allocated in the draw loop
- render loop is **fps-capped** (default 60) off the compositor clock, so a 144Hz
  monitor doesn't cook your cpu
- hard cap of **5000** particles
- `SKElement` (cpu raster) is used on purpose -> `SKGLElement` can't do per-pixel
  window transparency, so a true transparent overlay has to be software-rendered.
  that's the real ceiling here; the caps above keep it comfortable.

## settings

hit the **⚙** button or **ctrl+alt+o** for an in-app panel — quality, HUD, constellation
lines, audio mode, always-on-top, emission rate, max particles and target fps. changes
apply live and save when you close it. (opening the panel drops click-through so you can
actually use it.)

everything still lives in `%AppData%\SomniumWeaver\settings.json` if you'd rather edit by
hand or ship a preset.

## quality presets

`"Quality": "Low" | "Medium" | "High"` (or the radio buttons in the panel).

- **low**: solid dots, no constellation lines
- **medium** (default): dots + constellation lines
- **high**: adds a blurred bloom/glow per mote (prettier, heavier)

## project layout

```
SomniumWeaver.csproj      packages + wpf flags
app.manifest              per-monitor-v2 dpi awareness
App.xaml(.cs)             entry point
MainWindow.xaml(.cs)      window, render loop, HUD, hotkeys, click-through
Models/Particle.cs        the mote data model
Models/ParticleSystem.cs  the engine: pooling, emission, bursts, lines, audio, draw
Services/DataCollector.cs cpu/ram/net perf counters (with first-read priming)
Services/Metrics.cs       immutable vitals snapshot
Services/AudioAnalyzer.cs wasapi loopback capture + fft -> normalized bands + beats
Services/AudioFrame.cs    immutable audio snapshot
Services/SettingsService.cs  json persistence
docs/DEMO.md              how to record the demo gif
```

## roadmap / good first PRs

these are the fun ones if you want to fork and contribute:

- **spatial-grid for constellation lines** — the link pass is naive O(n²), gated at 600
  motes. a uniform grid would let lines run at the full 5000 cap. (`ParticleSystem.DrawConnections`)
- **network renderer** — net throughput is collected + shown but not yet visualized.
  ribbons or a pulse-on-spike would be lovely.
- **more emitters** — disk i/o, gpu load (via nvml/`nvidia-smi`), battery.
- **spectrum renderer for audio** — audio-reactive mode ships, but only drives the mote
  field. a proper per-bin bar/wave renderer off the same fft would be a great addition.
- **theme packs** — swap the palette for other characters/aesthetics via json.
- **taskbar tray icon** — minimize to tray, show/hide from there.

## credits

- rendering: [skiasharp](https://github.com/mono/SkiaSharp)
- prior art / patterns: [spectrumnet](https://github.com/diqezit/SpectrumNet) (object pooling, overlay mode, hotkeys, fps limiting)
- vibe: shorekeeper, *wuthering waves* (kuro games). no game assets are shipped here — this is pure procedural light.

MIT licensed.
