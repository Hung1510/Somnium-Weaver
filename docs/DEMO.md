# demo gif spec

the readme's first image is the whole ballgame for a visual project -> this is a
repeatable recipe for a ~10-12s loop that shows the good stuff and stays under github's
comfort zone (aim < 8 MB, ideally < 5).

## what the clip has to show (shot list)

the app makes it easy to trigger everything on cue, so you don't have to wait around:

| t (s) | beat                        | how to trigger                                         |
|-------|-----------------------------|--------------------------------------------------------|
| 0-3   | calm ambient motes rising   | just idle. maybe open a couple apps for gentle cpu     |
| 3-5   | butterfly burst on cpu spike| **Ctrl+Alt+B** (manual burst) — or actually spike cpu  |
| 5-9   | audio-reactive to music     | **Ctrl+Alt+A**, then play a track with a clear beat    |
| 9-12  | click-through over desktop  | **Ctrl+Alt+S**, drag a window "through" the motes      |

keep the HUD **on** for at least part of it (**Ctrl+Alt+D**) -> the live CPU/RAM/AUD
numbers are what sell that it's a real monitor, not a screensaver.

## setup for the cleanest capture

- set `"Quality": "High"` in `%AppData%\SomniumWeaver\settings.json` -> turns on the bloom
  glow, which reads much better when downscaled to gif.
- window around **900x600**, centered.
- **dark wallpaper** (or a subtle one). the palette is cyan/teal on transparent -> it pops
  on dark, disappears on white. a near-black gradient is ideal.
- close notification spam so nothing pops into frame.
- for the audio segment, pick a track with an obvious kick so the beat-bursts line up.

## recording

two easy paths. pick one.

### a) ScreenToGif (simplest, windows, free)

-> https://www.screentogif.com/ . record the window region directly, then in its editor:
trim to the loop, set **fps 20-24**, resize width to ~**720**, and export as gif. it has
a built-in palette optimizer. done. this is the fastest route to a clean loop.

### b) OBS -> mp4 -> ffmpeg (best quality, you already have ffmpeg)

1. record the window in OBS at 60fps to `somnium.mp4` (dark scene, window captured).
2. trim + palette-optimize to gif with the two-pass method below (this is what makes
   gifs look good instead of banded garbage).

```
:: 1) generate an optimized palette from the clip
ffmpeg -ss 00:00:02 -t 12 -i somnium.mp4 ^
  -vf "fps=24,scale=720:-1:flags=lanczos,palettegen=stats_mode=diff" ^
  -y palette.png

:: 2) render the gif using that palette
ffmpeg -ss 00:00:02 -t 12 -i somnium.mp4 -i palette.png ^
  -lavfi "fps=24,scale=720:-1:flags=lanczos[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=3" ^
  -y docs/preview.gif
```

`-ss 00:00:02` skips the first 2s, `-t 12` grabs a 12s window -> tweak to taste.
`bayer_scale=3` keeps dithering subtle so gradients don't get noisy.

### shrink if it's too big

- drop width: `scale=600:-1` instead of `720`.
- drop fps: `fps=18`.
- shorten: `-t 8`.
- last resort: `paletteuse=dither=none` (smaller, slightly flatter).

check size before committing -> `ls -lh docs/preview.gif`.

## also keep an mp4

github renders gifs, but for **Show HN / reddit / discord** an mp4 (or webm) looks sharper
and is smaller. keep both:

```
ffmpeg -ss 00:00:02 -t 12 -i somnium.mp4 -vf "scale=1080:-1" -c:v libx264 -crf 20 -an -y docs/preview.mp4
```

## wiring it into the readme

the readme already points at `docs/preview.gif`:

```
![somnium weaver](docs/preview.gif)
```

drop the file there, commit, and the hero image is live. a good loop here does more for
stars than any single feature.

## the "money shot" checklist

- [ ] dark background, motes clearly visible
- [ ] at least one butterfly burst on screen
- [ ] the audio segment visibly pulses/bursts on the beat
- [ ] HUD visible for part of it (proves it's live data)
- [ ] loops cleanly (start and end on a calm, similar frame)
- [ ] under ~8 MB
