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

## 4. Chunked encoding: resumable and continuous

- **Resumable even when not parallel**: reuse the parallel-encoding logic to encode one segment at a time, so every
  encode can be resumed after a stop or crash, even with a single worker.
- **Continuous segment scheduling** (like av1an): today N similar segments finish at about the same time, N
  processes (FFmpeg, rigaya encoders, VideoToolbox) close together and the next N start all at once, which is a
  heavy, bursty load. Instead keep a steady pool: start the next segment as soon as any worker frees up, stagger the
  starts, and use segments of varied length / more segments than workers so the queue drains gradually.

## 5. Progress bar for every step of a job

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
