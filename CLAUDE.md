# KnA — notes for Claude sessions

## Releases: always completely standalone

A person downloads **one** package, extracts it and uses KnA: nothing else to install, nothing missing, no hunting
for programs. Every package of `.github/workflows/release.yml` must already contain everything the features need:

- FFmpeg + FFprobe, fdkaac, Tesseract + the Italian and English OCR models, the archive engine (portable Python +
  py7zr), dovi_tool / hdr10plus_tool, the GPU encoders where the platform has them, shaders and upscale models,
  VapourSynth with the filter catalogue's plugins (component "vapoursynth": portable Python + wheels, every package);
- macOS: the tools live inside `KnA.app` (Homebrew builds made relocatable with dylibbundler);
- Linux: self-built static programs (fdkaac, Tesseract) so no system package is needed;
- Windows on Arm: native where possible, x64 builds (emulated) otherwise.

Only two things stay outside the packages, because they weigh gigabytes and depend on the GPU: **PaddleOCR** and
**PyTorch** (AI upscaler). They install with one click from Settings → Components, and KnA says so clearly when a
feature needs them.

Before tagging a release, check that a feature added since the last one does not depend on something the packages
lack. Releases are made only when the user asks (tag `v*` → draft release, published by hand).

## Working style

- The user writes in Italian; answers in Italian. Code, comments and commit messages in English.
- Work on `master`; commit and push when asked, never publish releases unless asked.
- UI text goes through `L.T(...)` with the Italian translation in `src/KnA.Core/Localization/L.it.cs`.
- Desktop (Avalonia, `src/KnA.App`) and web (`src/KnA.Server/wwwroot`) must offer the same features.

## Principle: it is an *auto*encoder

The user is never forced to do anything for an encode to come out right. KnA finds out by itself what the source has
(HDR10 / HDR10+ / Dolby Vision, colours, interlacing, tracks…) and carries it through every path (GPU, CPU, scripts)
without losses; extra tools run by themselves when needed. Study other programs (StaxRip, Hybrid) for their *logic*,
not their interface or workflow (no manual demux / indexing / extraction steps). Optional controls are fine; a
required manual step, or a silent loss the user would have to notice and fix, is a bug. When something really can't
be done automatically, KnA says so plainly and does the best safe thing. Confirmed by the user.

## Decisions

- **Blu-ray profiles (Blu-ray AAC / AC-3)**: keep KnA's values (H.264 High@4.1, `-maxrate 40000k -bufsize 30000k`,
  `-bluray-compat 1` with x264, see `VideoEncoders.AddProfileConstraints`), not the old TnA ones (50000k / 70000k).
  Confirmed by the user.

## Pending work

The user's open requests are in `docs/TODO.md`; `/master` resumes them.
