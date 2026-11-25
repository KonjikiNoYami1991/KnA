# KnA con Docker

[English](DOCKER.md)

L'edizione web di KnA gira in un solo container: apri `http://<host>:8585` in qualsiasi browser. L'immagine è pronta
e viene pubblicata a ogni release per **amd64** (PC e NAS Intel/AMD) e **arm64** (Raspberry Pi 5, NAS ARM, Apple
Silicon con Docker Desktop):

```
ghcr.io/konjikinoyami1991/kna:latest      # l'ultima release
ghcr.io/konjikinoyami1991/kna:3.3.0       # una versione fissa
```

Dentro: KnA, FFmpeg (build statica di BtbN), fdkaac, NVEncC / QSVEncC (amd64), i driver VA-API / Quick Sync / Vulkan
e Python per l'upscaler AI. I dispositivi ARM a 32 bit (armv7) non sono supportati.

## Le tre cose da impostare

| Cosa | Dove | Perché |
|---|---|---|
| **La cartella dei video** | bind mount → `/media` | KnA legge lì le sorgenti e scrive i risultati |
| **PUID / PGID** | variabili d'ambiente | KnA scrive i file come questo utente: sulla condivisione restano tuoi |
| **KNA_PASSWORD** | variabili d'ambiente | password dell'amministratore `admin` creato al primo avvio; senza, il primo che apre KnA crea l'amministratore |

Come trovare PUID/PGID: `id -u` e `id -g` su Linux; su un NAS, `id <tuo utente>` in una sessione SSH (Synology:
abilita SSH in Pannello di controllo → Terminale e SNMP). Senza PUID/PGID il container gira come root (file di root).

Impostazioni, preset, coda, log e modelli AI stanno nella cartella montata su `/config`: fanne il backup come di
qualsiasi cartella.

## docker compose (qualsiasi host Linux)

```bash
mkdir -p ~/kna && cd ~/kna
curl -fsSLO https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/docker-compose.yml
nano docker-compose.yml        # cartella dei video, PUID/PGID, KNA_PASSWORD
docker compose up -d
```

Aggiornamento: `docker compose pull && docker compose up -d`. Log: `docker logs -f kna`.

## docker run

```bash
docker run -d --name kna --restart unless-stopped -p 8585:8585 \
  -e PUID=1000 -e PGID=1000 -e KNA_PASSWORD=cambiami -e TZ=Europe/Rome \
  --mount type=bind,src=/srv/kna/config,dst=/config \
  --mount type=bind,src=/mnt/video,dst=/media \
  --device /dev/dri:/dev/dri \
  ghcr.io/konjikinoyami1991/kna:latest
```

## GPU

| GPU | Cosa aggiungere | Note |
|---|---|---|
| Intel (Quick Sync), AMD (VA-API) | `--device /dev/dri:/dev/dri` (compose: `devices:`) | i gruppi dei dispositivi li aggiunge il container da solo |
| NVIDIA (NVENC, CUDA) | `--gpus all` (compose: il blocco `deploy:`) | servono il driver NVIDIA e *nvidia-container-toolkit* sull'host |
| nessuna | niente | la codifica su CPU (x264, x265, SVT-AV1) funziona ovunque |

Dopo il primo avvio la scheda **Hardware** di KnA mostra quali encoder hanno superato la codifica di prova.

## Synology (DSM 7.2, Container Manager)

1. Installa **Container Manager** dal Centro pacchetti.
2. File Station: crea `docker/kna` (per le impostazioni). I video restano dove sono, es. `/volume1/video`.
3. Container Manager → **Progetto** → **Crea**: nome `kna`, percorso `docker/kna`, origine **Crea docker-compose.yml**,
   incolla [docker-compose.yml](../docker-compose.yml) e cambia:
   - `source: ./config` resta (diventa `docker/kna/config`);
   - `source: /mnt/anime` → `/volume1/video` (la tua cartella condivisa);
   - `PUID` / `PGID`: da `id <tuo utente>` via SSH (spesso `1026` / `100`);
   - modelli Intel con Quick Sync (es. DS920+, DS420+, DS224+): lascia `devices: /dev/dri`; i modelli senza `/dev/dri`
     (AMD Ryzen, ARM Realtek) togli le righe `devices:`.
4. Avanti → Fine. Apri `http://<nas>:8585`.

Aggiornamento: Container Manager → **Immagine** → `ghcr.io/konjikinoyami1991/kna` → **Aggiorna** (compare quando c'è
un'immagine più recente), poi riavvia il progetto.

## QNAP (Container Station 3)

1. Installa **Container Station** dall'App Center.
2. Applicazioni → **Crea** → incolla [docker-compose.yml](../docker-compose.yml), cambia il percorso dei video con la
   tua condivisione (es. `/share/Multimedia`), quello delle impostazioni con es. `/share/Container/kna`, PUID/PGID
   (`id <utente>` via SSH).
3. Modelli Intel con Quick Sync: lascia `devices: /dev/dri`. I modelli ARM a 32 bit (es. TS-431P, Annapurna Alpine)
   non possono eseguire KnA: usa un'altra macchina della rete come server e il NAS solo per i file.

## UGREEN (UGOS Pro, app Docker)

1. Installa **Docker** dall'App Center.
2. Docker → **Progetto** → **Crea**: nome `kna`, una cartella per il progetto (es. `docker/kna`), incolla
   [docker-compose.yml](../docker-compose.yml).
3. Cambia il percorso dei video con la cartella condivisa come la mostra UGOS (es. `/volume1/Video`), PUID/PGID
   (`id <utente>` via SSH). Modelli Intel: lascia `devices: /dev/dri` per Quick Sync.

## Unraid

Docker → **Add Container**: repository `ghcr.io/konjikinoyami1991/kna:latest`, porta `8585`, percorsi
`/config` → `/mnt/user/appdata/kna` e `/media` → `/mnt/user/videos`, variabili `PUID=99`, `PGID=100`,
`KNA_PASSWORD`; GPU Intel/AMD: parametro extra `--device=/dev/dri`; NVIDIA: il plugin *Nvidia Driver* e
`--runtime=nvidia` con `NVIDIA_VISIBLE_DEVICES=all`.

## Portainer

Stacks → **Add stack** → Web editor: incolla [docker-compose.yml](../docker-compose.yml), imposta percorsi e variabili,
**Deploy**. Aggiornamento: lo stack → **Pull and redeploy**.

## Proxmox

Meglio l'installer LXC (nativo, senza Docker, passaggio della GPU gestito): [PROXMOX.it.md](PROXMOX.it.md). Docker
dentro un LXC funziona ma richiede il nesting e il passaggio dei dispositivi fatto due volte.

## Problemi comuni

| Sintomo | Soluzione |
|---|---|
| "Permission denied" scrivendo i risultati | PUID/PGID devono essere un utente che può scrivere sulla condivisione; verifica con `ls -ln` sull'host |
| Nessun encoder hardware nella scheda Hardware | `/dev/dri` non passato (Intel/AMD), oppure manca `--gpus all` / nvidia-container-toolkit (NVIDIA) |
| La pagina chiede una password che non conosci | utente `admin` e la `KNA_PASSWORD` del primo avvio; persa: `docker exec -it kna dotnet /app/kna-server.dll --reset-password admin` |
| `exec format error` | host ARM a 32 bit: non supportato |
| Tutto il resto | `docker logs kna` e il log del lavoro (📄 nella coda) |

Tutte le variabili `KNA_*`: [PROXMOX.it.md → Variabili d'ambiente](PROXMOX.it.md#variabili-dambiente).
