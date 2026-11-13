# KnA su Proxmox VE (LXC o VM, senza desktop)

[English](PROXMOX.md) · **Italiano**

L'edizione web di KnA non ha bisogno di un desktop: l'encoder gira sul server e lo usi da qualsiasi
browser (PC, Mac, telefono) all'indirizzo `http://<ip-del-container>:8585`.

Ci sono due modi per installarla:

| | Docker (dentro un LXC o una VM) | Servizio nativo (LXC o VM) |
|---|---|---|
| Installazione | `docker compose up -d` | `sudo ./deploy/install-linux.sh` |
| Aggiornamenti | ricostruisci l'immagine | `git pull` + rilancia lo script |
| GPU | `/dev/dri` (o `--gpus all`) nel file compose | viene usato direttamente il `/dev/dri` del container |

## 1. Crea il container

Un **LXC Ubuntu 24.04 non privilegiato** è la scelta più semplice: .NET 10 arriva dai repository di Ubuntu.

- CPU: tutti i core che puoi concedere (x265 e SVT-AV1 li usano tutti).
- RAM: 2–4 GB.
- Disco: 8 GB più lo spazio per i file temporanei.
- Per usare Docker dentro l'LXC, attiva *Options → Features → Nesting*.

## 2. Passa la GPU al container (codifica hardware)

Sul Minisforum UM890 Pro, l'iGPU Radeon 780M (RDNA 3) codifica H.264, HEVC e AV1
tramite **VA-API**. Su Linux AMF non viene usato.

1. Sull'**host Proxmox**, controlla che il render node esista: `ls -l /dev/dri` deve elencare `renderD128`.
2. Trova l'id del gruppo `render` **dentro il container**:
   ```bash
   pct exec <CTID> -- getent group render
   ```
3. Aggiungi il dispositivo, dalla GUI (*Resources → Add → Device Passthrough*) oppure da riga di comando:
   ```bash
   pct set <CTID> -dev0 /dev/dri/renderD128,gid=<gid-render-dentro-il-CT>
   ```
4. Riavvia il container: le voci dei dispositivi vengono applicate all'avvio.

> Non combinare una voce `devN:` con un `lxc.mount.entry` manuale per lo stesso dispositivo:
> Proxmox fallisce con "mknod … File exists".

Verifica che funzioni dentro il container:

```bash
apt install -y vainfo
vainfo --display drm --device /dev/dri/renderD128 | grep -i enc
```

Cerca `VAEntrypointEncSlice` su `VAProfileAV1Profile0`, `VAProfileHEVCMain10` e `VAProfileH264High`.

Le **GPU NVIDIA** in un LXC richiedono la stessa versione del driver sull'host e nel container,
più i dispositivi `nvidia*` passati al container. Una VM con passthrough PCIe è più semplice.

## 3a. Installazione nativa (senza Docker)

```bash
apt install -y git
git clone https://github.com/KonjikiNoYami1991/TnA.git && cd TnA
KNA_MEDIA_ROOTS="/mnt/anime;/mnt/nas" ./deploy/install-linux.sh
```

Lo script installa FFmpeg, i driver VA-API/QSV e .NET 10, compila KnA in `/opt/kna`
e crea l'unità systemd `kna.service`, che gira come utente `kna` (membro dei gruppi `render` e `video`).

## 3b. Docker

```bash
git clone https://github.com/KonjikiNoYami1991/TnA.git && cd TnA
# modifica docker-compose.yml: la cartella dei video, RENDER_GID, KNA_PASSWORD facoltativa
RENDER_GID=$(getent group render | cut -d: -f3) docker compose up -d --build
```

## 4. Cartelle dei video (NAS)

Monta le condivisioni del NAS nel container, per esempio quelle dell'UGREEN DXP4800 Pro o del Synology DS420+.
Puoi fare un bind mount dall'host (`pct set <CTID> -mp0 /mnt/pve/nas-anime,mp=/mnt/anime`)
oppure usare NFS/SMB dentro un container privilegiato o una VM. Poi elencale in `KNA_MEDIA_ROOTS`, separate da `;`.
Il browser vede solo i file dentro queste radici.

L'utente `kna` (o il container) deve avere accesso in **scrittura**: di default i file finali vengono scritti accanto alle sorgenti.

## 5. Primo avvio

Al primo avvio KnA codifica una breve clip di prova con ogni encoder presente in FFmpeg.
La scheda *Hardware* mostra poi cosa funziona davvero su questa macchina.
Usa **Rileva di nuovo** dopo aver cambiato GPU o driver.

## Variabili d'ambiente

| Variabile | Predefinito | Significato |
|---|---|---|
| `KNA_URLS` | `http://0.0.0.0:8585` | Indirizzo di ascolto |
| `KNA_MEDIA_ROOTS` | `/media` (Docker), tutto il file system (nativo) | Cartelle in lettura/scrittura che si possono sfogliare, separate da `;` |
| `KNA_READONLY_ROOTS` | – | Radici in più che si possono sfogliare, scaricare e codificare ma mai scrivere |
| `KNA_UPLOAD_DIR` | `/media/uploads` (Docker), cartella dati | Destinazione predefinita degli upload dal browser |
| `KNA_MAX_UPLOAD_GB` | nessun limite | Upload più grande accettato |
| `KNA_FDKAAC` | auto | Percorso dell'encoder `fdkaac` |
| `KNA_VULKAN_DEVICE` | auto | GPU Vulkan per libplacebo / Anime4K (indice o parte del nome; `llvmpipe` = CPU) |
| `KNA_AI_SCRIPT` | incluso | Percorso di `kna_upscale.py` (upscaler neurale) |
| `KNA_DATA_DIR` | `/config` (Docker), `/var/lib/kna` | Impostazioni, log, file temporanei |
| `KNA_PASSWORD` | – | Attiva l'autenticazione HTTP Basic (qualsiasi nome utente) |
| `KNA_ALLOW_ADVANCED` | `false` | Permette di modificare dal browser il percorso di FFmpeg e gli argomenti extra |
| `KNA_ALLOW_POWER_ACTIONS` | `false` | Permette "spegni / sospendi a fine coda" |
| `KNA_FFMPEG` | auto | Percorso di un ffmpeg specifico (es. jellyfin-ffmpeg) |
| `KNA_LOCK_COMPONENTS` | `false` | Vieta di installare/aggiornare FFmpeg e gli strumenti HDR dal browser |
| `KNA_LANG` | auto | Forza la lingua (`it` o `en`); altrimenti decide il browser, oppure *Impostazioni → Generale* |
| `KNA_VSPIPE` | auto | Percorso di `vspipe` (VapourSynth) |

## VapourSynth e AviSynth sul server

- **VapourSynth**: installalo nel container/VM (per esempio `pip install vapoursynth` seguito da `vapoursynth config`, più i plugin sorgente usati dai tuoi script,
  come L-SMASH Works o BestSource) e assicurati che `vspipe` sia nel PATH dell'utente `kna`, oppure imposta `KNA_VSPIPE`.
- **AviSynth+** su Linux richiede un FFmpeg compilato con `--enable-avisynth` e la libreria AviSynth+ installata.
  Il pacchetto FFmpeg di Ubuntu e l'immagine Docker non lo includono; KnA lo segnala chiaramente quando un file `.avs` non è leggibile.
