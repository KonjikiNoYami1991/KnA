# KnA su Proxmox VE e server Linux

[English](PROXMOX.md) · **Italiano**

L'edizione web di KnA funziona senza schermo: l'encoder gira sul server e lo usi da qualsiasi browser
(PC, Mac, telefono) su `http://<ip-del-server>:8585`. Si installa **in modo nativo** come servizio systemd: niente Docker.

Ci sono due script:

| Script | Dove si lancia | Cosa fa |
|---|---|---|
| [`deploy/proxmox/kna-lxc.sh`](../deploy/proxmox/kna-lxc.sh) | sull'**host Proxmox** | crea un container LXC (risorse, rete, GPU, bind mount, mappatura utenti) e ci installa KnA |
| [`deploy/install-linux.sh`](../deploy/install-linux.sh) | **dentro** un LXC, una VM o una macchina Debian/Ubuntu esistente | installa o aggiorna KnA come servizio `kna` |

Entrambi hanno un wizard quando li avvii da un terminale, e una modalità senza domande guidata da variabili d'ambiente.

> Percorsi, indirizzi e id di questa guida sono **esempi presi da un ambiente di prova**: usa i tuoi.

## 1. Un comando sull'host Proxmox

Apri la shell del nodo (*Datacenter → nodo → Shell*) e lancia:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/deploy/proxmox/kna-lxc.sh)"
```

Il wizard chiede:

1. **Impostazioni predefinite o avanzate**: id del container, hostname, sistema (Debian 12 / 13, Ubuntu 24.04), core, RAM,
   swap, disco, storage, bridge, DHCP o indirizzo statico, VLAN, container non privilegiato o privilegiato.
   I valori predefiniti sono metà dei core dell'host (tutti sugli host piccoli), 4 GB di RAM, 16 GB di disco, DHCP e container non privilegiato.
2. **GPU da passare**: ogni render node dell'host appare con produttore e modello (Proxmox VE 8.2+).
3. **Bind mount**, quanti ne vuoi: una cartella dell'host → una cartella nel container, in lettura/scrittura o sola lettura.
   Le condivisioni di rete (NFS, SMB) vanno prima montate sull'host (*Datacenter → Storage*, oppure `/etc/fstab`).
4. **Cartelle video aggiuntive dentro il container** (sul suo disco), se ne vuoi.
5. **Mappatura utenti** (container non privilegiati con bind mount scrivibili, vedi [§3](#3-cartelle-dei-video-e-permessi)).
6. **Impostazioni di KnA**: porta, password, componenti opzionali.

Alla fine mostra l'indirizzo dell'interfaccia web.

Requisiti: Proxmox VE 8 o successivo (8.2+ per passare la GPU ai container), accesso a internet per il template e la release di KnA.

### Senza domande

Tutte le risposte del wizard si possono dare come variabili. Valori di esempio presi da un ambiente di prova:

```bash
KNA_CT_ID=210 KNA_CT_CORES=6 KNA_CT_RAM=6144 KNA_CT_DISK=24 \
KNA_CT_GPU=auto \
KNA_CT_BINDS="/mnt/pve/media-share/videos:/media/videos;/mnt/pve/media-share/archive:/media/archive:ro" \
KNA_CT_IDMAP=1000:1000 KNA_PASSWORD='cambiami' \
bash kna-lxc.sh --unattended
```

| Variabile | Predefinito | Significato |
|---|---|---|
| `KNA_CT_ID` | primo id libero | Id del container |
| `KNA_CT_HOSTNAME` | `kna` | Hostname |
| `KNA_CT_OS` | `debian-12` | `debian-12`, `debian-13` o `ubuntu-24.04` |
| `KNA_CT_CORES` · `KNA_CT_RAM` · `KNA_CT_SWAP` · `KNA_CT_DISK` | metà dei core · 4096 · 512 · 16 | Core, MB, MB, GB |
| `KNA_CT_STORAGE` · `KNA_CT_TEMPLATE_STORAGE` | il primo attivo | Storage per il disco e per il template |
| `KNA_CT_BRIDGE` · `KNA_CT_IP` · `KNA_CT_GW` · `KNA_CT_VLAN` | primo bridge · `dhcp` | Rete (`KNA_CT_IP` in forma CIDR se statico) |
| `KNA_CT_UNPRIVILEGED` | `1` | `0` per un container privilegiato |
| `KNA_CT_GPU` | `auto` | `auto` (tutti i render node), `none`, oppure nomi come `renderD128` |
| `KNA_CT_BINDS` | – | Voci `host:container[:ro]` separate da `;` |
| `KNA_CT_FOLDERS` | `/srv/kna/videos` se non ci sono bind mount | Cartelle video create dentro il container |
| `KNA_CT_IDMAP` | – | `uid:gid` dell'utente dell'host su cui mappare l'utente `kna` del container |

`KNA_PORT`, `KNA_PASSWORD`, `KNA_FFMPEG_SOURCE`, `KNA_COMPONENTS` e `KNA_VERSION` vengono passate all'installer interno.

## 2. Installare in un container, una VM o un server esistente

Su Debian 12+ / Ubuntu 22.04+ (amd64 o arm64), da root:

```bash
curl -fsSL https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/deploy/install-linux.sh | bash
```

Il wizard chiede:
- la porta;
- le **cartelle dei video** (lettura/scrittura, separate da `;`, create se mancano);
- le eventuali **cartelle in sola lettura** e la cartella per gli upload;
- una password;
- i **driver GPU** (i produttori trovati sono già selezionati);
- la build di FFmpeg e i componenti opzionali;
- se vuoi, uid e gid dell'utente del servizio.

Cosa installa:

- il pacchetto server autonomo dell'ultima release di KnA in `/opt/kna` (niente .NET SDK, nessuna compilazione);
- font, Vulkan (Mesa, compreso il ripiego su CPU) e `vainfo`;
- Intel: il media driver (abilitando *non-free* su Debian / *multiverse* su Ubuntu se serve) e oneVPL; AMD: Mesa VA-API;
- FFmpeg: di base KnA scarica da sé l'ultima build statica, poi la aggiorna da *Impostazioni → Aggiornamenti e componenti*.
  L'alternativa è il pacchetto della distribuzione, usato anche come ripiego se il download non riesce;
- il servizio systemd `kna`, che gira come utente `kna` (nei gruppi `render` e `video`);
- il comando `kna-update`.

Senza domande, con valori di esempio presi da un ambiente di prova:

```bash
KNA_MEDIA_ROOTS="/srv/videos;/mnt/share/videos" KNA_READONLY_ROOTS="/mnt/share/archive" \
KNA_PASSWORD='cambiami' KNA_COMPONENTS="anime4k dovi_tool hdr10plus_tool" \
bash install-linux.sh --unattended
```

| Variabile | Predefinito | Significato |
|---|---|---|
| `KNA_VERSION` | `latest` | Release da installare (`3.0.0`…) |
| `KNA_PORT` | `8585` | Porta dell'interfaccia web |
| `KNA_MEDIA_ROOTS` | `/srv/kna/videos` | Cartelle video in lettura/scrittura, separate da `;` |
| `KNA_READONLY_ROOTS` | – | Cartelle in sola lettura |
| `KNA_UPLOAD_DIR` | cartella dati | Destinazione degli upload dal browser |
| `KNA_PASSWORD` | – | Password dell'interfaccia web (senza apici singoli) |
| `KNA_GPU` | `auto` | `auto`, `none`, oppure uno o più tra `intel amd nvidia` |
| `KNA_FFMPEG_SOURCE` | `btbn` | `btbn` (build statica più recente) o `distro` |
| `KNA_COMPONENTS` | `anime4k fsrcnnx dovi_tool hdr10plus_tool` | Anche `ai-runtime` (Python + PyTorch, diversi GB); vuoto per nessuno |
| `KNA_UID` · `KNA_GID` | id di sistema | Id dell'utente `kna` |

### Aggiornamento, impostazioni, rimozione

| | |
|---|---|
| Aggiornare | `kna-update` (nel container; dall'host: `pct exec <id> -- kna-update`) |
| Impostazioni | `/etc/kna/kna.env`, poi `systemctl restart kna` (variabili [sotto](#variabili-dambiente)) |
| Log | `journalctl -u kna -f` |
| Riconfigurare | rilancia l'installer (il wizard riparte dalle impostazioni attuali) |
| Rimuovere | `bash /usr/local/sbin/kna-install --uninstall` (`--purge` cancella anche impostazioni e dati, mai i video) |

## 3. Cartelle dei video e permessi

KnA mostra solo le cartelle elencate in `KNA_MEDIA_ROOTS` (lettura/scrittura) e `KNA_READONLY_ROOTS`.
Di base gli output vengono scritti **accanto ai sorgenti**, quindi l'utente `kna` deve poter scrivere nelle cartelle in lettura/scrittura.
Le cartelle create dall'installer appartengono a `kna`. Per quelle esistenti, l'installer avvisa se `kna` non può scriverci.

**Bind mount in un container non privilegiato.** Dentro un container non privilegiato l'utente *N* è l'utente *100000 + N* sull'host.
Una cartella che appartiene a un utente dell'host (per esempio uid 1000) quindi non è scrivibile da `kna`. Hai tre possibilità:

- **Mappatura utenti** (lo script per Proxmox la fa se rispondi sì, oppure con `KNA_CT_IDMAP`). L'utente `kna` del container
  diventa esattamente quell'utente dell'host: una riga in `/etc/subuid` e `/etc/subgid` (`root:<uid>:1`), più queste righe in
  `/etc/pve/lxc/<id>.conf` (qui per uid/gid 1000):
  ```
  lxc.idmap: u 0 100000 1000
  lxc.idmap: u 1000 1000 1
  lxc.idmap: u 1001 101001 64535
  lxc.idmap: g 0 100000 1000
  lxc.idmap: g 1000 1000 1
  lxc.idmap: g 1001 101001 64535
  ```
- Dai la cartella dell'host all'id mappato: `chown -R 100000+<uid di kna> …` sull'host. Il container mostra `id kna`.
- Usa un container privilegiato. È più semplice, ma meno isolato.

I bind mount in sola lettura (`ro=1`) non hanno bisogno della mappatura: la lettura funziona se i file sono leggibili da tutti.

Per aggiungere un bind mount in seguito, dall'host:

```bash
pct set <id> -mp2 /cartella/host,mp=/media/nuova-cartella
```

Poi aggiungi `/media/nuova-cartella` a `KNA_MEDIA_ROOTS` in `/etc/kna/kna.env` e riavvia il servizio.

## 4. GPU

Al primo avvio KnA prova ogni encoder della sua build di FFmpeg. La scheda *Hardware* mostra poi cosa funziona davvero.
Usa **Rileva di nuovo** dopo aver cambiato GPU o driver.

| GPU | Nel container | Note |
|---|---|---|
| Intel (iGPU / Arc) | Quick Sync (oneVPL) e VA-API | l'installer aggiunge il media driver |
| AMD (APU / Radeon) | VA-API (Mesa), Vulkan | AMF non si usa su Linux |
| NVIDIA | NVENC | il driver user-space va installato nel container **alla stessa versione dell'host**, più i device `/dev/nvidia*`; spesso è più semplice una VM con passthrough PCIe |

Per passare un render node a mano (Proxmox VE 8.2+), dall'host:

```bash
ls -l /dev/dri                                    # render node dell'host
pct exec <id> -- getent group render              # gid di 'render' nel container
pct set <id> -dev0 /dev/dri/renderD128,gid=<quel-gid>
pct reboot <id>
```

> Non combinare una voce `devN:` con un `lxc.mount.entry` manuale per lo stesso device:
> Proxmox fallisce con "mknod … File exists".

Per verificarlo nel container: `vainfo --display drm --device /dev/dri/renderD128`.
Le righe `VAEntrypointEncSlice` elencano i codec che la GPU sa codificare.

## Variabili d'ambiente

Il servizio le legge da `/etc/kna/kna.env`.

| Variabile | Predefinito | Significato |
|---|---|---|
| `KNA_URLS` | `http://0.0.0.0:8585` | Indirizzo di ascolto |
| `KNA_MEDIA_ROOTS` | scelta dell'installer | Cartelle sfogliabili in lettura/scrittura, separate da `;` |
| `KNA_READONLY_ROOTS` | – | Radici in più sfogliabili, scaricabili e codificabili, ma mai scritte |
| `KNA_UPLOAD_DIR` | cartella dati | Destinazione predefinita degli upload dal browser |
| `KNA_MAX_UPLOAD_GB` | nessun limite | Upload più grande accettato |
| `KNA_DATA_DIR` | `/var/lib/kna` | Impostazioni, log, file temporanei, componenti scaricati |
| `KNA_PASSWORD` | – | Attiva l'autenticazione HTTP Basic (qualsiasi nome utente) |
| `KNA_FFMPEG` | auto | Percorso di un ffmpeg specifico |
| `KNA_FDKAAC` | auto | Percorso dell'encoder `fdkaac` (incluso nel pacchetto) |
| `KNA_VULKAN_DEVICE` | auto | GPU Vulkan per libplacebo / Anime4K (indice o parte del nome; `llvmpipe` = CPU) |
| `KNA_AI_SCRIPT` | incluso | Percorso di `kna_upscale.py` (upscaler neurale) |
| `KNA_VSPIPE` | auto | Percorso di `vspipe` (VapourSynth) |
| `KNA_ALLOW_ADVANCED` | `false` | Permette di modificare dal browser il percorso di FFmpeg e gli argomenti extra |
| `KNA_ALLOW_POWER_ACTIONS` | `false` | Permette "spegni / sospendi alla fine" |
| `KNA_LOCK_COMPONENTS` | `false` | Impedisce di installare/aggiornare FFmpeg e gli altri componenti dal browser |
| `KNA_LANG` | auto | Forza la lingua (`it` o `en`); altrimenti decide il browser, o *Impostazioni → Generale* |

## VapourSynth e AviSynth sul server

- **VapourSynth**: installalo nel container/VM (per esempio `pip install vapoursynth` seguito da `vapoursynth config`, più i plugin sorgente usati
  dai tuoi script, come L-SMASH Works o BestSource), assicurati che `vspipe` sia nel PATH dell'utente `kna` oppure imposta `KNA_VSPIPE`.
- **AviSynth+** su Linux richiede un FFmpeg compilato con `--enable-avisynth` e la libreria AviSynth+ installata.
  Le build di FFmpeg più comuni non lo includono; KnA lo segnala chiaramente quando non riesce a leggere un file `.avs`.
