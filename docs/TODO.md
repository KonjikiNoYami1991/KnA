# KnA — next work (requested by the user)

Work through it in order, on `master`; ask before releasing. Every point is confirmed by the user. Guiding rule for
all of them: CLAUDE.md, "it is an *auto*encoder" — the user is never forced to do anything for an encode to come out
right; optional controls are fine, a required manual step or a silent loss is a bug.

## 1. Web server: separate user accounts

Done (see "Done" below): accounts, roles and permissions, security, migration from `KNA_PASSWORD`; each user's own
queue, presets and settings, advanced fields locked in the editor; reverse-proxy compatibility; proxy single sign-on.

## 2. Desktop app on a KnA server (client mode)

First and second part done (see "Done" below): connection, discovery, keychain, version check, certificate pinning,
the server's queue in the main window, uploads and results back home (also after the laptop was off); editor, previews,
OCR, AI assistant, presets, wizard and the server's own files through the server. Still to do, in this order:

- **Files the server reaches in place** (same NAS share): recognised by themselves, no upload.
- **Outbox** time / quota in Settings, shown in the web UI too; a notification when results are home.
- **Streaming and segment-by-segment** modes for servers with little space, chosen by KnA (keeping the PC awake,
  bandwidth measured).
- **Linux keyring without secret-tool**: talk to the Secret Service over D-Bus directly (the package must not need a
  system tool); Windows Credential Manager and macOS Keychain to be checked on real machines (point 5).

The original request, for reference:

When the user asks for it, the desktop app uses a KnA server as its engine instead of the local one: e.g. a powerful
PC (or homelab server) runs kna-server, and a laptop or an older PC runs the desktop app. Not a bare remote control:
the desktop app stays the same program with the same features, only the work (analysis, previews, encodes,
components, GPU) happens on the server.

- **Connection**: Settings → "Encode on a KnA server": address, account (the accounts of point 1), "remember";
  servers on the local network found by themselves (e.g. mDNS / a broadcast answer of kna-server), typed by hand
  otherwise. The password is kept in the system's keychain (Windows Credential Manager, macOS Keychain, libsecret),
  never in plain text. Switching back to "This computer" is always one click.
- **Same features as local**: queue, editor (tracks, trim, filters, parameter pages), previews and waveforms, OCR,
  presets, wizard, AI assistant — through the server's API (the same one the web UI uses), with live progress (SSE).
  The editor's options and the hardware shown are the server's (its GPU, its encoders, its components).
- **Autoencoder rule, no manual transfers**: a file of the laptop is uploaded by itself (chunked, resumable, already
  in the server) and the result comes back by itself into the output folder chosen on the laptop; files the server
  can already reach (the same NAS share) are used in place, without copying (KnA recognises them — path mapping
  found automatically, e.g. same size and checksum — never a path to type). Files of the server can be browsed and
  added too, like in the web UI.
- **The laptop can be switched off**: once sent (files uploaded), the jobs belong to the server; the results wait
  there in the account's outbox and, at the next connection, come back by themselves into the chosen output folders
  (resumable downloads, a notification when they are home), then are removed from the server. Outbox kept for a set
  time / up to a quota, said in Settings; the web UI shows it too.
- **Server with little space: streaming mode.** Only the server's engine (GPU, encoders) is used and it keeps
  nothing: the source is read from the client while it encodes and the encoded streams are sent back as they come
  out, written on the client. Notes for the design:
  - the source must be seekable (analysis, previews, two passes, chunked encoding, keyframe search, Dolby Vision /
    HDR10+ extraction): the client serves it with range requests (FFmpeg and the other readers open it as a URL) or
    through a small virtual file, not a one-way pipe;
  - the server sends back the elementary streams / an MKV written as it goes; what needs the whole file (MP4 index,
    mkvmerge, metadata injection, remux) runs on the client, which has the same tools in its package;
  - a dropped connection resumes from the last finished segment (the resumable / chunked encoding already there),
    never from scratch;
  - a middle way for long jobs: segments sent ahead and deleted on the server as soon as they are encoded and
    returned, so the server holds only a few at a time;
  - bandwidth: fine on wired gigabit even for UHD Blu-ray remuxes, Wi-Fi may be the bottleneck — KnA measures it and
    says the expected time.
- **KnA chooses by itself** (autoencoder rule), the user may override: full upload when the server has room for the
  source and the estimated output (so the laptop may then be switched off), streaming or segment-by-segment when it
  hasn't; streaming means the client must stay on and connected, and KnA says so before starting (and keeps the PC
  awake meanwhile).
- **Clear when it can't**: server unreachable, signed out, wrong version (API version check, with the update
  offered), a folder the account may not use, not enough space even for streaming — KnA says so plainly and offers to
  encode on this computer instead.
- **Security**: HTTPS when the server has it (reverse proxy of point 1), a self-signed certificate accepted once
  and pinned; HTTP only on the local network, with a warning.

## 3. Filter presets and before/after preview

- Every VapourSynth/AviSynth catalogue filter and every GPU filter (`--vpp-*`) gets **many sensible presets**: quality
  levels (light / medium / strong…) **and** content types (clean anime, grainy film, old VHS, noisy phone video…).
  Each preset is explained: what it does, when to use it, how much it costs in speed — not chosen "by feel". The
  numbers stay editable under "Advanced".
- **Before/after preview** of a frame right in the filter's box, to choose by looking.

## 4. Quick use first, "Pro" for the rest

Users often just want something fast ("compress this for WhatsApp, done"), but KnA must also have a Pro side that can
one day match StaxRip (which doesn't exist for Linux/macOS; there the reference is Hybrid).

- **Quick mode**: drop the file, pick the destination (WhatsApp, Telegram, Discord, YouTube, TV, archive…), optional
  maximum size, Start: KnA picks the GPU, a compatible codec and the size. The apps' size limits change: keep them
  verified and updatable, not hard-coded guesses.
- **Global "Pro" switch**: in quick mode the full editor, parameter pages and filter catalogue stay hidden.
- Optional (Pro): **quality check** (VMAF/SSIM on a few sample seconds) to compare presets and filters with a number.
- To evaluate, not a priority: importing Hybrid/StaxRip presets.

## 5. Checks on real hardware and runners

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
- mkvmerge in the packages: Windows x64/ARM64 from MKVToolNix's portable zip (only the static mkvmerge.exe is kept:
  it could not be started in the development container — check the bundle log and run it on Windows / Windows on
  Arm), Linux x64 from the AppImage (installed and verified in the development container), Linux ARM64 from Ubuntu's
  package with its libraries (needs the runner's glibc: check on older distributions), macOS from Homebrew with
  dylibbundler (Qt frameworks); package sizes.

## 6. Theme: Golden Darkness (last of the queue)

A UI theme dedicated to Golden Darkness (Yami) from *To LOVE-Ru*: colours from the character (black, gold, deep red),
suitable backgrounds, desktop and web, next to the current light/dark themes. Mind the artwork's rights: use images
the user provides or that may be redistributed; nothing scraped.

## Done (details in CHANGELOG)

- Desktop app on a KnA server, second part: `IEditorEngine` (`LocalEditorEngine` / `RemoteEditorEngine`) for the
  editor, wizard, AI assistant and OCR editor (`IOcrSession`); `?full=true` analysis, `/api/engine`, `/api/ocr/{id}/images`;
  the server's presets in the side panel (the preset manager points to the web page); "Server files" window; local
  externals uploaded (editor, drag and drop) and removed with the job. Checked live in the container: analysis,
  parameter pages, rate, size planner and estimate, previews, crop, command, external upload, presets, OCR read on the
  server with a correction used in the job; headless editor screenshots on a server's file.

- Desktop app on a KnA server, first part: Settings → KnA server (UDP discovery on 8585, address completed by itself,
  account, password in the system's keychain, `/api/version` check, self-signed certificate pinned, plain HTTP outside
  the LAN flagged); the main window shows the server's queue live (`IQueueEngine`: `QueueService` or `RemoteQueue`);
  local files uploaded by themselves, results downloaded into the output folder and collected (`/api/jobs/{id}/collected`
  deletes the upload and its result on the server), also at the next connection. Checked end to end in the container
  (upload, encode, result home, laptop switched off in between).

- Proxy single sign-on: `KNA_SSO` (`authelia`, `authentik` or a header name) believed only from
  `KNA_TRUSTED_PROXIES`, optional shared secret, accounts created at the first visit, roles from `KNA_SSO_ADMIN_GROUP`,
  `KNA_SSO_USER_GROUP`, sign-out to the portal. Checked with nginx standing in for the portal; real Authelia /
  Authentik only from their documentation.

- Reverse proxy: `X-Forwarded-*` only from `KNA_TRUSTED_PROXIES` (loopback always; the log names an untrusted proxy),
  `KNA_BASE_PATH` or `X-Forwarded-Prefix`, cookie path / Secure from them, relative URLs in the web UI, uploads that
  shrink their pieces on HTTP 413; guide with nginx, Caddy, Traefik and Nginx Proxy Manager examples
  (`docs/REVERSE-PROXY.md`). Checked in the container with real nginx (path kept and stripped) and Caddy; Traefik and
  NPM only from their documentation.

- Web server accounts, second part: each user has their own queue (one engine encodes everybody's in order; Stop
  stops only the user's files, Pause only while their own file runs), presets (built-in, shared = the administrators',
  own) and settings (quick-form defaults, theme); administrators see every job with its owner and manage each user's
  presets, settings and queue in Settings → Accounts; deleting an account removes them. The editor locks the
  hand-written code and argument fields for users without advanced mode (the global "Pro" switch of point 4 still to
  come).

- Web server accounts, first part: administrator created at the first start (or from `KNA_PASSWORD`, which keeps old
  HTTP Basic clients working until the password changes), users / administrators, per-user allowed folders and
  advanced mode, PBKDF2 hashes, HttpOnly SameSite cookies, sign-in throttling, `--reset-password` /
  `kna-reset-password`, `KNA_AUTH=off`; Settings → Accounts and the sign-in screen in the web UI (the desktop app is
  single-user: nothing to add there).

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
