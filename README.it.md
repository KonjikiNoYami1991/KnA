# KnA - Konjiki no Autoencoder 2.0

[English](README.md) · **Italiano**

Un hardsubber e encoder quasi automatico by KonjikiNoYami, riscritto da zero per **.NET 10**.

Trascina qualche episodio MKV e scegli un profilo o un preset. KnA trova la traccia di sottotitoli giusta, estrae i font allegati,
imprime i sottotitoli con libass e codifica il video su **CPU o GPU** (NVENC, Quick Sync, AMF, VideoToolbox, VA-API),
compresi **AV1 e HEVC a 10 bit**, **HDR10 / HDR10+ / Dolby Vision** e la **codifica a segmenti** riprendibile.

| Edizione web (Docker / Proxmox / qualsiasi browser) | Edizione desktop (Windows / macOS / Linux) |
|---|---|
| ![Interfaccia web](docs/screenshots/web-dark.png) | ![Interfaccia desktop](docs/screenshots/desktop-dark.png) |
| ![Editor web: taglio visivo](docs/screenshots/web-editor-trim-it.png) | ![Editor desktop: tracce](docs/screenshots/desktop-editor-tracks-it.png) |

## Due interfacce, un solo motore

| | **Edizione web** `src/KnA.Server` | **Edizione desktop** `src/KnA.App` |
|---|---|---|
| Gira su | Docker, LXC/VM Proxmox, NAS o in locale | Windows, macOS (anche Apple Silicon), Linux |
| Interfaccia | Pagina web responsive (anche da telefono), avanzamento in diretta via SSE | Finestra Avalonia nativa, drag & drop, Ctrl+V, Invia a |
| File | Sfogliati sul server (radici multimediali) | Dischi locali |

Entrambe usano **`KnA.Core`**, che si occupa di analisi dei file, costruzione dei comandi, coda, anteprime, preset, aggiornamenti e log.
L'interfaccia è in **italiano** se il sistema (o il browser) è in italiano, altrimenti in **inglese**; si può forzare da *Impostazioni → Generale*.

## Funzioni

### Codifica
- **Profili**: Universale MKV (H.264/HEVC/AV1), Streaming/Web MP4, Blu-ray AAC / AC-3, Workraw, XviD+MP3,
  Remux→MP4, Remux→MKV, **Immagini** (PNG / JPEG / TIFF / WebP / BMP), **Demux** e keyframe per Aegisub.
- **Encoder hardware** (ognuno viene verificato con una vera codifica di prova, non solo letto da `ffmpeg -encoders`):

  | Backend | H.264 | HEVC | AV1 | 10 bit |
  |---|---|---|---|---|
  | CPU (x264 / x265 / SVT-AV1) | ✓ | ✓ | ✓ | HEVC, AV1 |
  | NVIDIA NVENC | ✓ | ✓ | ✓ (RTX 40/50) | HEVC, AV1 |
  | Intel Quick Sync | ✓ | ✓ | ✓ (Arc / Core Ultra) | HEVC, AV1 |
  | AMD AMF (Windows) | ✓ | ✓ | ✓ (RDNA 3+) | HEVC, AV1 |
  | VA-API (Linux, Intel/AMD) | ✓ | ✓ | ✓ (se la GPU lo supporta) | HEVC, AV1 |
  | Apple VideoToolbox | ✓ | ✓ | — (Apple Silicon non ha un encoder AV1 hardware) | HEVC |

- **Codifica a segmenti** (stile av1an): il video viene diviso sui keyframe, i segmenti vengono codificati in parallelo
  e un lavoro fermato **riparte dall'ultimo segmento completato**. Ideale per gli encoder lenti su CPU (x265, SVT-AV1).
- **HDR**: l'HDR10 viene mantenuto (metadati mastering display e luminanza passati all'encoder) oppure **convertito in SDR**
  (libplacebo se disponibile, altrimenti zscale + Hable). **Dolby Vision** (RPU tramite `dovi_tool`) e **HDR10+**
  (tramite `hdr10plus_tool`) vengono conservati ricodificando in HEVC con x265; KnA spiega quando non è possibile (taglio, ritaglio, cambio di frame rate, encoder GPU).
- **Contenitori**: MKV, MP4, MOV, WebM, AVI, TS, con controllo di compatibilità con i codec scelti
  (un AVI non può contenere HEVC, gli allegati richiedono Matroska, e così via).

### Editor per file
Ogni file in coda ha un editor (✎) con cinque schede:

1. **Tracce**: usa, rimuovi o sostituisci i flussi della sorgente; aggiungi **audio, sottotitoli, font e altri file esterni**.
   I file MKV / MKA / MP4 vengono analizzati e spunti i flussi da importare. Ogni traccia ha lingua, titolo,
   flag predefinita/forzata e un proprio **ritardo** (video, audio e sottotitoli). Un sottotitolo può essere impresso.
2. **Taglio**: timeline visiva con miniature e forma d'onda dell'audio, maniglie trascinabili, spostamenti al fotogramma.
   **Il taglio è sempre applicato per ultimo**, dopo tracce importate, ritardi, filtri e sottotitoli impressi: un sottotitolo
   importato con un ritardo resta sincronizzato anche dopo il taglio.
3. **Filtri**: orientamento (i video ruotati dei telefoni vengono riconosciuti; rotazione e specchiatura si possono correggere),
   deinterlacciamento, ritaglio (con rilevamento delle bande nere), riduzione rumore, deband, nitidezza, frame rate, filtri FFmpeg personalizzati;
   guadagno audio, normalizzazione del volume (EBU R128), riduzione a stereo, frequenza di campionamento.
   Come frameserver si possono scegliere **VapourSynth** e **AviSynth+**, con modelli pronti; è supportato anche l'audio prodotto dallo script.
4. **Uscita**: tutte le impostazioni del profilo, contenitore, modalità HDR, codifica a segmenti, opzioni immagini o demux, destinazione,
   "salva come preset" e il comando FFmpeg esatto.
5. **Anteprima**: il fotogramma finale (filtri, sottotitoli impressi, conversione HDR) in qualsiasi punto, più una clip di 6 secondi del risultato.
   Tutte le anteprime (fotogrammi, miniature, forma d'onda, rilevamento bande nere, clip) funzionano anche con sorgenti VapourSynth e AviSynth.

### Preset
Preset integrati, preset personali salvati da qualsiasi file e una **procedura guidata** che fa qualche domanda semplice
(a cosa serve, compatibilità o dimensione, velocità o compressione, sottotitoli, audio) e sceglie una GPU funzionante se chiedi velocità.

### Robustezza
- **Transport stream** (`.ts`, `.m2ts`…): finestra di analisi più ampia, timestamp rigenerati, pacchetti corrotti e flussi
  dati/teletext sconosciuti ignorati: i metadati strani delle trasmissioni non mandano più in errore FFmpeg.
- Controllo dello spazio libero e dei permessi di scrittura prima di iniziare, watchdog per i processi FFmpeg bloccati, log dei crash,
  rotazione dei log, coda che sopravvive ai riavvii, pulizia dei file temporanei.
- **Non sovrascrive mai nulla**: i nomi seguono un modello (`{name} [{label}]` di default) e si aggiunge un contatore se il nome esiste già.

### Aggiornamenti
- *Impostazioni → Aggiornamenti e componenti* controlla le **release GitHub di questo repository** per una nuova versione di KnA
  (l'app desktop la scarica e la installa; il server mostra come aggiornare il container o il servizio).
- **FFmpeg, ffprobe, dovi_tool e hdr10plus_tool** si installano o aggiornano dalle loro pagine di release ufficiali
  (build FFmpeg di BtbN su Windows/Linux, Homebrew su macOS).

### Integrazione col desktop
- **"Invia a" di Windows**: *Impostazioni → Uscita* aggiunge la voce "KnA" più una voce per ogni preset scelto (aggiunge i file con
  quel preset e avvia la coda). Più file inviati insieme finiscono nella stessa finestra.
- **Linux**: "Apri con", script di Nautilus e menu di servizio di Dolphin con le stesse voci.
- Riga di comando: `KnA [--preset "Nome"] [--start] file-o-cartelle…`

## Avvio rapido

### Docker (server senza interfaccia, es. Proxmox + iGPU)

```bash
git clone https://github.com/KonjikiNoYami1991/TnA.git && cd TnA
# modifica docker-compose.yml: cartella dei video, password facoltativa
RENDER_GID=$(getent group render | cut -d: -f3) docker compose up -d --build
```

Poi apri `http://<host>:8585`. Vedi **[docs/PROXMOX.it.md](docs/PROXMOX.it.md)** per il passthrough della GPU in un LXC,
l'installazione nativa senza Docker (`deploy/install-linux.sh`) e tutte le variabili d'ambiente.

### Desktop

```bash
dotnet run --project src/KnA.App -c Release
```

Su Windows e Linux, *Impostazioni → Aggiornamenti e componenti* installa FFmpeg (build statica di BtbN, GPL: libass, x264, x265,
SVT-AV1, NVENC, QSV, AMF, VA-API). Su macOS esegui `brew install ffmpeg`.

### Edizione web sul tuo PC

```bash
dotnet run --project src/KnA.Server -c Release -- --open
```

### VapourSynth e AviSynth (facoltativi)

- **VapourSynth**: installalo in modo che `vspipe` sia nel PATH (oppure imposta `KNA_VSPIPE`), insieme ai plugin sorgente usati
  dai tuoi script (L-SMASH Works, BestSource o FFMS2). I file `.vpy` si possono aggiungere direttamente alla coda.
- **AviSynth+**: installa AviSynth+ e usa un FFmpeg compilato con `--enable-avisynth`; gli script `.avs` vengono letti da FFmpeg.
  KnA mostra un messaggio chiaro se l'FFmpeg in uso non riesce a leggerli.

Con un frameserver la decodifica avviene nello script, quindi senza decodifica hardware; l'encoder può comunque essere quello della GPU.

## Compilazione e test

Servono l'[SDK .NET 10](https://dotnet.microsoft.com/download) e, per i test di integrazione, FFmpeg nel PATH.

```bash
dotnet build KnA.slnx
dotnet test KnA.slnx
```

I test creano media di prova con FFmpeg (video anamorfico, audio 5.1, sottotitoli ASS/SRT, font, una clip HDR10, un transport stream)
ed eseguono lavori reali: hardsub, softsub AV1 10 bit, remux, keyframe, tracce esterne con ritardo, taglio dopo l'importazione, demux, immagini,
HDR10, codifica a segmenti con ripresa, sorgenti VapourSynth (se `vspipe` è disponibile), anteprime, più il rendering headless delle finestre desktop.

Build autonome:

```bash
dotnet publish src/KnA.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/KnA.App -c Release -r osx-arm64 --self-contained
dotnet publish src/KnA.Server -c Release -r linux-x64 --self-contained
```

Il push di un tag `v*` avvia `.github/workflows/release.yml`, che compila per tutte le piattaforme e prepara una release **in bozza**
con i pacchetti cercati dall'aggiornamento interno (`KnA-desktop-<rid>.zip`, `KnA-server-<rid>.zip`).

## Struttura del progetto

```
src/KnA.Core     motore (senza interfaccia): analisi, piano tracce, filtri, HDR, comandi, codifica a segmenti, coda, anteprime, preset, aggiornamenti
src/KnA.Server   API ASP.NET Core + interfaccia web (wwwroot), punto d'ingresso Docker
src/KnA.App      app desktop Avalonia
tests/           test xUnit unitari/di integrazione + test headless dell'interfaccia
deploy/          installer Linux/LXC (systemd)
docs/            guida Proxmox, screenshot
legacy/          l'app originale WinForms/.NET Framework 4.8 (v0.9.9), conservata come riferimento
```

## Cosa è cambiato dalla 0.9.9

Il vecchio codice era un unico form WinForms di 3.900 righe. Tra i bug corretti nella riscrittura:

- `-crf: N` (con i due punti di troppo) generava comandi non validi, e i nomi delle qualità venivano confrontati in modo incoerente
  ("Very High" contro "Very high", "Above medium" contro "Above normal"): alcuni preset finivano senza impostazioni di qualità.
- I file di uscita venivano sovrascritti (`-y`), nonostante il README promettesse il contrario.
- La pausa della generazione dei keyframe metteva in pausa `cmd.exe` invece di FFmpeg/SCXviD.
- La lettura dell'avanzamento dalla riga di stato di FFmpeg era fragile e falliva sui valori N/A. Ora usa `-progress`.
- I percorsi con virgolette o caratteri speciali rompevano la riga di comando. Ora si usa una lista di argomenti.
- La lettura degli indici dei flussi TS andava in crash (`Binary.Substring` su una stringa vuota).
- L'altezza della finestra veniva salvata con la chiave "Heigth" e mai ripristinata.
- Spariti `Thread.Abort` (non supportato nel .NET moderno), un hook globale degli appunti e un sistema di aggiornamento basato su Dropbox.
- Niente più DLL native di mkvmerge, 7-Zip o MediaInfo: fanno tutto FFmpeg/ffprobe.

## Licenza

[GPLv3](LICENSE)
