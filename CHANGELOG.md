# Changelog

## v3.3.0 — unreleased

### English

**Fixes**
- NVEncC / QSVEncC / VCEEncC with FFmpeg decoding: the size shown (and the final size estimated from it) was the raw
  frames sent to the encoder (~600 Mb/s at 1080p: a "394 GB" film); it is now the encoded file.
- HDR → SDR keeps 10 bits when the codec has them (AV1/HEVC "10-bit" presets no longer came out 8-bit), fewer bands.
- The log shows only the commands that really run (no FFmpeg encoder command next to the NVEncC one) and lists the
  tracks left out (e.g. PGS subtitles) instead of dropping them silently.
- Dolby Vision found from the frames when the container has no configuration record (old mkvmerge remuxes).

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

**Automation**
- Commands after each job and after the queue, with macros ({output}, {status}, {done}…).
- Command line: `kna-server --encode <files> [--preset …] [--size MB] [--trim a-b] …`, `--list-presets`.

**Releases and installation**
- One complete package per system: 9 downloads instead of about 26 (KnA-Windows-x64.zip, KnA-macOS-AppleSilicon.zip,
  KnA-Linux-x64.tar.gz, KnA-Server-…); fdkaac for the component manager in a separate "components" pre-release. The
  in-app updater of 3.2.1 and older does not know the new names: download 3.3.0 once by hand.
- Docker: ready-made image `ghcr.io/konjikinoyami1991/kna` (amd64 + arm64), PUID/PGID/UMASK, GPU groups joined
  automatically; guides for Synology, QNAP, UGREEN, Unraid, Portainer (docs/DOCKER.md).
- Linux/LXC installer uses the complete package (FFmpeg and GPU encoders included); Windows server as a service
  (deploy/install-windows-server.ps1).

### Italiano

**Correzioni**
- NVEncC / QSVEncC / VCEEncC con decodifica FFmpeg: la dimensione mostrata (e quella finale stimata) era dei fotogrammi
  non compressi inviati all'encoder (~600 Mb/s a 1080p: un film da "394 GB"); ora è quella del file codificato.
- HDR → SDR mantiene i 10 bit quando il codec li ha (i preset AV1/HEVC "10 bit" non escono più a 8 bit), meno banding.
- Il log mostra solo i comandi eseguiti davvero (niente comando dell'encoder FFmpeg accanto a quello di NVEncC) ed elenca
  le tracce escluse (es. sottotitoli PGS) invece di scartarle in silenzio.
- Dolby Vision riconosciuto dai fotogrammi quando il contenitore non ha il record di configurazione (vecchi remux mkvmerge).

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

**Automazione**
- Comandi dopo ogni lavoro e a coda finita, con macro ({output}, {status}, {done}…).
- Riga di comando: `kna-server --encode <file> [--preset …] [--size MB] [--trim a-b] …`, `--list-presets`.

**Release e installazione**
- Un pacchetto completo per sistema: 9 download invece di circa 26 (KnA-Windows-x64.zip, KnA-macOS-AppleSilicon.zip,
  KnA-Linux-x64.tar.gz, KnA-Server-…); fdkaac per il component manager in una pre-release "components" separata.
  L'aggiornamento interno della 3.2.1 e precedenti non conosce i nuovi nomi: scarica la 3.3.0 a mano una volta.
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
