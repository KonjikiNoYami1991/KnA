# KnA — next work (requested by the user)

Work through it in order, on `master`; ask before releasing. Every point is confirmed by the user. Guiding rule for
all of them: CLAUDE.md, "it is an *auto*encoder" — the user is never forced to do anything for an encode to come out
right; optional controls are fine, a required manual step or a silent loss is a bug.

## 1. HDR carried through every path, by itself

**Fully automatic**: no option, no button, no extraction step for the user. KnA detects the HDR of the source,
extracts and edits RPU / HDR10+ by itself and passes everything to the encoder. StaxRip is the reference for the logic
only, not for its interface or workflow (`Source/Encoding/NVEnc.vb` SetMetaData/BeforeEncoding,
`Source/Forms/MainForm.vb` ExtractHdr10PlusMetadata / ExtractDolbyVisionMetadata,
`Source/General/DolbyVisionMetadataFile.vb`). Option names checked against the NVEncC 9.41, QSVEncC 8.38 and
VCEEncC 9.23 option documents: `copy`/`auto` work only with the `--avhw`/`--avsw` readers, a script needs explicit
values and files.

- **Video from a script** (catalogue chain or hand-written): today the script is probed without HDR data, so
  `HdrPlanner` treats it as SDR and HDR10 / HDR10+ / Dolby Vision are lost silently with NVEncC/QSVEncC/VCEEncC
  (`--vpy`/`--avs` or the y4m pipe) and with FFmpeg/x265. When the script derives from the job's source, use the
  source's HDR data (primaries, transfer, matrix, range, mastering display, MaxCLL/MaxFALL, HDR10+, Dolby Vision).
- **Static metadata, explicit** with a script: rigaya gets `--colorprim/--transfer/--colormatrix/--colorrange`,
  `--master-display G(..)B(..)R(..)WP(..)L(max,min)`, `--max-cll MaxCLL,MaxFALL` (and `--chromaloc`), never `copy`;
  x265 gets the same from the source's data.
- **HDR10+**: extract the JSON (`hdr10plus_tool extract`, already used for x265) and pass `--dhdr10-info <json>`;
  check that rigaya's `hdr10plus_gen` module it needs is in every package (CLAUDE.md standalone rule).
- **Dolby Vision**: extract the RPU (`dovi_tool extract-rpu`, already used for x265) and pass
  `--dolby-vision-rpu <rpu>` with `--dolby-vision-profile` derived from the RPU (5 → 5.0, 7/8 → 8.1, 8.1 on HLG → 8.4;
  AV1 → 10.x).
- **Edit the metadata instead of dropping it** (today DV/HDR10+ are dropped with only a warning on a trim, a crop,
  chunked encoding, FFmpeg's GPU encoders hevc_nvenc/qsv/amf…, and rigaya's encoders when a filter forces the pipe):
  crop → RPU active area (`dovi_tool editor`, or `--dolby-vision-rpu-prm crop=true`); trim → keep only the kept frames
  in the RPU and in the HDR10+ JSON; when the encoder can't take them, inject them into the finished HEVC
  (`dovi_tool inject-rpu`, `hdr10plus_tool inject`, then remux), so every HEVC path keeps DV/HDR10+. Check what
  dovi_tool / hdr10plus_tool support for AV1 before promising it there.
- **Frame-count changes** (frame-rate change, double-rate deinterlace such as QTGMC, IVTC/decimate, script trims):
  dynamic metadata no longer matches frame by frame → static HDR10 only, said clearly; never dropped silently.
- **HDR → SDR**: when the output is SDR (tone mapping: H.264/8-bit, "convert to SDR", DV profile 5…) no HDR parameter
  reaches the encoder (no `--master-display`, `--max-cll`, `--dhdr10-info`, `--dolby-vision-*`, no BT.2020/PQ/HLG
  tags): output tagged BT.709. The source's HDR data is still used *for the conversion*: the tone mapping of a script's
  video must know the source is PQ/HLG BT.2020 (y4m from vspipe carries no colour data), so it runs on the source
  before/inside the script or with the input colours set explicitly — never on frames assumed SDR.
- Show in the editor's pipeline / log which HDR metadata is kept and how; tests with HDR10, HDR10+ and DV samples
  through a catalogue chain, a trim, a crop and chunked encoding.

## 2. The rest of the autoencoder review

Found by reading the code. Each point: KnA does it by itself, the user can still change it, the log / editor say
what happened.

- **GPU-first by default**: today the encoder program is FFmpeg unless the user picks NVEncC/QSVEncC/VCEEncC in the
  Video tab. When the rigaya encoder of the chosen GPU is in the package and supports the codec, it is the default
  (FFmpeg's GPU encoder only as fallback), with its profile from the detected GPU.
- **Hardware detection that follows the machine**: today it runs again only for a new detection schema or a new
  FFmpeg; after a GPU or driver change the GPU encoders stay "not detected" / wrong. Re-detect by itself when the
  GPUs, the driver versions or the encoder programs change (cheap fingerprint at start).
- **Picture subtitles never lost**: PGS/VobSub that the container can't hold (MP4/WebM) are left out with a warning
  unless OCR is on. When a picture subtitle would be dropped, OCR it automatically (Tesseract and the it/en models are
  in every package; other languages download by themselves) and keep the text track.
- **Telecine detection**: "Deinterlace: automatic" only looks at the field-order flag, so telecined film (NTSC DVD,
  anime, 29.97 hard/soft telecine) gets Bwdif (judder, blended frames) instead of IVTC back to 23.976. Analyse a few
  seconds (FFmpeg `idet` / repeat-field flags, like StaxRip / Hybrid detection) and choose IVTC (`fieldmatch+decimate`,
  VIVTC in the script, `--vpp-ivtc`/`--vpp-decimate` on the GPU), deinterlace or nothing.
- **Automatic crop of black borders** (approved by the user): letterbox / pillarbox bars removed by themselves, from
  several sample points spread over the film; only bars constant over the whole film (never variable or partial bars,
  never cutting into the picture), even values, safe margin; shown and editable in the editor (Filters → crop) and
  switchable off; the Dolby Vision RPU active area follows (point 1).
- **qaac without a manual step**: Apple's CoreAudioToolbox needs Settings → Components → qaac → Install; until then
  fdkaac is used (no failure). Do it by itself on first need, where the licence allows it (extracted on the user's
  machine, never redistributed).
- Optional by design, fine as they are: GPU mini benchmark, PaddleOCR / PyTorch (CLAUDE.md exceptions; KnA says when a
  feature needs them).

## 3. Web server: separate user accounts

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

## 4. Filter presets and before/after preview

- Every VapourSynth/AviSynth catalogue filter and every GPU filter (`--vpp-*`) gets **many sensible presets**: quality
  levels (light / medium / strong…) **and** content types (clean anime, grainy film, old VHS, noisy phone video…).
  Each preset is explained: what it does, when to use it, how much it costs in speed — not chosen "by feel". The
  numbers stay editable under "Advanced".
- **Before/after preview** of a frame right in the filter's box, to choose by looking.

## 5. Quick use first, "Pro" for the rest

Users often just want something fast ("compress this for WhatsApp, done"), but KnA must also have a Pro side that can
one day match StaxRip (which doesn't exist for Linux/macOS; there the reference is Hybrid).

- **Quick mode**: drop the file, pick the destination (WhatsApp, Telegram, Discord, YouTube, TV, archive…), optional
  maximum size, Start: KnA picks the GPU, a compatible codec and the size. The apps' size limits change: keep them
  verified and updatable, not hard-coded guesses.
- **Global "Pro" switch**: in quick mode the full editor, parameter pages and filter catalogue stay hidden.
- Optional (Pro): **quality check** (VMAF/SSIM on a few sample seconds) to compare presets and filters with a number.
- To evaluate, not a priority: importing Hybrid/StaxRip presets.

## 6. Checks on real hardware and runners

Not verifiable in the development container; do them when a test release (workflow input `test_package`) or the
user's machines are available.

- The release workflow on the macOS (arm64 / Intel), Windows (x64 / ARM64) and Linux ARM runners: the VapourSynth
  component (portable Python + wheels, plugin check), signing inside KnA.app, package sizes.
- rigaya's commands on real GPUs (NVIDIA / Intel / AMD): GPU filters, `--trim`, `--vpp-subburn`, `--vpy` with
  `--vsdir`, the speed/quality profiles, `--split-enc`, the mini benchmark.
- KNLMeansCL (OpenCL) and placebo.Deband (Vulkan) in the portable VapourSynth with a GPU driver present.

## 7. Theme: Golden Darkness (last of the queue)

A UI theme dedicated to Golden Darkness (Yami) from *To LOVE-Ru*: colours from the character (black, gold, deep red),
suitable backgrounds, desktop and web, next to the current light/dark themes. Mind the artwork's rights: use images
the user provides or that may be redistributed; nothing scraped.

## Done (details in CHANGELOG)

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
