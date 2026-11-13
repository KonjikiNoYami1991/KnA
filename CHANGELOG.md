# Changelog

## v3.1.1 — 2026-11-13

### English

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

### Italiano

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
