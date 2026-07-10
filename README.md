# somnium weaver

[![ci](https://github.com/Hung1510/Somnium-Weaver/actions/workflows/ci.yml/badge.svg)](https://github.com/Hung1510/Somnium-Weaver/actions/workflows/ci.yml)

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
| gpu %             | emission rate too (0.75 weight) -> launch a game and the field surges |
| ram pressure      | drift speed. tighter memory -> faster rising motes         |
| cpu / gpu temp    | palette warmth. hotter -> motes bleed from cyan toward amber |
| network KB/s      | shown on the HUD (drives a future renderer, see roadmap)  |
| **learned anomaly** | **butterfly burst -> 30 gold/pink motes spiral out. fires when any vital deviates from what's normal *for this machine* (see below), not a fixed threshold** |
| **audio (opt-in)**| motes react to system sound: bass swells them, loudness speeds them, treble brightens, and detected beats fire bursts |

## controls

toolbar (top-right) and global hotkeys:

| action              | hotkey            |
|---------------------|-------------------|
| start / stop        | ctrl+alt+space    |
| toggle HUD          | ctrl+alt+d        |
| audio-reactive mode | ctrl+alt+a        |
| butterfly burst     | ctrl+alt+b        |
| click-through on/off| ctrl+alt+s        |
| wallpaper mode      | ctrl+alt+w        |
| settings panel      | ctrl+alt+o        |
| quit                | ctrl+alt+q        |

drag the overlay by grabbing anywhere on the canvas (when click-through is off).
settings persist to `%AppData%\SomniumWeaver\settings.json`.

> click-through makes the whole window ignore the mouse so you can work "through" it.
> the toolbar goes dead in that mode on purpose -> use **ctrl+alt+s** to get it back.

## run it

needs the .net 8 sdk (free) and windows.

```
git clone https://github.com/Hung1510/Somnium-Weaver
cd Somnium-Weaver
dotnet run --project SomniumWeaver.csproj
```

or open `SomniumWeaver.csproj` in visual studio / rider and hit run.

### build a standalone exe

```
dotnet publish SomniumWeaver.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

pushing a `v*` tag runs `.github/workflows/release.yml`, which does exactly this on a
windows runner and attaches the zip to a github release.

## hardware sensors

beyond cpu/ram/net (which come from windows performance counters), the app pulls real
sensors via **[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)**
(MPL-2.0) — gpu load/temp, cpu package temp, per-core load, vram, fan rpm. that's what
drives the game-surge emission and the temperature-warmed palette, and it gives the
anomaly detector a richer signal vector to learn from. everything shows on the HUD.

> **admin caveat:** LibreHardwareMonitor loads a ring0 driver to read some sensors —
> **cpu package temperature in particular needs administrator**. without elevation you'll
> still get gpu load/temp and ram, but cpu temp comes back blank. run the exe as admin to
> unlock it (or flip the manifest to `requireAdministrator` to always elevate). the app
> degrades per-sensor, so nothing breaks either way. toggle the whole thing off in settings
> if you don't want it.

## wallpaper mode

press **⃞** in the toolbar or **ctrl+alt+w** to drop the tapestry *behind* your desktop
icons — animated wallpaper instead of a floating overlay. it uses the classic Progman /
`WorkerW` `SetParent` trick (with a Progman fallback for the Windows builds that lay the
desktop out differently). in this mode it fills the screen on an opaque dark background
(which also sidesteps a layered-transparency quirk that can black out reparented windows),
hides the toolbar, and drops always-on-top. toggle it off with the same hotkey — the
floating overlay comes back exactly where it was. the choice persists across launches.

## anomaly detection (the butterfly trigger)

the bursts used to fire on a dumb `cpu > 80%` line. now they fire on a **learned anomaly**.
two interchangeable engines, both fed the same multi-signal vector (cpu, ram, net, gpu,
cpu/gpu temp):

- **z-score** (default, no model) — per-signal exponentially-weighted mean + variance;
  score is the max |z| across signals. adapts to *your* machine's normal continuously.
- **autoencoder** (ONNX, opt-in) — a small autoencoder trained on standardized "normal"
  telemetry. it learns the *shape* of normal cross-signal correlations, so it catches things
  a per-signal z-score can't — e.g. temperature high while load is normal. reconstruction
  error is the anomaly score. runs on ONNX Runtime; if the model file is missing it silently
  falls back to z-score. the HUD shows which engine is live.

both standardize each signal online (per-feature EWMA), which is the trick that lets one
autoencoder work on any machine: it only ever sees ~N(0,1) inputs regardless of your
hardware. tune with the "anomaly threshold" slider (sigma; lower = more bursts).

### training / retraining the model

the shipped `model/anomaly_autoencoder.onnx` is trained on synthetic correlated data — good
enough to demo, but the real win is retraining on *your* machine:

1. enable **"log telemetry to csv"** in settings and use the machine normally for a while.
   it writes `%AppData%\SomniumWeaver\telemetry.csv`.
2. `python ml/train_autoencoder.py "%AppData%\SomniumWeaver\telemetry.csv"`
   (needs `pip install numpy onnx`). it retrains and overwrites the model + metadata.
3. restart — the autoencoder now knows *your* normal.

the whole training pipeline is one dependency-light script (numpy for the net, onnx for
export — no torch). the natural next step is a deeper model or an isolation forest; the C#
inference path (`OnnxAutoencoderEngine.cs`) stays the same as long as it's an autoencoder
that reconstructs the signal vector.

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

## tests

the rendering is visual, but the parts that shouldn't need eyeballs are covered by a real
xunit suite (`tests/SomniumWeaver.Tests`):

- **`OnlineStandardizer`** — the EWMA standardization math: first-observation behaviour, a
  constant stream never deviating, a spike producing a large z that then adapts back down.
- **`ZScoreAnomalyEngine`** — warmup suppression, firing on a spike and naming the signal,
  cooldown suppressing an immediate refire, NaN signals ignored, reset restoring warmup.
- **`OnnxAutoencoderEngine`** — the model loads, a missing model falls back gracefully, and
  the real one: after warming on correlated telemetry it flags a *broken* correlation
  (temps high while load is normal) but not an on-manifold sample.

```
dotnet test SomniumWeaver.sln
```

runs on windows (the test project references the WPF app, so same TFM). the repo root has
both a `.sln` and the app `.csproj`, so name the solution explicitly (a bare `dotnet test`
there hits "more than one project or solution file"). CI runs the same on every push / PR
via `.github/workflows/ci.yml`.

## project layout

```
SomniumWeaver.csproj      packages + wpf flags
app.manifest              per-monitor-v2 dpi awareness
App.xaml(.cs)             entry point
MainWindow.xaml(.cs)      window, render loop, HUD, hotkeys, click-through
Models/Particle.cs        the mote data model
Models/ParticleSystem.cs  the engine: pooling, emission, bursts, lines, audio, draw
Services/DataCollector.cs cpu/ram/net counters + owns the sensor + anomaly sources
Services/Metrics.cs       immutable vitals snapshot (perf counters + sensors + anomaly)
Services/HardwareMonitor.cs  LibreHardwareMonitor wrapper -> gpu/temp/vram/fan sensors
Services/IAnomalyEngine.cs   pluggable anomaly-engine interface + AnomalyResult
Services/OnlineStandardizer.cs  online EWMA standardization shared by both engines
Services/AnomalyDetector.cs  z-score engine (online EWMA, no deps)
Services/OnnxAutoencoderEngine.cs  autoencoder engine over ONNX Runtime
Services/AudioAnalyzer.cs wasapi loopback capture + fft -> normalized bands + beats
Services/AudioFrame.cs    immutable audio snapshot
Services/WallpaperMode.cs Progman/WorkerW reparenting for behind-the-icons rendering
Services/SettingsService.cs  json persistence
ml/train_autoencoder.py   trains + exports the anomaly autoencoder (numpy + onnx)
model/                    shipped anomaly_autoencoder.onnx + anomaly_meta.json
tests/SomniumWeaver.Tests xunit suite over the engines + standardizer
docs/DEMO.md              how to record the demo gif
```

## roadmap / good first PRs

these are the fun ones if you want to fork and contribute:

- **spatial-grid for constellation lines** — the link pass is naive O(n²), gated at 600
  motes. a uniform grid would let lines run at the full 5000 cap. (`ParticleSystem.DrawConnections`)
- **multi-monitor wallpaper** — wallpaper mode fills the primary WorkerW; spanning all
  monitors (or per-monitor instances) would be a nice extension.
- **deeper anomaly model** — the autoencoder is a 6-4-2-4-6 toy. a bigger net, an isolation
  forest, or a temporal model (LSTM/temporal-conv over a window) would sharpen it.
- **network + vram renderers** — both are collected and shown on the HUD but not yet drawn.
- **spectrum renderer for audio** — audio-reactive mode ships, but only drives the mote
  field. a proper per-bin bar/wave renderer off the same fft would be a great addition.
- **theme packs** — swap the palette for other characters/aesthetics via json.
- **taskbar tray icon** — minimize to tray, show/hide from there.

## credits

- rendering: [skiasharp](https://github.com/mono/SkiaSharp)
- hardware sensors: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0)
- audio capture + fft: [NAudio](https://github.com/naudio/NAudio)
- anomaly inference: [ONNX Runtime](https://github.com/microsoft/onnxruntime)
- prior art / patterns: [spectrumnet](https://github.com/diqezit/SpectrumNet) (object pooling, overlay mode, hotkeys, fps limiting)
- vibe: shorekeeper, *wuthering waves* (kuro games). no game assets are shipped here — this is pure procedural light.

MIT licensed.
