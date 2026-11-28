# Changelog

## v3.3.0 — unreleased

### English

**User accounts on the web server** (replace the single shared password)
- At the first start KnA asks to **create the administrator**; administrators create, edit, disable and remove the
  other accounts in Settings → Accounts, and can sign an account out on every device.
- **Roles**: administrators manage the server (settings, components, updates, watch folders, hardware test, accounts);
  users encode. Per user: **allowed folders** (inside the server's media roots, for browsing, uploads, downloads and
  jobs) and **advanced mode** (hand-written scripts, extra encoder / tool arguments, custom FFmpeg filters, a script as
  the source: off for new users; what the presets and defaults already contain keeps working).
- **Security**: passwords stored as salted PBKDF2-SHA256 hashes (600,000 iterations, upgraded at sign-in when the cost
  rises); HttpOnly, SameSite=Strict session cookies (Secure over HTTPS), of which only a SHA-256 is stored; "stay
  signed in" for 30 days, otherwise 12 idle hours; brute-force throttling per address and per user name (5 free
  attempts, then 2 s, 4 s, 8 s… up to 15 min, before any hash is computed); a password change signs the other devices
  out; account files readable only by the service user.
- **Migration from `KNA_PASSWORD`** (Docker, Linux and Windows services): the password becomes the administrator
  `admin` (`KNA_ADMIN_USER`); HTTP Basic with any user name and that password keeps working for scripts until it is
  changed. Lost password: `kna-reset-password` (Linux), `kna-server --reset-password NAME`, also while KnA runs.
  `KNA_AUTH=off` keeps an install without accounts. The Docker health check and the installers' checks no longer need
  a password.
- **Each user has their own queue, presets and settings**: a user sees and manages only the files they queued (the
  live progress, downloads, logs, OCR and the editor too: another account's job answers as if it did not exist); the
  server encodes everybody's in queue order. *Start* also resumes the user's stopped files, *Stop* stops only theirs
  (the others go on), *Pause* works while their own file is being encoded. Presets: built-in, **shared** (the
  administrators', seen by everybody) and **own** (a copy with a shared preset's name takes its place for that user);
  quick-form defaults and theme are kept per user. **Administrators see and manage everything**: every job with the
  name of whoever queued it, and in Settings → Accounts each user's presets (share them with everybody, delete them),
  settings (reset) and queue; deleting an account removes its presets, settings and waiting files.
- **Editor, advanced mode**: for users without it the fields that run hand-written code or arguments on the server
  (encoder parameter pages, audio/subtitle program parameters, custom audio/video/GPU filters, the hand-written
  VapourSynth/AviSynth script, container parameters) are locked, with a note saying who can allow them; the server
  keeps refusing them too. The options written by KnA's own rigaya speed/quality profiles are no longer counted as
  hand-written (a user without advanced mode can pick a profile).
- Fix: the editor's encoder parameter pages failed to open (a function was not imported).
- **Behind a reverse proxy** (nginx, Caddy, Traefik, Nginx Proxy Manager; guide with examples in
  `docs/REVERSE-PROXY.md`): `X-Forwarded-For/-Proto/-Host/-Prefix` are believed only from `KNA_TRUSTED_PROXIES`
  (addresses, networks, `private`; this machine always), so sign-in throttling sees the real client and the session
  cookie is `Secure` over HTTPS; headers from anybody else are ignored and the log says which address to trust.
  `KNA_BASE_PATH` (e.g. `/kna`) publishes KnA under a path, also when the proxy strips it and sends
  `X-Forwarded-Prefix`; cookies follow the path, `/kna` redirects to `/kna/`, the web UI uses relative addresses only.
  Uploads make their pieces smaller by themselves when a proxy refuses large bodies (HTTP 413). Checked with nginx and
  Caddy.
- **Single sign-on from the proxy** (Authelia, Authentik, oauth2-proxy, Cloudflare Access…): `KNA_SSO=authelia`,
  `authentik` or a header name; the user name is believed only from `KNA_TRUSTED_PROXIES` (optionally with a shared
  secret, `KNA_SSO_SECRET`; the log warns when whole networks are trusted without it). Accounts are created at the
  first visit (`KNA_SSO_CREATE=false` to allow only existing ones; the first account is the administrator), roles
  follow `KNA_SSO_ADMIN_GROUP`, `KNA_SSO_USER_GROUP` limits who may enter; "Sign out" leads to the portal
  (`KNA_SSO_LOGOUT_URL`). KnA's own sign-in and HTTP Basic keep working without the header.

**Desktop app on a KnA server** (client mode, first part)
- Settings → KnA server: servers on the local network found by themselves (UDP discovery on port 8585, answered by
  kna-server), or an address typed (completed by itself: `192.168.1.10` → `http://192.168.1.10:8585`, names → HTTPS);
  the account of point 1; the password in the system's keychain (Windows Credential Manager, macOS Keychain, Secret
  Service through secret-tool), never in a file. The server's version is checked (`/api/version`), too old or too new
  is said plainly; a self-signed HTTPS certificate is shown with its fingerprint and pinned once trusted; plain HTTP
  outside the local network is flagged. The top bar shows where the files are encoded; back to this computer is one
  click; an unreachable server leaves the work on this computer, saying why.
- The main window shows the server's queue with live progress: add files and folders, start, stop, pause, order,
  retry, remove, apply the side panel's settings, logs and commands; the hardware panel shows the server's.
- Local files are uploaded by themselves (resumable, smaller pieces through a strict proxy) and their results come back
  by themselves into the output folder or next to the source (resumable, never overwriting), then the server deletes
  the upload and its result. Once uploaded, the laptop may be switched off: results come home at the next connection.
- Second part: the desktop editor works on a server's files through its API (`IEditorEngine`: the whole analysis
  travels, previews and waveforms are downloaded into a cache, parameter pages, size estimate and planner, rate
  control from the server's hardware, segments split there); the OCR editor reads on the server (all pictures in one
  stream, corrections sent as they are made, "Use in the job" there, "Save as" here); the AI assistant and the wizard
  use the server's AI and hardware; the side panel's presets are the server's; "Server files" browses and queues the
  server's own files; subtitles, audio, fonts and attachments of this computer are uploaded by themselves (editor and
  drag and drop) and removed from the server with the job.
- Third part: files of this computer the server can read too (the same NAS share) are used in place instead of being
  uploaded — found by themselves (`/api/files/locate`: the end of the path under the account's folders, the size and a
  sampled SHA-256 must match), the mapping between the two folders is learnt for the next files; results written by
  the server into the share are already home (no copy), and the server keeps the share's files.

**Fixes**
- NVEncC / QSVEncC / VCEEncC with FFmpeg decoding: the size shown (and the final size estimated from it) was the raw
  frames sent to the encoder (~600 Mb/s at 1080p: a "394 GB" film); it is now the encoded file.
- HDR → SDR keeps 10 bits when the codec has them (AV1/HEVC "10-bit" presets no longer came out 8-bit), fewer bands.
- The log shows only the commands that really run (no FFmpeg encoder command next to the NVEncC one) and lists the
  tracks left out (e.g. PGS subtitles) instead of dropping them silently.
- Dolby Vision found from the frames when the container has no configuration record (old mkvmerge remuxes).

**Autoencoder review: KnA does it by itself**
- **GPU first by default**: the encoder program is "Automatic" — NVEncC / QSVEncC / VCEEncC of the chosen GPU when it
  is installed and makes the codec (AV1 only with hardware AV1), FFmpeg's GPU encoder otherwise; the editor shows the
  program that will really run, with its profile from the detected GPU.
- **Hardware detection follows the machine**: a fingerprint of the GPUs, their drivers and the encoder programs is
  checked at every start; when it changes the test runs again by itself.
- **Picture subtitles never lost**: PGS / VobSub that the container can't hold (MP4, WebM…) are read with OCR by
  themselves and kept as text (said in a warning) instead of being left out.
- **Telecine detection**: "Deinterlace: automatic" analyses a few seconds at three points (FFmpeg idet: combed frames
  and repeated fields) instead of trusting the field-order flag: telecined film gets inverse telecine back to 23.976
  (fieldmatch + decimate, `--vpp-ivtc` on the GPU), interlaced video Bwdif, mis-flagged progressive video nothing. New
  choice "Inverse telecine" in Deinterlace.
- **Automatic crop of black bars**: cropdetect on keyframes at 16 points of the film; only bars present in every
  sample are cut (never variable or partial bars), thin bars ignored, even values. Shown in Filters → Crop, editable,
  switchable off ("Automatic crop of black bars"); the Dolby Vision active area follows.
- **qaac without a manual step** (Windows): the first AAC encode that would fall back to fdkaac installs Apple's
  CoreAudioToolbox by itself (extracted on this computer from Apple's iTunes installer, never redistributed); fdkaac
  goes on if it fails.

**HDR carried through every path, by itself**
- **Video from a script** (catalogue chain, the standard VapourSynth frameserver or a hand-written script of the source)
  takes the source's colours and HDR metadata: HDR10 / HLG / HDR10+ / Dolby Vision are no longer lost silently with
  x265 or NVEncC/QSVEncC/VCEEncC reading `--vpy`/`--avs` or the y4m pipe. HDR sources now go through the standard
  VapourSynth frameserver too (only Dolby Vision profile 5 stays with FFmpeg's reader).
- **Static metadata written explicitly** whenever the encoder doesn't read the file itself: colour primaries,
  transfer, matrix, range, chroma location, mastering display and MaxCLL/MaxFALL (`copy`/`auto` only with
  `--avhw`/`--avsw`).
- **Dolby Vision** RPU extracted by KnA (dovi_tool) and given to x265 and to NVEncC/QSVEncC/VCEEncC as a file, with
  the profile derived from the source (8.1, 8.4 on HLG, 5.0 for profile 5, 10.x in AV1); profile 5 is now kept instead
  of being converted to SDR when the encoder can carry it.
- **Edited, not dropped**: a trim keeps only the kept frames in the RPU and in the HDR10+ JSON; a crop resets the RPU's
  active area; FFmpeg's GPU encoders, chunked and multi-part encodes and HDR10+ with rigaya's HEVC get the metadata
  injected into the finished HEVC (dovi_tool `inject-rpu`, hdr10plus_tool `inject`), checked frame by frame and
  remuxed. When frames are added or removed (frame-rate change, a script that changes the count) dynamic metadata
  can't follow: static HDR is kept and the reason is said.
- **HDR → SDR** of a script's video: the tone mapping knows the frames are PQ/HLG BT.2020 (FFmpeg `setparams`,
  explicit `--vpp-colorspace` input colours on the GPU); no HDR parameter reaches the encoder.
- **Dolby Vision recognised by TVs and players**: mkvmerge (MKVToolNix, now in every package and in the Docker image)
  writes the container's Dolby Vision configuration record that FFmpeg leaves out (x265, injected RPUs); the video is
  remuxed with every frame's original time, language, name and static HDR10 metadata, MP4 gets the record through
  FFmpeg. Injected metadata is remuxed by mkvmerge too: FFmpeg can't time a raw HEVC stream (that remux failed).
  Settings → Components installs MKVToolNix's own builds (Windows portable zip: only mkvmerge.exe; Linux x64 AppImage:
  mkvmerge with its libraries, for systems with glibc 2.14 or newer).
- The editor and the log list which HDR metadata is kept and how.

**Customization editor (the pencil), rebuilt**
- Tabs: **Preset** · Trim · Video · Audio · Subtitles · Filters · Output · Preview. The Tracks tab is gone: each kind of
  stream (video, audio, subtitles, attachments) is listed, kept or left out, ordered and labelled in its own tab, and
  external files can be added from any of them. Chapters and attachments are in Output.
- **Explicit settings only**: when the editor opens, what the profile would choose by itself (tracks, audio codec,
  encoder and bitrate of each track, subtitle formats, container, the quality value and speed preset behind a quality
  level) is written into the fields; a profile or preset can only be imported (Preset tab: "Import into the fields").
- **Picking a profile in the Video tab** asks first, then resets the video parameters to what the profile allows
  (e.g. Blu-ray AAC → H.264 8-bit, never HEVC) and lets it choose the audio and subtitle formats again.
- **Video**: sub-tabs General · Quality and speed · and one page per group (rate control, GOP, analysis, colour/HDR,
  profile, performance, input/output, other) with **every** parameter of the encoder that will run — FFmpeg's
  encoders (x264/x265/SVT-AV1, NVENC, QSV, AMF, VA-API, Vulkan, VideoToolbox) and rigaya's NVEncC / QSVEncC / VCEEncC
  — read from its own help, each with its value field and description. Options KnA sets itself are not listed twice.
- **GPU encoders only when the GPU is there** (the hardware test must have passed on it; on the server, its own GPUs).
- **Audio and Subtitles are tab views**: one tab per track of the output file (also with a single track), labelled with
  what it becomes ("#1 ita → Opus"); no track lists left. Each audio tab holds everything about its track: language,
  title, delay, default/forced, codec, the exact encoder (qaac, libfdk_aac, AudioToolbox, fdkaac, FFmpeg's aac,
  libopus…), bitrate, every parameter of that encoder only, and **its own audio filters** (volume, loudness
  normalisation, channels/downmix, sample rate, custom) — no longer job-wide in the Filters tab. **+** next to the tabs
  adds a track of the file or of an added file, duplicates the current one or adds external files; **✕** on a tab
  removes it; **← →** set the order in the output file. **Duplicate track**: the same source track twice with
  independent settings (e.g. Italian in Opus and in Vorbis, a subtitle as ASS and as SRT), the copy titled with its
  codec ("Italiano (Vorbis)"); fdkaac/qaac, VapourSynth script audio and OCR handle every copy. Subtitles: the same
  structure, with format, burning and the OCR editor in each tab. Desktop and web.
- **Filters**: the real **GPU filters** of the encoder — FFmpeg's (scale_cuda, vpp_qsv, denoise_vaapi, libplacebo…,
  frames uploaded to the GPU right before the encoder) or rigaya's --vpp-* — and a **VapourSynth / AviSynth+ filter
  list** like StaxRip's (QTGMC, Bwdif, IVTC, KNLMeansCL, DFTTest, SMDegrain, neo_f3kdb, CAS, LSFmod, AWarpSharp2,
  DeHalo_alpha, resize, crop, levels, grain…) with a form for each filter; the script is written from the list, or by hand.
- **Output** offers only the containers that can hold every final stream (e.g. no MP4 with FFV1, no WebM with FLAC).

**GPU-first video with NVEncC / QSVEncC / VCEEncC**
- **The frame stays on the GPU**: KnA's filters become the encoder's own GPU filters, so the encoder reads the file
  itself (`--avhw`; `--avsw` for formats its GPU cannot decode) even with filters: deinterlace (`--vpp-bwdif`), crop,
  HDR → SDR (`--vpp-colorspace hdr2sdr`, Hable / Mobius / BT.2390), denoise (`--vpp-hqdn3d`, the same values as
  FFmpeg's hqdn3d), deband, resize (`--output-res` + Lanczos), sharpen (`--vpp-cas`), burned subtitles
  (`--vpp-subburn`, text and PGS) and the trim (`--trim`, exact frames). Every option checked against the official
  option documents of NVEncC 9.41, QSVEncC 8.38 and VCEEncC 9.23.
- **No silent fallback**: the Filters tab shows where each step runs (GPU / CPU) and why; a filter without a GPU
  equivalent (custom FFmpeg filters, frame-rate change, HLG tone mapping, rotation, VFR trim…) is named in the
  editor, the warnings and the log, with the GPU filter to use instead where there is one.
- **Scripts read by the encoder**: VapourSynth / AviSynth scripts go straight to the encoder (`--vpy` / `--avs`,
  `--vsdir` for the portable VapourSynth on Windows) instead of vspipe → FFmpeg → pipe; a catalogue chain made only
  of filters the GPU has too (Bwdif, crop, resize, CAS, bit depth) runs on the GPU without the script. CPU-only
  filters (QTGMC, SMDegrain…) stay in VapourSynth and each shows its GPU alternative (`--vpp-rtgmc`,
  `--vpp-degrain`, `--vpp-nlmeans`…) with a **"Use the GPU filter instead"** button.
- **The script is always shown**: the whole picture chain as a VapourSynth script, each step marked with where it
  runs (Filters tab).
- **Speed / quality profiles** per encoder — Best quality, Balanced, Fast — built on StaxRip's defaults and moved
  towards GPU work (P7 + UHQ tune, lookahead, temporal AQ, `--split-enc` for HEVC/AV1 on NVENC; QSV/AMF presets with
  pyramid B-frames / VBAQ / pre-encode), explained and editable. **Starting values from the detected GPU** (newer
  generations with hardware AV1 start one step more careful); **AV1 only where the GPU encodes it in hardware**.
- **Optional GPU mini benchmark**: a 10-second 1080p clip encoded with each profile gives this job's expected time
  with each one and the suggested profile (Video → Quality and speed). Desktop and web.
- **VapourSynth in every package** (Windows x64/ARM64, Linux x64/ARM64, macOS, Docker): a portable Python 3.13 with
  VapourSynth R82, vsjetpack and every plugin of the filter catalogue (official wheels, fixed versions, checked by
  loading them when installed), nothing installed on the system; on Linux/macOS KnA writes VapourSynth's
  configuration at run time, so the folder can be moved. The catalogue's QTGMC, SMDegrain, DFTTest, LSFmod →
  FineSharp, AWarpSharp2, DeHalo_alpha, f3kdb and grain now use vsjetpack (the old havsfunc functions are gone);
  "NLMeans (CPU)" added for computers without OpenCL; filters whose plugin is missing are marked. L-SMASH's index
  goes to KnA's folder, not next to the video.
- **VapourSynth reads the video of CPU encodes** (like StaxRip; Settings → Processing, on by default): the script is
  the frameserver and KnA's filters follow. GPU encoders keep their GPU decoding; HDR, variable frame rate,
  interlaced, rotated and anamorphic sources, discs and MPEG streams stay with FFmpeg's reader, and the editor and
  the log say which one reads the video and why.

**Segmented encoding and progress**
- Encodes that can't be written as resumable pieces (encoders without forced keyframes) use the segment logic with one
  worker: a stop or crash only loses the segment being encoded.
- Parallel segments as a steady pool (like av1an): at least three segments per worker, varied lengths, shorter ones at
  the end and staggered first starts, so the encoders don't all start and finish together.
- The queue card's bar follows every step with its name: OCR and AI model downloads with their percentage, joining,
  result check; steps that can't be measured show a moving bar. Desktop and web.

**AI assistant: guided set-up**
- Settings → AI → "Guided set-up…" (and in the assistant when no model is chosen): which program runs the model,
  how to install and start it, address and key with a connection check that says what is wrong in plain words
  (not running, wrong port, key needed, missing /v1), text model with a real test question, optional vision model,
  summary. Desktop and web.

**Preset wizard, detailed**
- Up to 20 questions, only the ones that apply: what the result is for, kind of picture (tune), codec, encoder (only
  working GPUs), 8/10-bit, how the quality is decided (level, CRF/CQ value, bitrate or file size), resolution, HDR,
  picture clean-up, container, audio codec / bitrate / channels / loudness, languages to keep, commentaries,
  subtitles, external subtitles, OCR, segmented encoding; then a summary of what the preset will do. Same steps on
  desktop and web.

**Editor**
- One tab per kind of stream: Tracks · Trim · **Video** · **Audio** · **Subtitles** · Filters · Output · Preview. Every
  setting is in one place: the quality level and the hand-set rate control are a single choice; codec and bitrate of each
  audio track, format and burning of each subtitle moved from Tracks to their tabs.
- **Every parameter of every program**: the options of the encoder that will run (FFmpeg encoders, NVEncC/QSVEncC/VCEEncC),
  of the audio encoders (FFmpeg, fdkaac, qaac), of the subtitle encoder and of the container are read from the program's
  own help, searchable, with their allowed values; or written as text.
- **Maximum file size**: the video bitrate is computed from the size minus audio (copied tracks at their real bitrate),
  subtitles and container overhead, with a 3 % margin; a size larger than the video needs is capped.
- **Size estimate** (compressibility check): a few seconds encoded in several places predict the final size.

**Trim**
- Thumbnails load first and in parallel (HDR tone mapped), a hover preview follows the pointer, the frame fits the window.
- **Trim** button on every job: the cut alone, without the advanced editor (also in the web UI).

**Subtitle OCR (like Subtitle Edit)**
- Blu-ray picture subtitles (PGS) become text: KnA decodes the pictures itself and reads them with **Tesseract 5** and the
  language model of the track (official tessdata_best, downloaded on first use). Per track ("Text (OCR)" in the Subtitles
  tab), for every file ("Convert picture subtitles to text" in the settings and presets), or as a standalone
  "OCR → .srt" export. MP4 and players without PGS support get real subtitles.
- Settings → Components: Tesseract (Windows: the same 5.5.0 package Subtitle Edit uses; Linux/macOS: system package,
  installed by the Linux installer and in the Docker image) and the models of 12 languages. Windows packages include
  Tesseract; every package includes the Italian and English models.

- **DVD subtitles (VobSub)** too, from MKV, VOB/MPEG and .sub/.idx: KnA decodes the pictures with their palette.
- **OCR editor**, Subtitle Edit's OCR window improved: every picture next to its text, confidence colours and a "lines to
  check" filter, italics per line, reading again only the selected lines, find/replace (also regex), Enter = keep and
  next, then "Save SRT" or "Use in this job" (the SRT replaces the picture track). Desktop and web.
- **Italics**: Tesseract 4/5 no longer reports font styles, so KnA measures the slant of the letters on the picture and
  writes `<i>` by itself.
- **PaddleOCR** (PP-OCRv5, the standalone build Subtitle Edit uses) as a second engine, CPU or NVIDIA GPU, Windows and
  Linux x64; its 7-Zip packages are opened by a new **archive engine**: a standalone Python with py7zr, nothing installed
  on the system.

**Automation**
- Commands after each job and after the queue, with macros ({output}, {status}, {done}…).
- Command line: `kna-server --encode <files> [--preset …] [--size MB] [--trim a-b] …`, `--list-presets`.

**Working files and updates**
- Working files next to the source video (hidden `.kna-work`: temporary files, parallel segments, resumable pieces):
  same disk as the video, nothing big on the system drive; read-only folders and discs use the data folder by themselves.
  Removed when the job is done or taken out of the queue; never picked up as a source.
- **Automatic updates** built in: checked once a day, installed by KnA itself when the program folder may be written
  (no scripts, no PowerShell, no helper program from a temporary folder, SHA-256 of the package checked — what
  antivirus/EDR products such as Sophos MDR flag is not there). Without permission (Program Files without
  administrator, /opt/kna, Docker) it only tells you. Servers running as a service restart into the new version when
  the queue is idle.

**Releases and installation**
- One complete package per system: 9 downloads instead of about 26 (KnA-Windows-x64.zip, KnA-macOS-AppleSilicon.zip,
  KnA-Linux-x64.tar.gz, KnA-Server-…); fdkaac for the component manager in a separate "components" pre-release. The
  in-app updater of 3.2.1 and older does not know the new names: download 3.3.0 once by hand.
- **Every package complete** (CLAUDE.md): Tesseract built statically for Linux, FFmpeg and Tesseract from Homebrew made
  relocatable inside `KnA.app` (macOS 14+, no Homebrew needed), the archive engine (portable Python + py7zr) and the OCR
  models everywhere, x64 tools in the Windows on Arm package. Only PyTorch (AI upscaler) and PaddleOCR stay outside:
  KnA offers them with one click where they are needed.
- Docker: ready-made image `ghcr.io/konjikinoyami1991/kna` (amd64 + arm64), PUID/PGID/UMASK, GPU groups joined
  automatically; guides for Synology, QNAP, UGREEN, Unraid, Portainer (docs/DOCKER.md).
- Linux/LXC installer uses the complete package (FFmpeg and GPU encoders included); Windows server as a service
  (deploy/install-windows-server.ps1).

### Italiano

**Account utente nel server web** (al posto dell'unica password condivisa)
- Al primo avvio KnA chiede di **creare l'amministratore**; gli amministratori creano, modificano, disattivano ed
  eliminano gli altri account in Impostazioni → Account, e possono disconnettere un account su tutti i dispositivi.
- **Ruoli**: gli amministratori gestiscono il server (impostazioni, componenti, aggiornamenti, cartelle monitorate,
  test hardware, account); gli utenti codificano. Per utente: **cartelle consentite** (dentro le radici del server, per
  sfogliare, caricare, scaricare e codificare) e **modalità avanzata** (script scritti a mano, argomenti extra di
  encoder / programmi, filtri FFmpeg personalizzati, uno script come sorgente: spenta per i nuovi utenti; ciò che preset
  e predefiniti già contengono continua a funzionare).
- **Sicurezza**: password salvate come hash PBKDF2-SHA256 con salt (600.000 iterazioni, aggiornati all'accesso se il
  costo aumenta); cookie di sessione HttpOnly, SameSite=Strict (Secure in HTTPS), di cui si salva solo lo SHA-256;
  "resta connesso" per 30 giorni, altrimenti 12 ore di inattività; limite ai tentativi per indirizzo e per nome utente
  (5 tentativi liberi, poi 2 s, 4 s, 8 s… fino a 15 min, prima di calcolare qualsiasi hash); cambiare password
  disconnette gli altri dispositivi; file degli account leggibili solo dall'utente del servizio.
- **Migrazione da `KNA_PASSWORD`** (Docker, servizi Linux e Windows): la password diventa quella dell'amministratore
  `admin` (`KNA_ADMIN_USER`); HTTP Basic con qualsiasi nome utente e quella password continua a funzionare per gli
  script finché non viene cambiata. Password persa: `kna-reset-password` (Linux), `kna-server --reset-password NOME`,
  anche con KnA in esecuzione. `KNA_AUTH=off` mantiene un'installazione senza account. L'health check Docker e i
  controlli degli installer non richiedono più una password.
- **Ogni utente ha la propria coda, i propri preset e le proprie impostazioni**: un utente vede e gestisce solo i file
  che ha messo in coda (anche avanzamento in tempo reale, download, log, OCR ed editor: il job di un altro account
  risponde come se non esistesse); il server li codifica tutti nell'ordine della coda. *Avvia* riprende anche i file
  fermati dell'utente, *Ferma* ferma solo i suoi (gli altri proseguono), *Pausa* funziona mentre si codifica un suo
  file. Preset: integrati, **condivisi** (degli amministratori, visti da tutti) e **propri** (una copia con il nome di
  un preset condiviso ne prende il posto per quell'utente); predefiniti del modulo rapido e tema sono per utente. **Gli
  amministratori vedono e gestiscono tutto**: ogni job con il nome di chi l'ha messo in coda e, in Impostazioni →
  Account, preset (da condividere con tutti o eliminare), impostazioni (da riportare a quelle condivise) e coda di
  ogni utente; eliminare un account ne rimuove preset, impostazioni e file in attesa.
- **Editor, modalità avanzata**: per gli utenti che non ce l'hanno, i campi che eseguono codice o argomenti scritti a
  mano sul server (pagine dei parametri dell'encoder, parametri dei programmi audio/sottotitoli, filtri audio/video/GPU
  personalizzati, lo script VapourSynth/AviSynth scritto a mano, parametri del contenitore) sono bloccati, con una nota
  che dice chi può consentirli; il server continua comunque a rifiutarli. Le opzioni scritte dai profili di
  velocità/qualità rigaya di KnA non contano più come scritte a mano (un utente senza modalità avanzata può scegliere un
  profilo).
- Correzione: le pagine dei parametri dell'encoder nell'editor non si aprivano (una funzione non era importata).
- **Dietro un reverse proxy** (nginx, Caddy, Traefik, Nginx Proxy Manager; guida con esempi in
  `docs/REVERSE-PROXY.it.md`): `X-Forwarded-For/-Proto/-Host/-Prefix` sono creduti solo da `KNA_TRUSTED_PROXIES`
  (indirizzi, reti, `private`; questa macchina sempre), così il limite ai tentativi di accesso vede il client vero e il
  cookie di sessione è `Secure` in HTTPS; le intestazioni di chiunque altro sono ignorate e il log dice quale indirizzo
  rendere fidato. `KNA_BASE_PATH` (es. `/kna`) pubblica KnA sotto un percorso, anche quando il proxy lo toglie e invia
  `X-Forwarded-Prefix`; i cookie seguono il percorso, `/kna` porta a `/kna/`, l'interfaccia web usa solo indirizzi
  relativi. I caricamenti rimpiccioliscono da soli i pezzi quando un proxy rifiuta corpi grandi (HTTP 413). Provato con
  nginx e Caddy.
- **Single sign-on dal proxy** (Authelia, Authentik, oauth2-proxy, Cloudflare Access…): `KNA_SSO=authelia`,
  `authentik` o il nome di un'intestazione; il nome utente è creduto solo da `KNA_TRUSTED_PROXIES` (eventualmente con un
  segreto condiviso, `KNA_SSO_SECRET`; il log avvisa quando ci si fida di intere reti senza). Gli account vengono creati
  alla prima visita (`KNA_SSO_CREATE=false` per ammettere solo quelli esistenti; il primo account è l'amministratore),
  i ruoli seguono `KNA_SSO_ADMIN_GROUP`, `KNA_SSO_USER_GROUP` limita chi può entrare; "Esci" porta al portale
  (`KNA_SSO_LOGOUT_URL`). L'accesso di KnA e HTTP Basic continuano a funzionare senza l'intestazione.

**App desktop su un server KnA** (modalità client, prima parte)
- Impostazioni → Server KnA: i server della rete locale trovati da soli (scoperta UDP sulla porta 8585, a cui risponde
  kna-server), oppure un indirizzo scritto (completato da solo: `192.168.1.10` → `http://192.168.1.10:8585`, i nomi →
  HTTPS); l'account del punto 1; la password nel portachiavi di sistema (Gestione credenziali di Windows, Portachiavi
  di macOS, Secret Service tramite secret-tool), mai in un file. La versione del server viene controllata
  (`/api/version`), troppo vecchia o troppo nuova lo si dice chiaramente; un certificato HTTPS autofirmato viene
  mostrato con la sua impronta e fissato una volta accettato; l'HTTP semplice fuori dalla rete locale viene segnalato.
  La barra in alto mostra dove si codificano i file; tornare a questo computer è un clic; un server irraggiungibile
  lascia il lavoro su questo computer, dicendo perché.
- La finestra principale mostra la coda del server con l'avanzamento in tempo reale: aggiungi file e cartelle, avvia,
  ferma, pausa, ordine, riprova, rimuovi, applica le impostazioni del pannello laterale, log e comandi; il pannello
  hardware mostra quello del server.
- I file locali vengono caricati da soli (con ripresa, pezzi più piccoli con un proxy severo) e i risultati tornano da
  soli nella cartella di uscita o accanto alla sorgente (con ripresa, senza mai sovrascrivere), poi il server cancella
  il caricamento e il suo risultato. Una volta caricato, il portatile si può spegnere: i risultati tornano al
  collegamento successivo.
- Seconda parte: l'editor desktop lavora sui file del server tramite la sua API (`IEditorEngine`: l'analisi viaggia
  intera, anteprime e forme d'onda vengono scaricate in una cache, pagine dei parametri, stima e pianificazione della
  dimensione, controllo del bitrate dall'hardware del server, segmenti divisi lì); l'editor OCR legge sul server (tutte
  le immagini in un solo flusso, correzioni inviate man mano, "Usa nel job" lì, "Salva come" qui); assistente AI e
  wizard usano l'AI e l'hardware del server; i preset del pannello laterale sono quelli del server; "File del server"
  sfoglia e mette in coda i file del server; sottotitoli, audio, font e allegati di questo computer vengono caricati da
  soli (editor e trascinamento) e tolti dal server insieme al job.
- Terza parte: i file di questo computer che anche il server legge (la stessa condivisione del NAS) vengono usati dove
  sono invece di essere caricati — trovati da soli (`/api/files/locate`: fine del percorso sotto le cartelle
  dell'account, dimensione e SHA-256 a campione devono coincidere), la corrispondenza tra le due cartelle viene
  imparata per i file successivi; i risultati che il server scrive nella condivisione sono già a casa (nessuna copia) e
  il server non tocca i file della condivisione.

**Correzioni**
- NVEncC / QSVEncC / VCEEncC con decodifica FFmpeg: la dimensione mostrata (e quella finale stimata) era dei fotogrammi
  non compressi inviati all'encoder (~600 Mb/s a 1080p: un film da "394 GB"); ora è quella del file codificato.
- HDR → SDR mantiene i 10 bit quando il codec li ha (i preset AV1/HEVC "10 bit" non escono più a 8 bit), meno banding.
- Il log mostra solo i comandi eseguiti davvero (niente comando dell'encoder FFmpeg accanto a quello di NVEncC) ed elenca
  le tracce escluse (es. sottotitoli PGS) invece di scartarle in silenzio.
- Dolby Vision riconosciuto dai fotogrammi quando il contenitore non ha il record di configurazione (vecchi remux mkvmerge).

**Revisione da autoencoder: KnA fa da sé**
- **GPU prima di tutto, di default**: il programma di codifica è "Automatico" — NVEncC / QSVEncC / VCEEncC della GPU
  scelta quando è installato e fa il codec (AV1 solo con AV1 in hardware), altrimenti l'encoder GPU di FFmpeg; l'editor
  mostra il programma che girerà davvero, con il profilo ricavato dalla GPU rilevata.
- **Il rilevamento hardware segue la macchina**: a ogni avvio si controlla un'impronta di GPU, driver e programmi di
  codifica; se cambia, il test riparte da solo.
- **Sottotitoli a immagine mai persi**: PGS / VobSub che il contenitore non può tenere (MP4, WebM…) vengono letti con
  l'OCR da soli e mantenuti come testo (lo dice un avviso) invece di essere scartati.
- **Rilevamento del telecine**: "Deinterlaccia: automatico" analizza qualche secondo in tre punti (idet di FFmpeg:
  fotogrammi pettinati e campi ripetuti) invece di fidarsi del flag: il film telecinato ha il telecine inverso a 23,976
  (fieldmatch + decimate, `--vpp-ivtc` sulla GPU), il video interlacciato Bwdif, quello progressivo con il flag
  sbagliato niente. Nuova scelta "Telecine inverso" in Deinterlaccia.
- **Ritaglio automatico delle bande nere**: cropdetect sui fotogrammi chiave in 16 punti del film; si tagliano solo le
  bande presenti in tutti i campioni (mai bande variabili o parziali), quelle sottili sono ignorate, valori pari.
  Mostrato in Filtri → Ritaglio, modificabile e disattivabile ("Ritaglio automatico delle bande nere"); l'area attiva
  Dolby Vision segue.
- **qaac senza passaggi manuali** (Windows): la prima codifica AAC che ripiegherebbe su fdkaac installa da sola il
  CoreAudioToolbox di Apple (estratto su questo computer dall'installer di iTunes di Apple, mai ridistribuito); se non
  riesce si continua con fdkaac.

**HDR mantenuto in ogni percorso, da solo**
- **Video da uno script** (catena del catalogo, frameserver VapourSynth standard o script scritto a mano sulla
  sorgente): prende i colori e i metadati HDR della sorgente, quindi HDR10 / HLG / HDR10+ / Dolby Vision non si perdono
  più in silenzio con x265 o con NVEncC/QSVEncC/VCEEncC che leggono `--vpy`/`--avs` o la pipe y4m. Anche le sorgenti
  HDR passano ora dal frameserver VapourSynth standard (solo Dolby Vision profilo 5 resta al lettore di FFmpeg).
- **Metadati statici scritti esplicitamente** quando l'encoder non legge da sé il file: primari, transfer, matrice,
  range, posizione della crominanza, mastering display e MaxCLL/MaxFALL (`copy`/`auto` solo con `--avhw`/`--avsw`).
- **Dolby Vision**: l'RPU è estratto da KnA (dovi_tool) e passato come file a x265 e a NVEncC/QSVEncC/VCEEncC, con il
  profilo ricavato dalla sorgente (8.1, 8.4 su HLG, 5.0 per il profilo 5, 10.x in AV1); il profilo 5 ora viene
  mantenuto invece di essere convertito in SDR quando l'encoder lo può portare.
- **Modificati, non scartati**: un taglio tiene nell'RPU e nel JSON HDR10+ solo i fotogrammi tenuti; un ritaglio
  azzera l'area attiva dell'RPU; con gli encoder GPU di FFmpeg, la codifica a segmenti o a più parti e l'HDR10+ con
  l'HEVC di rigaya i metadati vengono iniettati nell'HEVC finito (dovi_tool `inject-rpu`, hdr10plus_tool `inject`),
  controllati fotogramma per fotogramma e rimuxati. Quando si aggiungono o tolgono fotogrammi (cambio di frame rate,
  uno script che cambia il numero) i metadati dinamici non possono seguire: resta l'HDR statico e viene detto perché.
- **HDR → SDR** del video di uno script: il tone mapping sa che i fotogrammi sono PQ/HLG BT.2020 (`setparams` di FFmpeg,
  colori di ingresso espliciti in `--vpp-colorspace` sulla GPU); nessun parametro HDR arriva all'encoder.
- **Dolby Vision riconosciuto da TV e lettori**: mkvmerge (MKVToolNix, ora in ogni pacchetto e nell'immagine Docker)
  scrive il record di configurazione Dolby Vision del contenitore che FFmpeg non mette (x265, RPU iniettati); il video
  viene rimuxato con i tempi originali di ogni fotogramma, lingua, nome e metadati HDR10 statici, l'MP4 riceve il
  record tramite FFmpeg. Anche i metadati iniettati vengono rimuxati con mkvmerge: FFmpeg non sa dare i tempi a un
  flusso HEVC grezzo (quel rimux falliva). Impostazioni → Componenti installa le build ufficiali di MKVToolNix (zip
  portable per Windows: solo mkvmerge.exe; AppImage per Linux x64: mkvmerge con le sue librerie, per sistemi con
  glibc 2.14 o più recente).
- L'editor e il log dicono quali metadati HDR vengono mantenuti e come.

**Editor di personalizzazione (la matita), rifatto**
- Schede: **Preset** · Taglio · Video · Audio · Sottotitoli · Filtri · Output · Anteprima. La scheda Tracce non c'è più:
  ogni tipo di stream (video, audio, sottotitoli, allegati) si tiene o si esclude, si ordina e si etichetta nella sua
  scheda, e da ognuna si possono aggiungere file esterni. Capitoli e allegati sono in Output.
- **Solo impostazioni esplicite**: all'apertura, ciò che il profilo sceglierebbe da solo (tracce, codec, encoder e
  bitrate di ogni traccia audio, formati dei sottotitoli, contenitore, valore di qualità e preset di velocità dietro a
  un livello di qualità) viene scritto nei campi; un profilo o un preset si può solo importare (scheda Preset:
  "Importa nei campi").
- **Scegliere un profilo nella scheda Video** chiede conferma, poi riporta i parametri video a ciò che il profilo
  permette (es. Blu-ray AAC → H.264 8 bit, mai HEVC) e gli fa scegliere di nuovo i formati audio e sottotitoli.
- **Video**: sotto-schede Generale · Qualità e velocità · e una pagina per gruppo (rate control, GOP, analisi,
  colore/HDR, profilo, prestazioni, ingresso/uscita, altro) con **tutti** i parametri dell'encoder che verrà usato — gli
  encoder di FFmpeg (x264/x265/SVT-AV1, NVENC, QSV, AMF, VA-API, Vulkan, VideoToolbox) e NVEncC / QSVEncC / VCEEncC di
  rigaya — letti dal suo help, ognuno con il suo campo e la sua descrizione. Le opzioni che KnA imposta da sé non
  compaiono due volte.
- **Encoder GPU solo se la GPU c'è** (deve aver superato il test hardware; sul server, le GPU del server).
- **Audio e Sottotitoli a schede**: una scheda per ogni traccia del file di output (anche con una sola traccia), con
  ciò che diventa ("#1 ita → Opus"); niente più elenchi di tracce. Ogni scheda audio contiene tutto della sua traccia:
  lingua, titolo, ritardo, predefinita/forzata, codec, encoder esatto (qaac, libfdk_aac, AudioToolbox, fdkaac, aac di
  FFmpeg, libopus…), bitrate, tutti i parametri solo di quell'encoder e **i suoi filtri audio** (volume,
  normalizzazione del volume, canali/downmix, frequenza di campionamento, personalizzati) — non più per tutto il lavoro
  nella scheda Filtri. **+** accanto alle schede aggiunge una traccia del file o di un file aggiunto, duplica quella
  attuale o aggiunge file esterni; **✕** su una scheda la toglie; **← →** decidono l'ordine nel file di output.
  **Duplica traccia**: la stessa traccia sorgente due volte con impostazioni indipendenti (es. italiano in Opus e in
  Vorbis, un sottotitolo come ASS e come SRT), la copia intitolata con il suo codec ("Italiano (Vorbis)"); fdkaac/qaac,
  l'audio degli script VapourSynth e l'OCR gestiscono ogni copia. Sottotitoli: la stessa struttura, con formato,
  hardsub ed editor OCR in ogni scheda. Desktop e web.
- **Filtri**: i veri **filtri GPU** dell'encoder — quelli di FFmpeg (scale_cuda, vpp_qsv, denoise_vaapi, libplacebo…,
  fotogrammi caricati sulla GPU subito prima dell'encoder) o i --vpp-* di rigaya — e un **elenco di filtri
  VapourSynth / AviSynth+** come quello di StaxRip (QTGMC, Bwdif, IVTC, KNLMeansCL, DFTTest, SMDegrain, neo_f3kdb, CAS,
  LSFmod, AWarpSharp2, DeHalo_alpha, ridimensiona, ritaglia, livelli, grana…) con un modulo per ogni filtro; lo script
  viene scritto dall'elenco, oppure a mano.
- **Output** propone solo i contenitori che possono contenere tutti gli stream finali (es. niente MP4 con FFV1, niente
  WebM con FLAC).

**Video GPU-first con NVEncC / QSVEncC / VCEEncC**
- **Il fotogramma resta sulla GPU**: i filtri di KnA diventano i filtri GPU dell'encoder, così l'encoder legge il file
  da sé (`--avhw`; `--avsw` per i formati che la sua GPU non decodifica) anche con i filtri: deinterlacciamento
  (`--vpp-bwdif`), ritaglio, HDR → SDR (`--vpp-colorspace hdr2sdr`, Hable / Mobius / BT.2390), riduzione rumore
  (`--vpp-hqdn3d`, gli stessi valori dell'hqdn3d di FFmpeg), deband, ridimensionamento (`--output-res` + Lanczos),
  nitidezza (`--vpp-cas`), sottotitoli impressi (`--vpp-subburn`, testo e PGS) e il taglio (`--trim`, fotogrammi
  esatti). Ogni opzione verificata sui documenti ufficiali delle opzioni di NVEncC 9.41, QSVEncC 8.38 e VCEEncC 9.23.
- **Nessun ripiego silenzioso**: la scheda Filtri mostra dove gira ogni passo (GPU / CPU) e perché; un filtro senza
  equivalente GPU (filtri FFmpeg personalizzati, cambio di frame rate, tone mapping HLG, rotazione, taglio di video
  VFR…) è nominato nell'editor, negli avvisi e nel log, con il filtro GPU da usare al suo posto quando esiste.
- **Script letti dall'encoder**: gli script VapourSynth / AviSynth vanno direttamente all'encoder (`--vpy` / `--avs`,
  `--vsdir` per il VapourSynth portable su Windows) invece di vspipe → FFmpeg → pipe; una catena del catalogo fatta
  solo di filtri che anche la GPU ha (Bwdif, ritaglio, ridimensionamento, CAS, profondità di bit) gira sulla GPU senza
  lo script. I filtri solo CPU (QTGMC, SMDegrain…) restano in VapourSynth e ognuno mostra la sua alternativa GPU
  (`--vpp-rtgmc`, `--vpp-degrain`, `--vpp-nlmeans`…) con il pulsante **"Usa invece il filtro GPU"**.
- **Lo script è sempre mostrato**: tutta la catena dell'immagine come script VapourSynth, ogni passo con il punto in
  cui gira (scheda Filtri).
- **Profili velocità / qualità** per encoder — Qualità massima, Bilanciato, Veloce — costruiti sui valori di StaxRip
  e spostati verso il lavoro sulla GPU (P7 + tune UHQ, lookahead, AQ temporale, `--split-enc` per HEVC/AV1 su NVENC;
  preset QSV/AMF con B-frame a piramide / VBAQ / pre-codifica), spiegati e modificabili. **Valori iniziali dalla GPU
  rilevata** (le generazioni con AV1 hardware partono un passo più accurate); **AV1 solo dove la GPU lo codifica in
  hardware**.
- **Mini benchmark GPU facoltativo**: una clip 1080p di 10 secondi codificata con ogni profilo dà il tempo previsto
  di questo lavoro con ciascuno e il profilo consigliato (Video → Qualità e velocità). Desktop e web.
- **VapourSynth in ogni pacchetto** (Windows x64/ARM64, Linux x64/ARM64, macOS, Docker): un Python 3.13 portable con
  VapourSynth R82, vsjetpack e tutti i plugin del catalogo dei filtri (wheel ufficiali, versioni fisse, verificati
  caricandoli all'installazione), niente installato nel sistema; su Linux/macOS KnA scrive la configurazione di
  VapourSynth all'avvio, così la cartella si può spostare. QTGMC, SMDegrain, DFTTest, LSFmod → FineSharp,
  AWarpSharp2, DeHalo_alpha, f3kdb e la grana del catalogo usano ora vsjetpack (le vecchie funzioni di havsfunc non
  esistono più); aggiunto "NLMeans (CPU)" per i computer senza OpenCL; i filtri il cui plugin manca sono segnalati.
  L'indice di L-SMASH va nella cartella di KnA, non accanto al video.
- **VapourSynth legge il video delle codifiche su CPU** (come StaxRip; Impostazioni → Elaborazione, attivo di
  predefinito): lo script è il frameserver e i filtri di KnA seguono. Gli encoder GPU mantengono la decodifica sulla
  GPU; le sorgenti HDR, a frame rate variabile, interlacciate, ruotate e anamorfiche, i dischi e i flussi MPEG restano
  al lettore di FFmpeg, e l'editor e il log dicono chi legge il video e perché.

**Codifica a segmenti e avanzamento**
- Le codifiche che non si possono scrivere a pezzi ripristinabili (encoder senza keyframe forzati) usano la logica dei
  segmenti con un solo processo: uno stop o un crash perde solo il segmento in corso.
- Segmenti in parallelo come una coda costante (stile av1an): almeno tre segmenti per processo, lunghezze variate, più
  corti alla fine e prime partenze sfalsate, così gli encoder non partono e finiscono tutti insieme.
- La barra della scheda in coda segue ogni fase con il suo nome: OCR e download del modello AI con la percentuale, unione,
  controllo del risultato; le fasi non misurabili mostrano una barra in movimento. Desktop e web.

**Assistente AI: configurazione guidata**
- Impostazioni → AI → "Configurazione guidata…" (e nell'assistente quando non c'è un modello): quale programma esegue
  il modello, come installarlo e avviarlo, indirizzo e chiave con una verifica della connessione che spiega a parole
  cosa non va (non avviato, porta sbagliata, chiave richiesta, /v1 mancante), modello di testo con una vera domanda di
  prova, modello vision facoltativo, riepilogo. Desktop e web.

**Wizard dei preset, dettagliato**
- Fino a 20 domande, solo quelle pertinenti: a cosa serve il risultato, tipo di immagine (tune), codec, encoder (solo
  GPU funzionanti), 8/10 bit, come si decide la qualità (livello, valore CRF/CQ, bitrate o dimensione del file),
  risoluzione, HDR, pulizia dell'immagine, contenitore, codec / bitrate / canali / volume dell'audio, lingue da tenere,
  commenti, sottotitoli, sottotitoli esterni, OCR, codifica a segmenti; poi un riepilogo di cosa farà il preset. Stessi
  passi su desktop e web.

**Editor**
- Una scheda per tipo di flusso: Tracce · Taglio · **Video** · **Audio** · **Sottotitoli** · Filtri · Uscita · Anteprima.
  Ogni impostazione sta in un solo posto: livello di qualità e controllo del bitrate a mano sono una sola scelta; codec e
  bitrate di ogni traccia audio, formato e impressione di ogni sottotitolo sono passati da Tracce alle loro schede.
- **Tutti i parametri di tutti i programmi**: le opzioni dell'encoder che verrà usato (encoder FFmpeg, NVEncC/QSVEncC/VCEEncC),
  degli encoder audio (FFmpeg, fdkaac, qaac), dell'encoder dei sottotitoli e del contenitore vengono lette dall'help del
  programma, con ricerca e valori ammessi; oppure scritte come testo.
- **Dimensione massima del file**: il bitrate video è calcolato dalla dimensione meno audio (tracce copiate al loro bitrate
  reale), sottotitoli e overhead del contenitore, con un margine del 3 %; una dimensione più grande del necessario viene limitata.
- **Stima della dimensione** (compressibility check): qualche secondo codificato in più punti prevede la dimensione finale.

**Taglio**
- Le miniature arrivano per prime e in parallelo (HDR con tone mapping), un'anteprima segue il puntatore, il fotogramma
  si adatta alla finestra.
- Pulsante **Taglia** su ogni lavoro: solo il taglio, senza l'editor avanzato (anche nell'interfaccia web).

**OCR dei sottotitoli (come Subtitle Edit)**
- I sottotitoli immagine dei Blu-ray (PGS) diventano testo: KnA decodifica da sé le immagini e le legge con **Tesseract 5**
  e il modello della lingua della traccia (tessdata_best ufficiali, scaricati al primo uso). Per traccia ("Testo (OCR)"
  nella scheda Sottotitoli), per ogni file ("Converti i sottotitoli immagine in testo" nelle impostazioni e nei preset)
  oppure come esportazione "OCR → .srt". MP4 e i lettori senza PGS ottengono sottotitoli veri.
- Impostazioni → Componenti: Tesseract (Windows: lo stesso pacchetto 5.5.0 di Subtitle Edit; Linux/macOS: pacchetto di
  sistema, installato dall'installer Linux e nell'immagine Docker) e i modelli di 12 lingue. I pacchetti Windows includono
  Tesseract; tutti i pacchetti includono i modelli italiano e inglese.

- Anche i **sottotitoli dei DVD (VobSub)**, da MKV, VOB/MPEG e .sub/.idx: KnA decodifica le immagini con la loro palette.
- **Editor OCR**, la finestra OCR di Subtitle Edit migliorata: ogni immagine accanto al suo testo, colori di confidenza e
  filtro "righe da controllare", corsivo per riga, rilettura delle sole righe selezionate, trova/sostituisci (anche regex),
  Invio = conferma e prosegui, poi "Salva SRT" o "Usa in questo lavoro" (l'SRT sostituisce la traccia immagine). Desktop e web.
- **Corsivi**: Tesseract 4/5 non riconosce più gli stili, quindi KnA misura l'inclinazione delle lettere sull'immagine e
  scrive da sé i tag `<i>`.
- **PaddleOCR** (PP-OCRv5, la build standalone usata da Subtitle Edit) come secondo motore, CPU o GPU NVIDIA, Windows e
  Linux x64; i suoi pacchetti 7-Zip li apre un nuovo **motore archivi**: un Python standalone con py7zr, niente installato
  nel sistema.

**Automazione**
- Comandi dopo ogni lavoro e a coda finita, con macro ({output}, {status}, {done}…).
- Riga di comando: `kna-server --encode <file> [--preset …] [--size MB] [--trim a-b] …`, `--list-presets`.

**File di lavoro e aggiornamenti**
- File di lavoro accanto al video sorgente (cartella nascosta `.kna-work`: file temporanei, segmenti paralleli, pezzi
  ripristinabili): stesso disco del video, niente di pesante sul disco di sistema; cartelle di sola lettura e dischi usano
  da soli la cartella dati. Rimossi a fine lavoro o quando il lavoro esce dalla coda; mai presi come sorgenti.
- **Aggiornamenti automatici** integrati: controllo una volta al giorno, installazione fatta da KnA stesso quando la
  cartella del programma è scrivibile (niente script, niente PowerShell, nessun programma d'appoggio lanciato dalla
  cartella temporanea, impronta SHA-256 del pacchetto verificata: ciò che antivirus/EDR come Sophos MDR segnalano non c'è).
  Senza permessi (Programmi senza amministratore, /opt/kna, Docker) ti avvisa soltanto. I server installati come
  servizio ripartono con la nuova versione a coda vuota.

**Release e installazione**
- Un pacchetto completo per sistema: 9 download invece di circa 26 (KnA-Windows-x64.zip, KnA-macOS-AppleSilicon.zip,
  KnA-Linux-x64.tar.gz, KnA-Server-…); fdkaac per il component manager in una pre-release "components" separata.
  L'aggiornamento interno della 3.2.1 e precedenti non conosce i nuovi nomi: scarica la 3.3.0 a mano una volta.
- **Ogni pacchetto completo** (CLAUDE.md): Tesseract compilato statico per Linux, FFmpeg e Tesseract di Homebrew resi
  rilocabili dentro `KnA.app` (macOS 14+, senza Homebrew), il motore archivi (Python portabile + py7zr) e i modelli OCR
  ovunque, strumenti x64 nel pacchetto Windows su Arm. Restano fuori solo PyTorch (upscaler AI) e PaddleOCR: KnA li
  offre con un clic dove servono.
- Docker: immagine pronta `ghcr.io/konjikinoyami1991/kna` (amd64 + arm64), PUID/PGID/UMASK, gruppi GPU aggiunti da soli;
  guide per Synology, QNAP, UGREEN, Unraid, Portainer (docs/DOCKER.it.md).
- L'installer Linux/LXC usa il pacchetto completo (FFmpeg ed encoder GPU inclusi); server Windows come servizio
  (deploy/install-windows-server.ps1).

## v3.2.1 — 2026-11-14

### English

- **VapourSynth (Windows)**: R74 and later ship as a pip package; KnA now installs it into a portable Python (3.12+) inside
  tools\vapoursynth, with BestSource and L-SMASH-Works in its autoload folder, so it is included again in the Windows
  portable packages and installs from Settings → Components. Also found when installed in the per-user data folder.

### Italiano

- **VapourSynth (Windows)**: dalla R74 è un pacchetto pip; KnA ora lo installa in un Python portabile (3.12+) dentro
  tools\vapoursynth, con BestSource e L-SMASH-Works nella sua cartella di caricamento automatico, quindi torna nei pacchetti
  portable per Windows e si installa da Impostazioni → Componenti. Viene trovato anche se installato nella cartella dati dell'utente.

## v3.2.0 — 2026-11-14

### English

**Resumable encodes**
- Every video encode can be stopped and continued: one FFmpeg writes the video in 60 s pieces (forced IDR at each cut), and after a
  stop, a crash or a restart the next run starts exactly after the last finished piece (frame-exact, no quality loss, no extra
  cost while it runs). *Start* continues stopped jobs by itself, without asking. Audio and subtitles are made alongside the video.

**Speed**
- **Hardware decoding**: with GPU encoders the source is decoded on the GPU (NVDEC, D3D11VA, VA-API, VideoToolbox). A UHD HEVC remux
  decoded by the CPU was what limited NVENC encodes (and every parallel segment decoded its own part). Setting: with GPU encoders /
  always / off.
- Parallel (segmented) encoding: split points found by seeking near each step instead of reading the whole file first; no preroll
  per segment unless temporal filters are on.
- **Previews**: a still frame no longer decodes and filters ~8 s of video first (5 frames instead of ~200 through tone mapping,
  scaling and subtitles); a frame the playhead already left is cancelled; frames never wait behind the thumbnails and the waveform.

**Encode settings and presets**
- **Advanced encoder** (editor ✎ → Output, preset manager): constant quality value (CRF/CQ/ICQ/QP) or average bitrate with maximum,
  encoder speed preset, tune, keyframe interval, B-frames, lookahead, extra encoder options.
- **Track selection** in presets: audio and subtitle languages to keep (in order of preference), leave out commentaries and
  attachments; applied to every file's automatic track plan.
- **Preset manager**: every setting of a preset editable, new / duplicate / rename / update from the side panel / delete,
  import and export; **apply only some parts** (video, audio, subtitles, picture filters, track selection) to the selected files,
  or to the open file from the editor.

**rigaya's GPU encoders: NVEncC, QSVEncC, VCEEncC** (Windows and Linux x64)
- New *Encoder program* in the advanced encoder settings: FFmpeg or NVEncC (NVIDIA) / QSVEncC (Intel) / VCEEncC (AMD).
- When no FFmpeg filter is needed (a plain resize at most) the encoder reads the source itself with GPU decoding (--avhw),
  resizes on the GPU, copies **Dolby Vision (profile 8.1) and HDR10+** and, with *parallel encoding* on, splits the file between
  the GPU's encoder engines (--parallel): the StaxRip way. Otherwise FFmpeg decodes, filters, burns subtitles and trims, and
  sends y4m frames to the encoder. If the GPU reader refuses a file, the FFmpeg path runs by itself.
- Installed from Settings → Components (Windows .7z, Linux .deb unpacked in tools\); a GPU whose FFmpeg encoder fails
  (old driver) can still be chosen when its rigaya encoder is installed. macOS keeps VideoToolbox (rigaya has no macOS builds).

**Audio: qaac**
- **qaac** (Apple Core Audio AAC / HE-AAC, Windows) is the first AAC choice, then libfdk_aac / fdkaac, then FFmpeg's own.
  Apple's CoreAudioToolbox is extracted on your PC from the official iTunes x64 installer (makeportable2 method); KnA never
  redistributes Apple's files.

**Models**
- Settings → Components lists the **Real-ESRGAN upscale models** (download / reinstall, also while the queue runs) and the
  **assistant models** (qwen3, qwen2.5-vl, qwen3-vl, gemma3) pulled through **Ollama** with progress; the first pulled text /
  vision model becomes the assistant's model when none is set.

**CPU**
- x86-64 level detection (v2 / v3 AVX2 / v4 AVX-512), shown with the hardware info; on v4 CPUs (Ryzen 7000/9000, Intel with
  AVX-512) x265 runs its AVX-512 kernels (asm=avx512). .NET, FFmpeg, SVT-AV1 and dav1d pick AVX2/AVX-512 by themselves.

**Previews: no rendering while zapping**
- While the playhead or a trim handle is dragged only the nearest timeline thumbnail is shown; the real frame is rendered when
  the pointer is released (desktop and web). Keyboard steps still render after a short pause.

**Portable, Docker, Proxmox**
- One executable plus a few native libraries. The portable packages (desktop **and now server**) contain FFmpeg, fdkaac,
  NVEncC / QSVEncC / VCEEncC, qaac (without Apple's files), **VapourSynth portable with embedded Python, BestSource and
  L-SMASH-Works** (Windows), dovi_tool, hdr10plus_tool, the Anime4K / FSRCNNX shaders and the five Real-ESRGAN models; they are
  filled by KnA's own component manager (`kna-server --bundle`). PyTorch stays a download (its build depends on the GPU).
- Docker image: NVEncC / QSVEncC / VCEEncC installed with their dependencies (build argument RIGAYA); docker-compose has an
  optional **Ollama** service (`--profile llm`).
- Linux / Proxmox installers: rigaya encoders for the GPUs found, the AnimeVideo v3 model, and the system libraries they need.


**HDR → SDR**
- The SDR picture was too bright: libplacebo measured every scene (dynamic peak detection) and stretched dim ones towards white,
  and its contrast recovery lifted flat areas further. Now the brightness follows the mastering metadata and stays stable;
  scene-by-scene detection is an option (*Adapt brightness scene by scene*).
- **Tone-mapping curve** to choose: Spline (default, balanced), BT.2390 (the previous curve, brighter), BT.2446 method A (ITU, darker),
  Hable (filmic), Mobius (brightest). Measured on a 100-nit grey: 70 % (Mobius), 69 % (BT.2390), 56 % (Spline), 53 % (Hable), 51 % (BT.2446a).
- **No HDR metadata in SDR files**: recent FFmpeg versions passed the source's mastering display and MaxCLL/MaxFALL on to the encoder,
  so a BT.709 file was labelled "HDR SMPTE ST 2086" and some players/TVs showed it with the wrong brightness. They are removed now.

**Queue**
- Finished jobs are recognisable at a glance: coloured status badge with an icon (✓ done, ✓ done with warnings, ✕ error, ■ stopped,
  ▶ running) and a coloured card border, on desktop and web.

**Fixes**
- Editor: "Don't burn any subtitle" stays available after a subtitle is chosen again; opening a tab no longer turns the automatic
  track plan or the file's chapters into custom ones.
- Segmented encoding: audio codec chosen for the final container (WebM), attachment MIME types, delayed tracks kept.

### Italiano

**Codifiche riprendibili**
- Ogni codifica video si può fermare e continuare: un solo FFmpeg scrive il video in pezzi da 60 s (IDR forzato a ogni taglio) e
  dopo uno stop, un crash o un riavvio la codifica riparte esattamente dopo l'ultimo pezzo completato (al fotogramma, senza perdita di
  qualità, senza costi aggiuntivi mentre gira). *Avvia* riprende da solo i lavori fermati, senza chiedere. Audio e sottotitoli
  vengono preparati in parallelo al video.

**Velocità**
- **Decodifica hardware**: con gli encoder GPU la sorgente viene decodificata dalla GPU (NVDEC, D3D11VA, VA-API, VideoToolbox). Era la
  decodifica CPU di un remux UHD HEVC a limitare NVENC (e ogni segmento parallelo decodificava la sua parte). Impostazione: con gli
  encoder GPU / sempre / spenta.
- Codifica parallela (a segmenti): i punti di taglio si trovano con un seek vicino a ogni passo invece di leggere prima tutto il
  file; nessun preroll per segmento salvo con filtri temporali.
- **Anteprime**: un fotogramma non decodifica e filtra più ~8 s di video prima (5 fotogrammi invece di ~200 attraverso tone mapping,
  scala e sottotitoli); il fotogramma di una posizione già lasciata dalla testina viene annullato; i fotogrammi non aspettano più
  miniature e forma d'onda.

**Impostazioni di codifica e preset**
- **Encoder avanzato** (editor ✎ → Uscita, gestione preset): valore di qualità costante (CRF/CQ/ICQ/QP) o bitrate medio con massimo,
  preset di velocità dell'encoder, tune, intervallo dei keyframe, B-frame, lookahead, opzioni extra dell'encoder.
- **Selezione tracce** nei preset: lingue audio e sottotitoli da tenere (in ordine di preferenza), esclusione di commenti e allegati;
  applicata al piano tracce automatico di ogni file.
- **Gestione preset**: ogni impostazione modificabile, nuovo / duplica / rinomina / aggiorna dal pannello laterale / elimina,
  importa ed esporta; **applica solo alcune parti** (video, audio, sottotitoli, filtri immagine, selezione tracce) ai file selezionati,
  o al file aperto dall'editor.

**Encoder GPU di rigaya: NVEncC, QSVEncC, VCEEncC** (Windows e Linux x64)
- Nuovo *Programma di codifica* nelle impostazioni dell'encoder avanzato: FFmpeg oppure NVEncC (NVIDIA) / QSVEncC (Intel) / VCEEncC (AMD).
- Quando non serve alcun filtro FFmpeg (al massimo un ridimensionamento) l'encoder legge da solo la sorgente con decodifica GPU
  (--avhw), ridimensiona sulla GPU, copia **Dolby Vision (profilo 8.1) e HDR10+** e, con la *codifica parallela* attiva, divide il file
  tra i motori di codifica della GPU (--parallel): il metodo di StaxRip. Altrimenti FFmpeg decodifica, filtra, imprime i sottotitoli e
  taglia, e passa all'encoder i fotogrammi y4m. Se il lettore GPU rifiuta un file, parte da solo il percorso FFmpeg.
- Si installano da Impostazioni → Componenti (Windows .7z, Linux .deb estratto in tools\); una GPU il cui encoder FFmpeg fallisce
  (driver vecchio) si può scegliere lo stesso se il suo encoder rigaya è installato. Su macOS resta VideoToolbox (rigaya non ha build macOS).

**Audio: qaac**
- **qaac** (AAC / HE-AAC di Apple Core Audio, Windows) è la prima scelta per l'AAC, poi libfdk_aac / fdkaac, poi l'encoder di FFmpeg.
  CoreAudioToolbox di Apple viene estratto sul tuo PC dall'installer ufficiale di iTunes x64 (metodo makeportable2); KnA non
  ridistribuisce mai i file di Apple.

**Modelli**
- Impostazioni → Componenti elenca i **modelli di upscale Real-ESRGAN** (scarica / reinstalla, anche con la coda in corso) e i
  **modelli per l'assistente** (qwen3, qwen2.5-vl, qwen3-vl, gemma3) scaricati tramite **Ollama** con avanzamento; il primo modello
  testo / visivo scaricato diventa quello dell'assistente se non ce n'è uno impostato.

**CPU**
- Rilevamento del livello x86-64 (v2 / v3 AVX2 / v4 AVX-512), mostrato con le informazioni hardware; sulle CPU v4 (Ryzen 7000/9000,
  Intel con AVX-512) x265 usa i suoi kernel AVX-512 (asm=avx512). .NET, FFmpeg, SVT-AV1 e dav1d scelgono AVX2/AVX-512 da soli.

**Anteprime: nessun rendering durante lo zapping**
- Mentre si trascina la testina o una maniglia del taglio si vede solo la miniatura più vicina della timeline; il fotogramma vero
  viene generato al rilascio del puntatore (desktop e web). I passi da tastiera generano il fotogramma dopo una breve pausa.

**Portable, Docker, Proxmox**
- Un solo eseguibile più poche librerie native. I pacchetti portable (desktop **e ora anche server**) contengono FFmpeg, fdkaac,
  NVEncC / QSVEncC / VCEEncC, qaac (senza i file di Apple), **VapourSynth portable con Python incorporato, BestSource e
  L-SMASH-Works** (Windows), dovi_tool, hdr10plus_tool, gli shader Anime4K / FSRCNNX e i cinque modelli Real-ESRGAN; li riempie il
  gestore componenti di KnA stesso (`kna-server --bundle`). PyTorch resta da scaricare (la build dipende dalla GPU).
- Immagine Docker: NVEncC / QSVEncC / VCEEncC installati con le loro dipendenze (argomento di build RIGAYA); docker-compose ha un
  servizio **Ollama** facoltativo (`--profile llm`).
- Installer Linux / Proxmox: encoder rigaya per le GPU trovate, il modello AnimeVideo v3 e le librerie di sistema che servono.


**HDR → SDR**
- L'immagine SDR era troppo luminosa: libplacebo misurava ogni scena (rilevamento dinamico del picco) e spingeva quelle scure verso il bianco,
  e il suo contrast recovery schiariva ancora le zone piatte. Ora la luminosità segue i metadati di mastering e resta stabile;
  il rilevamento scena per scena è un'opzione (*Adatta la luminosità scena per scena*).
- **Curva di tone mapping** a scelta: Spline (default, bilanciata), BT.2390 (la curva precedente, più luminosa), BT.2446 metodo A (ITU, più scura),
  Hable (cinematografica), Mobius (la più luminosa). Misurato su un grigio a 100 nit: 70 % (Mobius), 69 % (BT.2390), 56 % (Spline), 53 % (Hable), 51 % (BT.2446a).
- **Niente metadati HDR nei file SDR**: le versioni recenti di FFmpeg passavano all'encoder mastering display e MaxCLL/MaxFALL della sorgente,
  così un file BT.709 risultava "HDR SMPTE ST 2086" e alcuni player/TV lo mostravano con la luminosità sbagliata. Ora vengono rimossi.

**Coda**
- I lavori conclusi si riconoscono a colpo d'occhio: badge di stato colorato con icona (✓ completato, ✓ completato con avvisi, ✕ errore,
  ■ fermato, ▶ in corso) e bordo della scheda colorato, sia nell'app desktop sia nel web.

**Correzioni**
- Editor: "Non imprimere sottotitoli" resta disponibile dopo aver scelto di nuovo un sottotitolo; aprire una scheda non rende più
  personalizzati il piano tracce automatico o i capitoli del file.
- Codifica a segmenti: codec audio scelto per il contenitore finale (WebM), MIME type degli allegati, tracce con ritardo mantenute.

## v3.1.0 — 2026-11-13

### English

**Per-file editor — Tracks**
- Every stream of the file is listed (video, audio, subtitles, attachments) in every profile, **Demux included**; plans saved by
  older versions get the missing tracks back (unticked).
- **Copy or convert each track from the list**: video *copy* (no re-encoding), audio *copy* or AAC / HE-AAC / Opus / AC-3 / E-AC-3 /
  FLAC / … with its own bitrate, subtitles *copy* or SRT / ASS / WebVTT / mov_text, checked against the container.
- **Attachments**: add any file (fonts, covers, text…) with the right MIME type, remove the added ones, untick the file's own.
- **Chapters**: listed with time and title; add, remove, rename, move, "every 5 minutes", back to the file's chapters.
- **Demux**: the ticked tracks, attachments and chapters are what gets extracted (the old "Streams to extract" box is gone).

**Trim with several segments**
- Keep as many parts as you like: they are **joined into one file** (each part made with the job's settings, then joined without
  re-encoding; chapters are moved onto the joined timeline) or turned into **N queue jobs**, one per segment ("… - part N").

**AI upscaler runtime (Python + PyTorch)**
- **No Python needed**: when no suitable Python (3.11–3.14) is installed, KnA downloads a portable one (python-build-standalone,
  SHA-256 verified) into its data folder. Current PyTorch requires Python 3.11+: older environments are rebuilt.
- PyTorch build matched to the machine: the newest **CUDA build the NVIDIA driver supports** (RTX 50 / Blackwell included),
  newest ROCm on AMD/Linux, MPS on macOS, CPU otherwise; the GPU is checked after installing.
- Update check against PyPI: *Components* shows installed vs latest PyTorch and offers the update.

**Installers**
- Native installers: Proxmox VE container script and a wizard for Linux / LXC (Debian, Ubuntu, Fedora, Arch).
- Proxmox script: single review screen, language choice, rollback on failure, bind mounts picked from the host's mount points,
  resize and update of existing KnA containers, disk sized from the choices.

**Fixes**
- Editor: the selected buttons (segment, *Final*) were unreadable in the desktop app.
- ShellCheck warnings in the installers.
- fdkaac for macOS is built again (one build per architecture joined with `lipo`): it was missing from v3.0.0.

### Italiano

**Editor per file — Tracce**
- Vengono elencati tutti i flussi del file (video, audio, sottotitoli, allegati) in ogni profilo, **Demux compreso**; i piani salvati con
  le versioni precedenti riottengono le tracce mancanti (senza spunta).
- **Copia o converti ogni traccia dalla lista**: video *copia* (senza ricodifica), audio *copia* oppure AAC / HE-AAC / Opus / AC-3 / E-AC-3 /
  FLAC / … con il proprio bitrate, sottotitoli *copia* oppure SRT / ASS / WebVTT / mov_text, verificati con il contenitore.
- **Allegati**: aggiungi qualsiasi file (font, copertine, testi…) con il MIME type corretto, rimuovi quelli aggiunti, escludi quelli del file.
- **Capitoli**: elencati con tempo e titolo; aggiungi, rimuovi, rinomina, sposta, "ogni 5 minuti", torna ai capitoli del file.
- **Demux**: vengono estratti tracce, allegati e capitoli spuntati (il vecchio riquadro "Flussi da estrarre" non c'è più).

**Taglio a più segmenti**
- Tieni quante parti vuoi: vengono **unite in un unico file** (ogni parte prodotta con le impostazioni del lavoro, poi unite senza
  ricodifica; i capitoli vengono spostati sulla nuova timeline) oppure diventano **N elementi in coda**, uno per segmento ("… - parte N").

**Runtime dell'upscaler AI (Python + PyTorch)**
- **Python non serve**: se non c'è un Python adatto (3.11–3.14), KnA ne scarica uno portabile (python-build-standalone, verificato con
  SHA-256) nella sua cartella dati. L'attuale PyTorch richiede Python 3.11+: gli ambienti più vecchi vengono ricreati.
- Build di PyTorch adatta alla macchina: la **build CUDA più recente supportata dal driver NVIDIA** (RTX 50 / Blackwell comprese),
  ROCm più recente su AMD/Linux, MPS su macOS, CPU negli altri casi; dopo l'installazione viene verificata la GPU.
- Controllo aggiornamenti su PyPI: *Componenti* mostra la versione di PyTorch installata e la più recente e propone l'aggiornamento.

**Installer**
- Installer nativi: script per container Proxmox VE e procedura guidata per Linux / LXC (Debian, Ubuntu, Fedora, Arch).
- Script Proxmox: schermata di riepilogo unica, scelta della lingua, ripristino in caso di errore, bind mount scelti tra i punti di mount
  dell'host, ridimensionamento e aggiornamento dei container KnA esistenti, disco dimensionato in base alle scelte.

**Correzioni**
- Editor: i pulsanti selezionati (segmento, *Finale*) erano illeggibili nell'app desktop.
- Avvisi ShellCheck negli installer.
- fdkaac per macOS torna a essere compilato (una build per architettura unite con `lipo`): mancava nella v3.0.0.

## v3.0.0 — 2026-11-13

First release of KnA 3 (Konjiki no Autoencoder): desktop app and web server on .NET 10, rewritten from Tanoshimi no Autohardsubber.
See the [README](README.md) for the full feature list.
