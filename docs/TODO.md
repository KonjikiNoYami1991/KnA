# KnA — next work (requested by the user, not started yet)

Work through it in order, on `master`; ask before releasing. Items marked ✅ are done (see CHANGELOG).

## 1. ✅ Customization editor (the pencil) — needs a thorough overhaul

- **No duplicated settings anywhere.**
- **Fully independent from the default profile's automation.** The most it may do is *import* a profile and convert
  it into separate, visible settings that fill the fields.
- **Tracks tab: remove it.** Encoding settings don't belong there and the tab became useless. Before removing it, move
  its useful parts into the right sections: adding external files and excluding internal streams of *any* kind
  (video, audio, subtitles, fonts, attachments, …).
- **New "Preset" tab** in place of Tracks, before Trim. The preset picker currently in Output moves here.
- **Trim**: fine as it is.
- **Video**:
  - choosing an encoder shows **sub-tabs inside the Video tab** with **all** of that encoder's parameters: rigaya's
    NVEncC / QSVEncC / VCEEncC (all of them, not two or three), VAAPI, Vulkan and VideoToolbox (as complete as
    possible). Do not make the window bigger.
  - Picking a profile in the Video section also resets the video parameters (after a confirmation) so they match the
    profile. Example: "Bluray AAC" cannot use HEVC. Take the constraints from the original profile definitions in the
    repository history; don't make them up.
  - GPU encoders can be picked only when the matching GPU is present (web edition: the GPUs of the server, using the
    existing server-side detection).
- **Audio**: a tab view with **one tab per selected track**, each with the settings of **every** available audio
  encoder.
- **Subtitles**: the same structure as Audio, adapted to subtitles.
- **Filters**: the real filters of the chosen GPU encoder, plus complete VapourSynth and AviSynth filters, like
  StaxRip (but user-friendly, without its clunkiness).
- **Output**: offer only the containers that can hold the final streams (e.g. MP4 cannot hold FFV1).
- Desktop (Avalonia) and web (wwwroot) must get the same changes.

## 2. ✅ Preset wizard

Handy, but it must build the preset in a more specific, detailed way.

## 3. ✅ AI assistant

Handy, but it needs guided set-up procedures (wizards), or users give up.

## 4. ✅ Chunked encoding: resumable and continuous

- **Resumable even when not parallel**: reuse the parallel-encoding logic to encode one segment at a time, so every
  encode can be resumed after a stop or crash, even with a single worker.
- **Continuous segment scheduling** (like av1an): today N similar segments finish at about the same time, N
  processes (FFmpeg, rigaya encoders, VideoToolbox) close together and the next N start all at once, which is a
  heavy, bursty load. Instead keep a steady pool: start the next segment as soon as any worker frees up, stagger the
  starts, and use segments of varied length / more segments than workers so the queue drains gradually.

## 5. ✅ Progress bar for every step of a job

The progress bar in the queue item's card is nice, but today it only follows the video encode. After START it must
show progress (with the step's name) for every step of the job: archive extraction, probing, OCR, audio/subtitle
encoding, VapourSynth/AI filtering, muxing, size checks, cleanup, and so on. Desktop and web.

## 6. Web server: separate user accounts

All points below are confirmed by the user (required, not optional).

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

## 7. Editor: Audio and Subtitles as tab views (confirmed by the user)

Do this before item 8. Desktop and web.

- **No track lists left anywhere**: the tracks live only in their sections (Video, Audio, Subtitles; attachments and
  chapters in Output). The list at the top of the Audio and Subtitles tabs goes away.
- **Audio = a tab view**, one tab per selected and active audio track (3 tracks kept → 3 tabs), clearly visible as tabs
  even with a single track. Each tab holds everything about that track: language, title, delay, default/forced, codec,
  encoder, bitrate, channels, the parameters **of the chosen codec only** (pick Opus → Opus parameters), and its own
  **audio filters** (volume, loudness normalisation, downmix, sample rate) — no longer job-wide in the Filters tab.
- **"+"** next to the tabs: adds a track of the file or of an external file not yet active, or "Add external files…".
  **✕** on a tab removes the track; **← →** orders the tracks (= order in the output file).
- **Duplicate track**: the same source track twice with independent settings (e.g. Italian in Opus and in Vorbis).
  Tab label with the output codec ("#1 ita → Opus"); the copy's title is pre-filled with the codec ("Italiano (Vorbis)").
  Fix the places that assume a source track appears once: fdkaac/qaac pre-encode (`File|Stream` keys), VapourSynth
  script audio, `TrackPlanner.Complete` — every copy needs its own identity and intermediate file.
- **Subtitles: the same structure**, duplicate included (e.g. the same subtitle as ASS and as SRT), with format,
  burning and the OCR editor in each tab.

## 8. GPU-first video with rigaya's encoders, VapourSynth as standard (confirmed by the user)

Where StaxRip's speed comes from: the smart use of rigaya's parameters (NVEncC / QSVEncC / VCEEncC), not phase
parallelism. KnA must be **GPU-first** for video whenever possible, CPU only as a fallback. OCR stays a separate step
before the encode (as today).

- **Keep the frame on the GPU**: with a rigaya encoder, KnA translates its filters into the encoder's GPU filters
  (`--avhw` decoding + `--vpp-*`: crop, resize, deinterlace, denoise incl. `--vpp-nlmeans`, deband, sharpen, burned
  subtitles `--vpp-subburn`, HDR→SDR `--vpp-colorspace hdr2sdr`…) instead of passing through FFmpeg on the CPU.
  Check every option against the official NVEncC/QSVEncC/VCEEncC option documents before using it.
- **No silent fallback**: when a VapourSynth/AviSynth filter has no GPU equivalent, KnA says so and **proposes the GPU
  filter to use instead**. Splitting the load (CPU filter + GPU encode) only when it is really worth it, explained.
- **VapourSynth is the standard frameserver for encodes** (not remuxes), like StaxRip: the script is always generated
  and shown. When it contains only operations with a GPU equivalent, the same chain runs on the GPU and KnA says so
  ("run on the GPU, faster"); CPU-only filters (QTGMC, SMDegrain…) go through VapourSynth. Scripts are read by the
  rigaya encoder directly (`--vpy` / `--avs`, `--vsdir` for a portable VapourSynth) instead of through vspipe.
  AviSynth stays a Pro option (many plugins are Windows only).
- **Packages**: VapourSynth + the catalogue's plugins must be in every package (Windows, Linux, macOS) — CLAUDE.md
  standalone rule; Linux/macOS need a portable/relocatable build.
- **Speed/quality profiles per rigaya encoder** ("Best quality", "Balanced", "Fast"…), built on StaxRip's choices
  (study StaxRip's source: the defaults of its NVEnc/QSVEnc/VCEEnc classes) but adapted to KnA's GPU-first goal;
  explained, and editable in the parameter pages. `--split-enc` where the GPU has several NVENC engines.
- **Starting values from the detected GPU** (AV1 only where encoded in hardware, split encoding, speed presets per
  generation); optional one-time **mini benchmark** to estimate each job's time and suggest the preset.

## 9. Filter presets and before/after preview (confirmed by the user)

- Every VapourSynth/AviSynth catalogue filter and every GPU filter (`--vpp-*`) gets **many sensible presets**: quality
  levels (light / medium / strong…) **and** content types (clean anime, grainy film, old VHS, noisy phone video…).
  Each preset is explained: what it does, when to use it, how much it costs in speed — not chosen "by feel". The
  numbers stay editable under "Advanced".
- **Before/after preview** of a frame right in the filter's box, to choose by looking.

## 10. Quick use first, "Pro" for the rest (confirmed by the user)

Users often just want something fast ("compress this for WhatsApp, done"), but KnA must also have a Pro side that can
one day match StaxRip (which doesn't exist for Linux/macOS; there the reference is Hybrid).

- **Quick mode**: drop the file, pick the destination (WhatsApp, Telegram, Discord, YouTube, TV, archive…), optional
  maximum size, Start: KnA picks the GPU, a compatible codec and the size. The apps' size limits change: keep them
  verified and updatable, not hard-coded guesses.
- **Global "Pro" switch**: in quick mode the full editor, parameter pages and filter catalogue stay hidden.
- Optional (Pro): **quality check** (VMAF/SSIM on a few sample seconds) to compare presets and filters with a number.
- To evaluate, not a priority: importing Hybrid/StaxRip presets.
