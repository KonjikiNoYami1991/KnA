# KnA — next work (requested by the user)

Work through it in order, on `master`; ask before releasing. Every point is confirmed by the user. Guiding rule for
all of them: CLAUDE.md, "it is an *auto*encoder" — the user is never forced to do anything for an encode to come out
right; optional controls are fine, a required manual step or a silent loss is a bug.

## 1. Web server: separate user accounts

- **Accounts instead of the single shared password**: at first start KnA asks to create an **admin** account; the
  admin creates, edits, disables and removes the other accounts.
- Each user has their own queue, presets and settings; the admin sees and manages everything.
- **Roles**: at least admin / user, plus per-user permissions: allowed folder roots and whether advanced mode is
  allowed.
- **Security**: passwords stored hashed (salted, slow hash); secure session cookies; brute-force throttling.
- **Reverse-proxy compatible**: forwarded headers (X-Forwarded-For/Proto/Host) from trusted proxies only, a
  configurable base path (e.g. `/kna`), correct cookie paths and redirects, WebSocket/SSE progress through the proxy;
  documented examples for nginx, Caddy, Traefik and Nginx Proxy Manager.
- **Proxy SSO**: accept an auth header from a trusted proxy (Authelia/Authentik style) as the logged-in user.
- **Migration**: existing `KNA_PASSWORD` installs migrate smoothly (Docker, Linux, Windows service).

## 2. Filter presets and before/after preview

- Every VapourSynth/AviSynth catalogue filter and every GPU filter (`--vpp-*`) gets **many sensible presets**: quality
  levels (light / medium / strong…) **and** content types (clean anime, grainy film, old VHS, noisy phone video…).
  Each preset is explained: what it does, when to use it, how much it costs in speed — not chosen "by feel". The
  numbers stay editable under "Advanced".
- **Before/after preview** of a frame right in the filter's box, to choose by looking.

## 3. Quick use first, "Pro" for the rest

Users often just want something fast ("compress this for WhatsApp, done"), but KnA must also have a Pro side that can
one day match StaxRip (which doesn't exist for Linux/macOS; there the reference is Hybrid).

- **Quick mode**: drop the file, pick the destination (WhatsApp, Telegram, Discord, YouTube, TV, archive…), optional
  maximum size, Start: KnA picks the GPU, a compatible codec and the size. The apps' size limits change: keep them
  verified and updatable, not hard-coded guesses.
- **Global "Pro" switch**: in quick mode the full editor, parameter pages and filter catalogue stay hidden.
- Optional (Pro): **quality check** (VMAF/SSIM on a few sample seconds) to compare presets and filters with a number.
- To evaluate, not a priority: importing Hybrid/StaxRip presets.

## 4. Checks on real hardware and runners

Not verifiable in the development container; do them when a test release (workflow input `test_package`) or the
user's machines are available.

- The release workflow on the macOS (arm64 / Intel), Windows (x64 / ARM64) and Linux ARM runners: the VapourSynth
  component (portable Python + wheels, plugin check), signing inside KnA.app, package sizes.
- Point "autoencoder review" on real machines: GPU-first "Automatic" picking NVEncC/QSVEncC/VCEEncC, the hardware
  fingerprint after a driver update (Windows registry / Linux sysfs), telecine detection on real NTSC DVDs and anime
  (idet thresholds), the automatic crop on real films (scope, 4:3 pillarbox, films with variable bars), qaac's
  automatic install on Windows.
- rigaya's commands on real GPUs (NVIDIA / Intel / AMD): GPU filters, `--trim`, `--vpp-subburn`, `--vpy` with
  `--vsdir`, the speed/quality profiles, `--split-enc`, the mini benchmark.
- KNLMeansCL (OpenCL) and placebo.Deband (Vulkan) in the portable VapourSynth with a GPU driver present.
- HDR with real HDR10 / HDR10+ / Dolby Vision samples (through a catalogue chain, a trim, a crop, chunked encoding,
  hevc_nvenc and NVEncC): RPU / JSON extraction, the editor's frame ranges, `inject-rpu` / `inject` and the remux;
  NVEncC's `--dolby-vision-rpu <file>` together with `--trim` (KnA gives the uncut RPU to the encoder's own reader,
  because rigaya numbers the frames from the start of the source — verify); players / TVs recognising Dolby Vision in
  the output (configuration record written by mkvmerge).
- mkvmerge in the packages: Windows from MKVToolNix's portable 7z (download URL and folder layout not verifiable from
  the development container: check the bundle log), Linux from Ubuntu's package with its libraries (needs glibc ≥ the
  runner's: check on older distributions), macOS from Homebrew with dylibbundler (Qt frameworks), the in-app install
  on Linux x64 from the AppImage; package sizes.

## 5. Theme: Golden Darkness (last of the queue)

A UI theme dedicated to Golden Darkness (Yami) from *To LOVE-Ru*: colours from the character (black, gold, deep red),
suitable backgrounds, desktop and web, next to the current light/dark themes. Mind the artwork's rights: use images
the user provides or that may be redistributed; nothing scraped.

## Done (details in CHANGELOG)

- mkvmerge in every package and the Docker image: the Dolby Vision configuration record is written when the encoder
  leaves it out; injected metadata is remuxed by mkvmerge (FFmpeg can't time a raw HEVC stream).

- Autoencoder review: GPU first by default ("Automatic" encoder program), hardware detection that follows the machine
  (fingerprint), picture subtitles read with OCR instead of being lost, telecine detection (idet → IVTC / Bwdif /
  nothing), automatic crop of constant black bars (editable, switchable off), qaac installed by itself on first need.
  Optional by design: GPU mini benchmark, PaddleOCR / PyTorch.

- HDR carried through every path by itself: script video with the source's HDR data, explicit static metadata,
  Dolby Vision RPU / HDR10+ extracted, edited for trim and crop, given to the encoder or injected into the finished
  HEVC; frame-count changes keep static HDR and say so; HDR → SDR of script frames with the source's colours. HDR10+
  never needs rigaya's hdr10plus_gen module (HEVC: injected; AV1: copied by the encoder's own reader).

- Customization editor (the pencil) rebuilt: explicit settings only, Preset tab, encoder parameter pages, per-track
  Audio/Subtitles, GPU and VapourSynth/AviSynth filters, containers that fit the streams.
- Detailed preset wizard; AI assistant with guided set-up.
- Chunked encoding resumable with one worker, continuous segment scheduling; progress bar for every step of a job.
- Audio and Subtitles as tab views: per-track audio filters, "+"/✕/← →, duplicate tracks.
- GPU-first video with rigaya's encoders: KnA's filters as `--vpp-*`, native reading with filters/trim/burned
  subtitles, GPU/CPU pipeline with alternatives, scripts read by the encoder, speed/quality profiles, starting values
  from the GPU, AV1 only with hardware AV1, mini benchmark.
- Portable VapourSynth (Python 3.13 + R82 + vsjetpack + the catalogue's plugins) in every package and the Docker image;
  VapourSynth reads the video of CPU encodes.
