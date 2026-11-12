# TnA - Tanoshimi no Autohardsubber 2.0

An almost automatic hardsubber and encoder by KonjikiNoYami, rewritten from scratch for **.NET 10**.

Drop in some MKV episodes and pick a profile. TnA finds the right subtitle track, extracts the embedded fonts,
burns the subtitles in with libass, and encodes the video on the **CPU or the GPU** (NVENC, Quick Sync, AMF,
VideoToolbox, VA-API), including **AV1 and HEVC 10-bit**.

| Web edition (Docker / Proxmox / any browser) | Desktop edition (Windows / macOS / Linux) |
|---|---|
| ![Web UI](docs/screenshots/web-dark.png) | ![Desktop UI](docs/screenshots/desktop-dark.png) |

## Two front-ends, one engine

| | **Web edition** `src/TnA.Server` | **Desktop edition** `src/TnA.App` |
|---|---|---|
| Runs on | Docker, Proxmox LXC/VM, NAS, or locally | Windows, macOS (Apple Silicon too), Linux |
| UI | Responsive web page (phone included), live progress via SSE | Native Avalonia window, drag & drop, Ctrl+V |
| Files | Browsed on the server (media roots) | Local disks |

Both use **`TnA.Core`**, which handles probing, command building, the queue, pause/resume, hardware detection and logs.

## Features

- **Profiles**: Universal MKV (H.264/HEVC/AV1), Streaming/Web MP4, Blu-ray AAC / AC-3, Workraw,
  XviD+MP3, Remux→MP4, Remux→MKV, and Aegisub keyframes.
- **Hardware encoders** (each one is checked with a real test encode, not just read from `ffmpeg -encoders`):

  | Backend | H.264 | HEVC | AV1 | 10-bit |
  |---|---|---|---|---|
  | CPU (x264 / x265 / SVT-AV1) | ✓ | ✓ | ✓ | HEVC, AV1 |
  | NVIDIA NVENC | ✓ | ✓ | ✓ (RTX 40/50) | HEVC, AV1 |
  | Intel Quick Sync | ✓ | ✓ | ✓ (Arc / Core Ultra) | HEVC, AV1 |
  | AMD AMF (Windows) | ✓ | ✓ | ✓ (RDNA 3+) | HEVC, AV1 |
  | VA-API (Linux, Intel/AMD) | ✓ | ✓ | ✓ (when the GPU has it) | HEVC, AV1 |
  | Apple VideoToolbox | ✓ | ✓ | — (no AV1 hardware encoder on Apple Silicon) | HEVC |

- **Hardsub**: picks a sidecar `.ass/.srt` first, otherwise the forced, default or first text track.
  It falls back to **PGS/VobSub bitmap subtitles** (overlay) when there are no text tracks.
  Fonts are extracted from the MKV attachments, so you don't need to install them.
- **Softsub**: keeps every subtitle track and font, and copies the audio untouched (MKV).
- **Smart resize**: never upscales, keeps the display aspect ratio, makes dimensions even, and turns anamorphic sources into square pixels.
- Deinterlacing with `bwdif`, only on frames that are actually interlaced.
- Audio is copied when it is already compatible with the profile, and re-encoded only when needed.
- **Queue**: pause/resume (suspends FFmpeg itself), stop, reorder, retry, per-file settings, an optional
  action when the queue finishes, and a "show FFmpeg command" button. Every job writes a log.
- **Never overwrites anything**: outputs are named `Name [Profile Codec 10bit 1080p Quality].ext`, and a counter is added when that name already exists.

## Quick start

### Docker (headless server, e.g. Proxmox + iGPU)

```bash
git clone https://github.com/KonjikiNoYami1991/TnA.git && cd TnA
# edit docker-compose.yml: media folder, optional password
RENDER_GID=$(getent group render | cut -d: -f3) docker compose up -d --build
```

Then open `http://<host>:8585`. See **[docs/PROXMOX.md](docs/PROXMOX.md)** for LXC GPU passthrough,
the native installer without Docker (`deploy/install-linux.sh`) and all environment variables.

### Desktop

```bash
dotnet run --project src/TnA.App -c Release
```

On Windows, *Settings → Download FFmpeg* fetches a full static build (BtbN, GPL: libass, x264, x265,
SVT-AV1, NVENC, QSV, AMF). On macOS, run `brew install ffmpeg`. On Linux, use your package manager.

### Web edition on your own PC

```bash
dotnet run --project src/TnA.Server -c Release -- --open
```

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the integration tests, FFmpeg on your PATH.

```bash
dotnet build TnA.slnx
dotnet test TnA.slnx
```

The tests use FFmpeg to build a sample MKV (anamorphic video, 5.1 audio, ASS and SRT subtitles, an attached font).
They then run real hardsub, softsub AV1 10-bit, remux, keyframe and pause/stop jobs, plus a headless render of the desktop window.

Self-contained builds:

```bash
dotnet publish src/TnA.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/TnA.App -c Release -r osx-arm64 --self-contained
dotnet publish src/TnA.Server -c Release -r linux-x64 --self-contained
```

## Project layout

```
src/TnA.Core     engine (no UI): probe, encoders, command builder, queue, hardware detection
src/TnA.Server   ASP.NET Core API + web UI (wwwroot), Docker entry point
src/TnA.App      Avalonia desktop app
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
