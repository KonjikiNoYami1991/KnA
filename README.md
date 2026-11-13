# KnA - Konjiki no Autoencoder 2.0

**English** · [Italiano](README.it.md)

An almost automatic hardsubber and encoder by KonjikiNoYami, rewritten from scratch for **.NET 10**.

Drop in some MKV episodes and pick a profile or a preset. KnA finds the right subtitle track, extracts the embedded fonts,
burns the subtitles in with libass, and encodes the video on the **CPU or the GPU** (NVENC, Quick Sync, AMF,
VideoToolbox, VA-API), including **AV1 and HEVC 10-bit**, **HDR10 / HDR10+ / Dolby Vision**, and resumable **segmented encoding**.

| Web edition (Docker / Proxmox / any browser) | Desktop edition (Windows / macOS / Linux) |
|---|---|
| ![Web UI](docs/screenshots/web-dark.png) | ![Desktop UI](docs/screenshots/desktop-dark.png) |
| ![Web editor: visual trim](docs/screenshots/web-editor-trim.png) | ![Desktop editor: tracks](docs/screenshots/desktop-editor-tracks.png) |

## Two front-ends, one engine

| | **Web edition** `src/KnA.Server` | **Desktop edition** `src/KnA.App` |
|---|---|---|
| Runs on | Docker, Proxmox LXC/VM, NAS, or locally | Windows, macOS (Apple Silicon too), Linux |
| UI | Responsive web page (phone included), live progress via SSE | Native Avalonia window, drag & drop, Ctrl+V, Send To |
| Files | Browsed on the server (media roots) | Local disks |

Both use **`KnA.Core`**, which handles probing, command building, the queue, previews, presets, updates and logs.
The UI is in **Italian** when the system (or the browser) is Italian, in **English** otherwise; it can be forced in *Settings → General*.

## Features

### Encoding
- **Profiles**: Universal MKV (H.264/HEVC/AV1), Streaming/Web MP4, Blu-ray AAC / AC-3, Workraw, XviD+MP3,
  Remux→MP4, Remux→MKV, **Images** (PNG / JPEG / TIFF / WebP / BMP), **Demux** and Aegisub keyframes.
- **Hardware encoders** (each one is checked with a real test encode, not just read from `ffmpeg -encoders`):

  | Backend | H.264 | HEVC | AV1 | 10-bit |
  |---|---|---|---|---|
  | CPU (x264 / x265 / SVT-AV1) | ✓ | ✓ | ✓ | HEVC, AV1 |
  | NVIDIA NVENC | ✓ | ✓ | ✓ (RTX 40/50) | HEVC, AV1 |
  | Intel Quick Sync | ✓ | ✓ | ✓ (Arc / Core Ultra) | HEVC, AV1 |
  | AMD AMF (Windows) | ✓ | ✓ | ✓ (RDNA 3+) | HEVC, AV1 |
  | VA-API (Linux, Intel/AMD) | ✓ | ✓ | ✓ (when the GPU has it) | HEVC, AV1 |
  | Apple VideoToolbox | ✓ | ✓ | — (no AV1 hardware encoder on Apple Silicon) | HEVC |

- **Segmented encoding** (av1an-style): the video is split at keyframes, segments are encoded in parallel,
  and a stopped job **resumes from the last finished segment**. Ideal for slow CPU encoders (x265, SVT-AV1).
- **HDR**: HDR10 is kept (mastering display and light levels passed to the encoder) or **tone-mapped to SDR**
  (libplacebo when available, otherwise zscale + Hable). **Dolby Vision** (RPU via `dovi_tool`) and **HDR10+**
  (via `hdr10plus_tool`) are carried over when re-encoding HEVC with x265; KnA explains when that is not possible (trim, crop, frame-rate change, GPU encoders).
- **Containers**: MKV, MP4, MOV, WebM, AVI, TS, with a compatibility check for the chosen codecs
  (an AVI cannot hold HEVC, attachments need Matroska, and so on).

### Per-file editor
Every file in the queue has an editor (✎) with five tabs:

1. **Tracks**: use, remove or replace the source streams; add **external audio, subtitles, fonts and other files**.
   MKV / MKA / MP4 files are analysed and you tick the streams to import. Each track has language, title,
   default/forced flags and its own **delay** (video, audio and subtitles). One subtitle can be burned in.
2. **Trim**: visual timeline with thumbnails and audio waveform, draggable handles, frame-accurate nudges.
   **The cut is always applied last**, after imported tracks, delays, filters and burned subtitles, so a subtitle
   imported with a delay stays in sync after the cut.
3. **Filters**: orientation (rotated phone videos are detected; rotation and mirroring can be corrected),
   deinterlace, crop (with black-border detection), denoise, deband, sharpen, frame rate, custom FFmpeg filters;
   audio gain, loudness normalization (EBU R128), stereo downmix, sample rate.
   **VapourSynth** and **AviSynth+** can be chosen as the frameserver, with ready-made templates; audio produced by the script is supported.
4. **Output**: every setting of the profile, container, HDR mode, segmented encoding, image or demux options, destination,
   "save as preset" and the exact FFmpeg command.
5. **Preview**: the final frame (filters, burned subtitles, HDR conversion) at any position, plus a 6-second clip of the result.
   All previews (frames, thumbnails, waveform, crop detection, clips) also work with VapourSynth and AviSynth sources.

### Presets
Built-in presets, your own presets saved from any file, and a **wizard** that asks a few plain questions
(what is it for, compatibility or size, speed or compression, subtitles, audio) and picks a working GPU when you ask for speed.

### Robustness
- **Transport streams** (`.ts`, `.m2ts`…): larger probe window, regenerated timestamps, corrupt packets and unknown
  data/teletext streams are ignored, so odd broadcast metadata no longer breaks FFmpeg.
- Disk-space and write checks before starting, a watchdog for stuck FFmpeg processes, crash logs,
  log rotation, a queue that survives restarts, temporary files cleaned up.
- **Never overwrites anything**: outputs follow a name template (`{name} [{label}]` by default), and a counter is added when the name already exists.

### Updates
- *Settings → Updates & components* checks the **GitHub releases of this repository** for a new KnA version
  (the desktop app downloads and installs it; the server shows how to update the container or the service).
- **FFmpeg, ffprobe, dovi_tool and hdr10plus_tool** are installed or updated from their official release pages
  (BtbN FFmpeg builds on Windows/Linux, Homebrew on macOS).

### Desktop integration
- **Windows "Send to"**: *Settings → Output* adds a "KnA" entry plus one entry per chosen preset (adds the files with that
  preset and starts the queue). Many files sent at once go into the same window.
- **Linux**: "Open with", Nautilus scripts and a Dolphin service menu with the same entries.
- Command line: `KnA [--preset "Name"] [--start] files-or-folders…`

## Quick start

### Docker (headless server, e.g. Proxmox + iGPU)

```bash
git clone https://github.com/KonjikiNoYami1991/TnA.git && cd TnA
# edit docker-compose.yml: media folder, optional password
RENDER_GID=$(getent group render | cut -d: -f3) docker compose up -d --build
```

Then open `http://<host>:8585`. See **[docs/PROXMOX.md](docs/PROXMOX.md)** ([italiano](docs/PROXMOX.it.md)) for LXC GPU passthrough,
the native installer without Docker (`deploy/install-linux.sh`) and all environment variables.

### Desktop

```bash
dotnet run --project src/KnA.App -c Release
```

On Windows and Linux, *Settings → Updates & components* installs FFmpeg (BtbN static build, GPL: libass, x264, x265,
SVT-AV1, NVENC, QSV, AMF, VA-API). On macOS, run `brew install ffmpeg`.

### Web edition on your own PC

```bash
dotnet run --project src/KnA.Server -c Release -- --open
```

### VapourSynth and AviSynth (optional)

- **VapourSynth**: install it so that `vspipe` is on the PATH (or set `KNA_VSPIPE`), plus the source plugins your
  scripts use (L-SMASH Works, BestSource or FFMS2). `.vpy` files can be added to the queue directly.
- **AviSynth+**: install AviSynth+ and use an FFmpeg built with `--enable-avisynth`; `.avs` scripts are read by FFmpeg.
  KnA shows a clear message when the FFmpeg in use cannot read them.

Frameserver decoding happens in the script, so it does not use hardware decoding; the encoder can still be a GPU one.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the integration tests, FFmpeg on your PATH.

```bash
dotnet build KnA.slnx
dotnet test KnA.slnx
```

The tests build sample media with FFmpeg (anamorphic video, 5.1 audio, ASS/SRT subtitles, fonts, an HDR10 clip, a transport stream)
and run real jobs: hardsub, softsub AV1 10-bit, remux, keyframes, external tracks with delays, trim after imports, demux, images,
HDR10, segmented encoding with resume, VapourSynth sources (when `vspipe` is available), previews, plus headless renders of the desktop windows.

Self-contained builds:

```bash
dotnet publish src/KnA.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/KnA.App -c Release -r osx-arm64 --self-contained
dotnet publish src/KnA.Server -c Release -r linux-x64 --self-contained
```

Pushing a `v*` tag runs `.github/workflows/release.yml`, which builds every platform and prepares a **draft** release
with the packages the in-app updater looks for (`KnA-desktop-<rid>.zip`, `KnA-server-<rid>.zip`).

## Project layout

```
src/KnA.Core     engine (no UI): probe, track plan, filters, HDR, command builder, chunked encoder, queue, previews, presets, updates
src/KnA.Server   ASP.NET Core API + web UI (wwwroot), Docker entry point
src/KnA.App      Avalonia desktop app
tests/           xUnit unit/integration tests + headless UI test
deploy/          Linux/LXC installer (systemd)
docs/            Proxmox guide, screenshots
legacy/          the original WinForms/.NET Framework 4.8 app (v0.9.9), kept for reference
```

## What changed since 0.9.9

The old code was a single 3,900-line WinForms form. Bugs fixed in the rewrite include:

- `-crf: N` (with a stray colon) produced invalid commands, and quality names were compared inconsistently
  ("Very High" vs "Very high", "Above medium" vs "Above normal"), so some presets silently got no quality settings.
- Output files were overwritten (`-y`), despite the README promising otherwise.
- Pausing keyframe generation paused `cmd.exe` instead of FFmpeg/SCXviD.
- Fragile progress parsing of FFmpeg's human-readable status line, which failed on N/A values. It now uses `-progress`.
- Paths with quotes or special characters broke the command line. It now uses an argument list.
- TS stream-index parsing crashed (`Binary.Substring` on an empty string).
- The window height was saved under the key "Heigth" and never restored.
- `Thread.Abort` (unsupported on modern .NET), a global clipboard hook, and a Dropbox-based updater are gone.
- No more mkvmerge, 7-Zip or MediaInfo native DLLs: FFmpeg/ffprobe do everything.

## License

[GPLv3](LICENSE)
