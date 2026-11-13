#!/usr/bin/env bash
# KnA on Proxmox VE: creates a Debian/Ubuntu LXC container and installs the KnA web edition inside it
# (native systemd service, no Docker). Run it on the Proxmox host, as root, from the node's shell:
#
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/deploy/proxmox/kna-lxc.sh)"
#
# The wizard asks for the container resources, the GPUs to pass through, any number of bind mounts
# (host folder → container folder, read/write or read-only) and an optional user mapping so that KnA can
# write into bind-mounted folders from an unprivileged container. Then it runs deploy/install-linux.sh
# inside the container.
#
# Unattended (every variable optional; KNA_PORT, KNA_PASSWORD, KNA_FFMPEG_SOURCE, KNA_COMPONENTS are passed
# to the installer inside the container):
#   KNA_CT_ID  KNA_CT_HOSTNAME  KNA_CT_OS (debian-12|debian-13|ubuntu-24.04)  KNA_CT_CORES  KNA_CT_RAM (MB)
#   KNA_CT_SWAP (MB)  KNA_CT_DISK (GB)  KNA_CT_STORAGE  KNA_CT_TEMPLATE_STORAGE  KNA_CT_BRIDGE
#   KNA_CT_IP (dhcp | 192.0.2.10/24)  KNA_CT_GW  KNA_CT_VLAN  KNA_CT_UNPRIVILEGED (1|0)
#   KNA_CT_GPU      auto | none | "renderD128 renderD129"
#   KNA_CT_BINDS    "host-path:container-path[:ro];…"
#   KNA_CT_FOLDERS  extra video folders created inside the container, ';'-separated
#   KNA_CT_IDMAP    "uid:gid" host ids the container's kna user is mapped to (unprivileged only)
#   bash kna-lxc.sh --unattended
set -euo pipefail

REPO="${KNA_REPO:-KonjikiNoYami1991/KnA}"
BRANCH="${KNA_BRANCH:-master}"
TITLE="KnA - Konjiki no Autoencoder · Proxmox LXC"

case "${KNA_INSTALL_LANG:-${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}}" in it*) IT=1 ;; *) IT=0 ;; esac
t() { if ((IT)); then printf '%s' "$2"; else printf '%s' "$1"; fi; }
log() { printf '\033[1;35m[KnA]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[KnA] %s\033[0m\n' "$*" >&2; }
die() { printf '\033[1;31m[KnA] %s\033[0m\n' "$*" >&2; exit 1; }

UNATTENDED=0
for a in "$@"; do
  case "$a" in
    --unattended) UNATTENDED=1 ;;
    -h|--help) sed -n '2,22p' "${BASH_SOURCE[0]:-/dev/null}" 2>/dev/null | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "$(t "Unknown option: $a" "Opzione sconosciuta: $a")" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "$(t "Run as root on the Proxmox host." "Esegui come root sull'host Proxmox.")"
command -v pct >/dev/null && command -v pveam >/dev/null && command -v pvesm >/dev/null \
  || die "$(t "This script runs on a Proxmox VE host (pct, pveam, pvesm not found)." "Questo script va eseguito su un host Proxmox VE (pct, pveam, pvesm non trovati).")"
PVE_VER=$(pveversion 2>/dev/null | grep -oE 'pve-manager/[0-9]+\.[0-9]+' | cut -d/ -f2 || echo 0.0)
PVE_MAJOR=${PVE_VER%%.*}; PVE_MINOR=${PVE_VER#*.}
((PVE_MAJOR >= 8)) || die "$(t "Proxmox VE 8 or newer is required (found $PVE_VER)." "Serve Proxmox VE 8 o successivo (trovato $PVE_VER).")"
DEV_PASSTHROUGH=0; ((PVE_MAJOR > 8 || PVE_MINOR >= 2)) && DEV_PASSTHROUGH=1

INTERACTIVE=0
if ((!UNATTENDED)) && (exec </dev/tty) 2>/dev/null; then
  command -v whiptail >/dev/null || die "whiptail missing (apt install whiptail)"
  INTERACTIVE=1
fi
wt() { whiptail --title "$TITLE" "$@" 3>&1 1>&2 2>&3 </dev/tty; }
cancelled() { die "$(t "Cancelled: nothing was created." "Annullato: non è stato creato nulla.")"; }

# ---------------------------------------------------------------- host facts
HOST_CORES=$(nproc)
NEXT_ID=$(pvesh get /cluster/nextid 2>/dev/null || echo 100)
mapfile -t ROOT_STORAGES < <(pvesm status -content rootdir 2>/dev/null | awk 'NR>1 && $3=="active" {print $1}')
mapfile -t TMPL_STORAGES < <(pvesm status -content vztmpl 2>/dev/null | awk 'NR>1 && $3=="active" {print $1}')
((${#ROOT_STORAGES[@]})) || die "$(t "No active storage accepts container disks (content 'rootdir')." "Nessuno storage attivo accetta dischi di container (contenuto 'rootdir').")"
((${#TMPL_STORAGES[@]})) || die "$(t "No active storage accepts templates (content 'vztmpl')." "Nessuno storage attivo accetta template (contenuto 'vztmpl').")"
mapfile -t BRIDGES < <(ip -o link show type bridge 2>/dev/null | awk -F': ' '{print $2}' | cut -d@ -f1)
((${#BRIDGES[@]})) || BRIDGES=(vmbr0)

# Render nodes of the host, with vendor and model.
GPU_NODES=() GPU_LABELS=()
for d in /sys/class/drm/renderD*; do
  [[ -e $d ]] || continue
  node=$(basename "$d"); vendor=$(cat "$d/device/vendor" 2>/dev/null || echo "?")
  case "$vendor" in 0x8086) v=Intel ;; 0x1002) v=AMD ;; 0x10de) v=NVIDIA ;; *) v="$vendor" ;; esac
  slot=$(basename "$(readlink -f "$d/device")")
  model=$(lspci -s "$slot" 2>/dev/null | cut -d: -f3- | sed 's/^ *//' | cut -c1-48)
  GPU_NODES+=("$node"); GPU_LABELS+=("$v ${model:-$slot}")
done

# ---------------------------------------------------------------- defaults
CT_ID="${KNA_CT_ID:-$NEXT_ID}"
CT_HOSTNAME="${KNA_CT_HOSTNAME:-kna}"
CT_OS="${KNA_CT_OS:-debian-12}"
CT_CORES="${KNA_CT_CORES:-$(( HOST_CORES > 4 ? HOST_CORES / 2 : HOST_CORES ))}"
CT_RAM="${KNA_CT_RAM:-4096}"
CT_SWAP="${KNA_CT_SWAP:-512}"
CT_DISK="${KNA_CT_DISK:-16}"
CT_STORAGE="${KNA_CT_STORAGE:-${ROOT_STORAGES[0]}}"
CT_TSTORAGE="${KNA_CT_TEMPLATE_STORAGE:-${TMPL_STORAGES[0]}}"
CT_BRIDGE="${KNA_CT_BRIDGE:-${BRIDGES[0]}}"
CT_IP="${KNA_CT_IP:-dhcp}"
CT_GW="${KNA_CT_GW:-}"
CT_VLAN="${KNA_CT_VLAN:-}"
CT_UNPRIV="${KNA_CT_UNPRIVILEGED:-1}"
CT_GPU="${KNA_CT_GPU:-auto}"
CT_BINDS="${KNA_CT_BINDS:-}"
CT_FOLDERS="${KNA_CT_FOLDERS:-}"
CT_IDMAP="${KNA_CT_IDMAP:-}"
[[ $CT_GPU == auto ]] && CT_GPU="${GPU_NODES[*]:-}"
[[ $CT_GPU == none ]] && CT_GPU=""

menu_items() { local x; for x in "$@"; do printf '%s\n%s\n' "$x" ""; done; }

# ---------------------------------------------------------------- wizard
if ((INTERACTIVE)); then
  wt --msgbox "$(t "This creates a new LXC container and installs the KnA web edition in it (systemd service, no Docker).

You choose: resources, network, GPUs to pass through, and the folders with your videos (bind mounts from this host and/or folders inside the container).

Nothing on this host is changed except the new container and, only if you ask for it, a user mapping in /etc/subuid and /etc/subgid." \
"Crea un nuovo container LXC e ci installa l'edizione web di KnA (servizio systemd, niente Docker).

Scegli tu: risorse, rete, GPU da passare e le cartelle dei video (bind mount da questo host e/o cartelle dentro il container).

Su questo host non cambia nulla tranne il nuovo container e, solo se lo chiedi, una mappatura utenti in /etc/subuid e /etc/subgid.")" 16 78 || cancelled

  mode=$(wt --menu "$(t "Settings" "Impostazioni")" 12 78 2 \
    default "$(t "Default: $CT_CORES cores, ${CT_RAM} MB RAM, ${CT_DISK} GB, DHCP on $CT_BRIDGE" "Predefinite: $CT_CORES core, ${CT_RAM} MB RAM, ${CT_DISK} GB, DHCP su $CT_BRIDGE")" \
    advanced "$(t "Advanced: choose every value" "Avanzate: scegli ogni valore")") || cancelled

  if [[ $mode == advanced ]]; then
    CT_ID=$(wt --inputbox "$(t "Container ID:" "ID del container:")" 8 50 "$CT_ID") || cancelled
    CT_HOSTNAME=$(wt --inputbox "Hostname:" 8 50 "$CT_HOSTNAME") || cancelled
    CT_OS=$(wt --default-item "$CT_OS" --menu "$(t "Operating system:" "Sistema operativo:")" 12 60 3 \
      debian-12 "Debian 12 (bookworm)" debian-13 "Debian 13 (trixie)" ubuntu-24.04 "Ubuntu 24.04 LTS") || cancelled
    CT_CORES=$(wt --inputbox "$(t "CPU cores (this host has $HOST_CORES; encoders use all you give):" "Core CPU (questo host ne ha $HOST_CORES; gli encoder usano tutti quelli che dai):")" 9 70 "$CT_CORES") || cancelled
    CT_RAM=$(wt --inputbox "RAM (MB):" 8 50 "$CT_RAM") || cancelled
    CT_SWAP=$(wt --inputbox "Swap (MB):" 8 50 "$CT_SWAP") || cancelled
    CT_DISK=$(wt --inputbox "$(t "Root disk (GB; temporary files and AI models live here):" "Disco root (GB; qui vanno file temporanei e modelli AI):")" 9 70 "$CT_DISK") || cancelled
    # shellcheck disable=SC2046
    CT_STORAGE=$(wt --default-item "$CT_STORAGE" --noitem --menu "$(t "Storage for the container disk:" "Storage per il disco del container:")" 15 60 6 $(menu_items "${ROOT_STORAGES[@]}")) || cancelled
    # shellcheck disable=SC2046
    CT_TSTORAGE=$(wt --default-item "$CT_TSTORAGE" --noitem --menu "$(t "Storage for the OS template:" "Storage per il template del sistema:")" 15 60 6 $(menu_items "${TMPL_STORAGES[@]}")) || cancelled
    # shellcheck disable=SC2046
    CT_BRIDGE=$(wt --default-item "$CT_BRIDGE" --noitem --menu "$(t "Network bridge:" "Bridge di rete:")" 15 60 6 $(menu_items "${BRIDGES[@]}")) || cancelled
    CT_IP=$(wt --inputbox "$(t "IPv4: 'dhcp' or an address with prefix (CIDR):" "IPv4: 'dhcp' oppure un indirizzo con prefisso (CIDR):")" 9 70 "$CT_IP") || cancelled
    [[ $CT_IP != dhcp ]] && { CT_GW=$(wt --inputbox "Gateway:" 8 50 "$CT_GW") || cancelled; }
    CT_VLAN=$(wt --inputbox "$(t "VLAN tag (empty for none):" "Tag VLAN (vuoto per nessuno):")" 8 50 "$CT_VLAN") || cancelled
    if wt --yesno "$(t "Unprivileged container? (recommended; GPU passthrough and bind mounts work, see the user mapping step)" \
                       "Container non privilegiato? (consigliato; GPU e bind mount funzionano, vedi il passo della mappatura utenti)")" 9 72; then
      CT_UNPRIV=1; else CT_UNPRIV=0; fi
  fi

  # GPUs
  if ((${#GPU_NODES[@]})); then
    if ((!DEV_PASSTHROUGH)); then
      wt --msgbox "$(t "GPU passthrough to containers needs Proxmox VE 8.2 or newer (found $PVE_VER). The container will encode on the CPU." \
                       "Il passaggio della GPU ai container richiede Proxmox VE 8.2 o successivo (trovato $PVE_VER). Il container codificherà su CPU.")" 9 72
      CT_GPU=""
    else
      items=(); for i in "${!GPU_NODES[@]}"; do items+=("${GPU_NODES[$i]}" "${GPU_LABELS[$i]}" ON); done
      sel=$(wt --checklist "$(t "GPUs to pass through for hardware encoding (VA-API / Quick Sync / Vulkan).
NVIDIA cards also need their driver inside the container: see the guide." \
"GPU da passare per la codifica hardware (VA-API / Quick Sync / Vulkan).
Le schede NVIDIA richiedono anche il driver dentro il container: vedi la guida.")" 16 78 6 "${items[@]}") || cancelled
      CT_GPU=$(echo "$sel" | tr -d '"')
    fi
  else
    CT_GPU=""
  fi

  # Bind mounts
  CT_BINDS=""
  while wt --yesno "$(t "Add a folder of this host to the container (bind mount)?
Current: ${CT_BINDS:-none}

Network shares (NFS/SMB) must be mounted on the host first (e.g. Datacenter → Storage, or /etc/fstab)." \
"Aggiungere al container una cartella di questo host (bind mount)?
Attuali: ${CT_BINDS:-nessuno}

Le condivisioni di rete (NFS/SMB) vanno prima montate sull'host (es. Datacenter → Storage, o /etc/fstab).")" 13 78; do
    hp=$(wt --inputbox "$(t "Folder on THIS HOST (absolute path):" "Cartella su QUESTO HOST (percorso assoluto):")" 9 72 "/") || continue
    [[ $hp == /* && $hp != */ && $hp != *[:\;]* ]] || { wt --msgbox "$(t "Absolute path without ':' or ';', not '/'." "Percorso assoluto senza ':' o ';', non '/'.")" 8 60; continue; }
    if [[ ! -d $hp ]]; then
      wt --yesno "$(t "$hp does not exist on the host. Create it?" "$hp non esiste sull'host. Crearla?")" 8 70 || continue
      mkdir -p "$hp"
    fi
    cp=$(wt --inputbox "$(t "Path INSIDE the container:" "Percorso DENTRO il container:")" 9 72 "/media/$(basename "$hp")") || continue
    [[ $cp == /* && $cp != */ && $cp != *[:\;]* ]] || { wt --msgbox "$(t "Absolute path without ':' or ';'." "Percorso assoluto senza ':' o ';'.")" 8 60; continue; }
    ro=""
    wt --defaultno --yesno "$(t "Read-only? (KnA reads and encodes from it but never writes there)" "Sola lettura? (KnA legge e codifica da lì ma non ci scrive mai)")" 9 70 && ro=":ro"
    CT_BINDS+="${CT_BINDS:+;}$hp:$cp$ro"
  done

  CT_FOLDERS=$(wt --inputbox "$(t "Extra video folders INSIDE the container (stored on its disk), ';'-separated. Empty for none." \
                                 "Cartelle video aggiuntive DENTRO il container (sul suo disco), separate da ';'. Vuoto per nessuna.")" 10 78 \
               "${CT_FOLDERS:-$([[ -z $CT_BINDS ]] && echo /srv/kna/videos)}") || cancelled

  # User mapping for writable bind mounts in an unprivileged container
  if ((CT_UNPRIV)) && [[ -n $CT_BINDS ]]; then
    first_rw=""; IFS=';' read -ra bl <<< "$CT_BINDS"
    for b in "${bl[@]}"; do [[ $b != *:ro ]] && { first_rw=${b%%:*}; break; }; done
    if [[ -n $first_rw ]]; then
      owner=$(stat -c '%u:%g' "$first_rw")
      [[ $owner == 0:* || $owner == *:0 ]] && owner=""
      if wt --yesno "$(t "In an unprivileged container, user N inside is user 100000+N on the host, so KnA usually cannot write into bind-mounted folders.

Map the container's 'kna' user to a user of THIS HOST (the owner of your video files)? This adds one line to /etc/subuid and /etc/subgid and 'lxc.idmap' lines to the container." \
"In un container non privilegiato l'utente N interno è l'utente 100000+N sull'host, quindi di solito KnA non può scrivere nelle cartelle in bind mount.

Mappare l'utente 'kna' del container su un utente di QUESTO HOST (il proprietario dei file video)? Aggiunge una riga a /etc/subuid e /etc/subgid e righe 'lxc.idmap' al container.")" 15 78; then
        while :; do
          CT_IDMAP=$(wt --inputbox "$(t "Host UID:GID (owner of $first_rw is $(stat -c '%u:%g' "$first_rw")):" "UID:GID dell'host (il proprietario di $first_rw è $(stat -c '%u:%g' "$first_rw")):")" 9 72 "${owner:-1000:1000}") || cancelled
          [[ $CT_IDMAP =~ ^([0-9]+):([0-9]+)$ ]] && ((BASH_REMATCH[1] > 0 && BASH_REMATCH[1] < 65536 && BASH_REMATCH[2] > 0 && BASH_REMATCH[2] < 65536)) && break
          wt --msgbox "$(t "Two numbers between 1 and 65535 (root cannot be mapped)." "Due numeri tra 1 e 65535 (root non si può mappare).")" 8 60
        done
      fi
    fi
  fi

  # KnA settings
  KNA_PORT=$(wt --inputbox "$(t "Web UI port:" "Porta dell'interfaccia web:")" 8 50 "${KNA_PORT:-8585}") || cancelled
  if wt --yesno "$(t "Protect the web UI with a password?" "Proteggere l'interfaccia web con una password?")" 8 60; then
    while :; do
      p1=$(wt --passwordbox "$(t "Password (no single quotes):" "Password (senza apici singoli):")" 9 60) || cancelled
      p2=$(wt --passwordbox "$(t "Repeat the password:" "Ripeti la password:")" 9 60) || cancelled
      [[ -n $p1 && $p1 == "$p2" && $p1 != *"'"* ]] && { KNA_PASSWORD=$p1; break; }
      wt --msgbox "$(t "Empty, different or containing a single quote." "Vuota, diversa o con un apice singolo.")" 8 60
    done
  fi
  c() { [[ " ${KNA_COMPONENTS-anime4k fsrcnnx dovi_tool hdr10plus_tool} " == *" $1 "* ]] && echo ON || echo OFF; }
  KNA_COMPONENTS=$(wt --checklist "$(t "Optional components:" "Componenti opzionali:")" 14 78 5 \
    anime4k "Anime4K (GPU upscaling shaders)" "$(c anime4k)" \
    fsrcnnx "FSRCNNX (GPU upscaling shader)" "$(c fsrcnnx)" \
    dovi_tool "dovi_tool (Dolby Vision)" "$(c dovi_tool)" \
    hdr10plus_tool "hdr10plus_tool (HDR10+)" "$(c hdr10plus_tool)" \
    ai-runtime "$(t "AI upscaler: Python + PyTorch (several GB)" "Upscaler AI: Python + PyTorch (diversi GB)")" "$(c ai-runtime)" | tr -d '"') || cancelled
fi

# ---------------------------------------------------------------- validation
[[ $CT_ID =~ ^[0-9]+$ ]] || die "CT ID: $CT_ID"
pct status "$CT_ID" >/dev/null 2>&1 && die "$(t "Container $CT_ID already exists." "Il container $CT_ID esiste già.")"
for v in CT_CORES CT_RAM CT_SWAP CT_DISK; do [[ ${!v} =~ ^[0-9]+$ ]] || die "$v: ${!v}"; done
[[ $CT_IP == dhcp || $CT_IP =~ ^[0-9.]+/[0-9]+$ ]] || die "IP: $CT_IP"
[[ -z $CT_VLAN || $CT_VLAN =~ ^[0-9]+$ ]] || die "VLAN: $CT_VLAN"
[[ ${KNA_PASSWORD:-} != *"'"* ]] || die "KNA_PASSWORD: no single quotes"
IDMAP_UID="" IDMAP_GID=""
if [[ -n $CT_IDMAP ]]; then
  [[ $CT_IDMAP =~ ^([0-9]+):([0-9]+)$ ]] || die "KNA_CT_IDMAP: uid:gid"
  IDMAP_UID=${BASH_REMATCH[1]}; IDMAP_GID=${BASH_REMATCH[2]}
  ((CT_UNPRIV)) || { warn "$(t "User mapping ignored: privileged container." "Mappatura utenti ignorata: container privilegiato.")"; IDMAP_UID="" IDMAP_GID=""; }
  ((IDMAP_UID > 0 && IDMAP_UID < 65536 && IDMAP_GID > 0 && IDMAP_GID < 65536)) || die "KNA_CT_IDMAP: 1..65535"
fi

# Container paths of the video folders, for the installer inside.
MEDIA_ROOTS="" RO_ROOTS="" MP_ARGS=() i=0
IFS=';' read -ra bl <<< "$CT_BINDS"
for b in "${bl[@]}"; do
  [[ -z $b ]] && continue
  IFS=':' read -r hp cp ro <<< "$b"
  [[ -d $hp ]] || die "$(t "Host folder not found: $hp" "Cartella dell'host non trovata: $hp")"
  [[ $cp == /* ]] || die "$(t "Container path must be absolute: $cp" "Il percorso nel container deve essere assoluto: $cp")"
  MP_ARGS+=(--"mp$i" "$hp,mp=$cp${ro:+,ro=1}"); i=$((i + 1))
  if [[ $ro == ro ]]; then RO_ROOTS+="${RO_ROOTS:+;}$cp"; else MEDIA_ROOTS+="${MEDIA_ROOTS:+;}$cp"; fi
done
[[ -n $CT_FOLDERS ]] && MEDIA_ROOTS+="${MEDIA_ROOTS:+;}$CT_FOLDERS"
[[ -z $MEDIA_ROOTS ]] && MEDIA_ROOTS="/srv/kna/videos"

for g in $CT_GPU; do [[ -e /dev/dri/$g ]] || die "$(t "GPU not found: /dev/dri/$g" "GPU non trovata: /dev/dri/$g")"; done
[[ -n $CT_GPU ]] && ((!DEV_PASSTHROUGH)) && { warn "$(t "GPU passthrough needs Proxmox VE 8.2+: skipped." "Il passaggio GPU richiede Proxmox VE 8.2+: saltato.")"; CT_GPU=""; }

if ((INTERACTIVE)); then
  wt --yesno "$(t "Create the container?" "Creare il container?")

  ID / hostname:   $CT_ID / $CT_HOSTNAME   ($CT_OS, $( ((CT_UNPRIV)) && t unprivileged "non privilegiato" || t privileged privilegiato))
  $(t "Resources" "Risorse"):       $CT_CORES core, $CT_RAM MB RAM, $CT_SWAP MB swap, $CT_DISK GB on $CT_STORAGE
  $(t "Network" "Rete"):         $CT_BRIDGE, $CT_IP${CT_GW:+ gw $CT_GW}${CT_VLAN:+ vlan $CT_VLAN}
  GPU:             ${CT_GPU:-$(t "none (CPU encoding)" "nessuna (codifica su CPU)")}
  Bind mounts:     ${CT_BINDS:-$(t none nessuno)}
  $(t "Video folders" "Cartelle video"):   $MEDIA_ROOTS${RO_ROOTS:+  ·  ro: $RO_ROOTS}
  $(t "User mapping" "Mappatura"):       ${CT_IDMAP:-$(t none nessuna)}
  KnA:             $(t port porta) ${KNA_PORT:-8585}, password $([[ -n ${KNA_PASSWORD:-} ]] && t yes sì || t no no)" 19 78 || cancelled
fi

# ---------------------------------------------------------------- template
case "$CT_OS" in
  debian-12) pattern='^debian-12-standard_' ;;
  debian-13) pattern='^debian-13-standard_' ;;
  ubuntu-24.04) pattern='^ubuntu-24\.04-standard_' ;;
  *) die "KNA_CT_OS: $CT_OS" ;;
esac
TEMPLATE=$(pveam list "$CT_TSTORAGE" 2>/dev/null | awk 'NR>1 {sub(/.*\//, "", $1); print $1}' | grep -E "$pattern" | sort -V | tail -1 || true)
if [[ -z $TEMPLATE ]]; then
  log "$(t "Downloading the $CT_OS template…" "Download del template $CT_OS…")"
  pveam update >/dev/null || warn "pveam update failed"
  TEMPLATE=$(pveam available --section system | awk '{print $2}' | grep -E "$pattern" | sort -V | tail -1 || true)
  [[ -n $TEMPLATE ]] || die "$(t "No $CT_OS template available from pveam." "Nessun template $CT_OS disponibile da pveam.")"
  pveam download "$CT_TSTORAGE" "$TEMPLATE"
fi

# ---------------------------------------------------------------- installer for inside the container
INSTALLER=$(mktemp)
trap 'rm -f "$INSTALLER"' EXIT
here="$(cd "$(dirname "${BASH_SOURCE[0]:-.}")" 2>/dev/null && pwd || true)"
if [[ -n $here && -f $here/../install-linux.sh && -z ${KNA_INSTALLER_URL:-} ]]; then
  cp "$here/../install-linux.sh" "$INSTALLER"
else
  curl -fsSL "${KNA_INSTALLER_URL:-https://raw.githubusercontent.com/$REPO/$BRANCH/deploy/install-linux.sh}" -o "$INSTALLER" \
    || die "$(t "Cannot download the KnA installer." "Impossibile scaricare l'installer di KnA.")"
fi

# ---------------------------------------------------------------- create
net="name=eth0,bridge=$CT_BRIDGE,ip=$CT_IP${CT_GW:+,gw=$CT_GW}${CT_VLAN:+,tag=$CT_VLAN}"
log "$(t "Creating container $CT_ID…" "Creazione del container $CT_ID…")"
pct create "$CT_ID" "$CT_TSTORAGE:vztmpl/$TEMPLATE" \
  --hostname "$CT_HOSTNAME" --cores "$CT_CORES" --memory "$CT_RAM" --swap "$CT_SWAP" \
  --rootfs "$CT_STORAGE:$CT_DISK" --net0 "$net" --unprivileged "$CT_UNPRIV" --features nesting=1 \
  --onboot 1 --timezone host --tags kna \
  --description "KnA - Konjiki no Autoencoder (web edition). https://github.com/$REPO" \
  "${MP_ARGS[@]}"

if [[ -n $IDMAP_UID ]]; then
  log "$(t "Mapping the container's kna user to host $IDMAP_UID:$IDMAP_GID…" "Mappatura dell'utente kna del container su $IDMAP_UID:$IDMAP_GID dell'host…")"
  for f in /etc/subuid /etc/subgid; do grep -q '^root:100000:65536$' "$f" 2>/dev/null || echo "root:100000:65536" >> "$f"; done
  grep -qx "root:$IDMAP_UID:1" /etc/subuid || echo "root:$IDMAP_UID:1" >> /etc/subuid
  grep -qx "root:$IDMAP_GID:1" /etc/subgid || echo "root:$IDMAP_GID:1" >> /etc/subgid
  u=$IDMAP_UID g=$IDMAP_GID
  cat >> "/etc/pve/lxc/$CT_ID.conf" <<EOF
lxc.idmap: u 0 100000 $u
lxc.idmap: u $u $u 1
lxc.idmap: u $((u + 1)) $((100000 + u + 1)) $((65535 - u))
lxc.idmap: g 0 100000 $g
lxc.idmap: g $g $g 1
lxc.idmap: g $((g + 1)) $((100000 + g + 1)) $((65535 - g))
EOF
fi

log "$(t "Starting the container…" "Avvio del container…")"
pct start "$CT_ID"
for _ in $(seq 1 60); do pct exec "$CT_ID" -- true 2>/dev/null && break; sleep 1; done

# GPU passthrough: device nodes owned by the container's render/video groups.
if [[ -n $CT_GPU ]]; then
  gids=$(pct exec "$CT_ID" -- sh -c 'getent group render >/dev/null || groupadd -r render; echo "$(getent group render | cut -d: -f3) $(getent group video | cut -d: -f3)"')
  render_gid=${gids%% *}; n=0
  for g in $CT_GPU; do pct set "$CT_ID" --"dev$n" "/dev/dri/$g,gid=$render_gid"; n=$((n + 1)); done
  log "$(t "GPU passed through: $CT_GPU (render gid $render_gid). Restarting…" "GPU passata: $CT_GPU (gid render $render_gid). Riavvio…")"
  pct reboot "$CT_ID"
  for _ in $(seq 1 60); do pct exec "$CT_ID" -- true 2>/dev/null && break; sleep 1; done
fi

log "$(t "Waiting for the network…" "Attesa della rete…")"
for _ in $(seq 1 60); do pct exec "$CT_ID" -- getent hosts github.com >/dev/null 2>&1 && break; sleep 2; done
pct exec "$CT_ID" -- getent hosts github.com >/dev/null 2>&1 \
  || die "$(t "The container cannot resolve github.com: check its network/DNS, then run the installer inside it." "Il container non risolve github.com: controlla rete/DNS, poi lancia l'installer al suo interno.")"

# ---------------------------------------------------------------- install KnA inside
pct push "$CT_ID" "$INSTALLER" /root/kna-install.sh --perms 0755
envs=(LANG=C.UTF-8 KNA_INSTALL_LANG="$( ((IT)) && echo it || echo en)" KNA_REPO="$REPO" KNA_BRANCH="$BRANCH"
      KNA_MEDIA_ROOTS="$MEDIA_ROOTS" KNA_READONLY_ROOTS="$RO_ROOTS" KNA_PORT="${KNA_PORT:-8585}" KNA_GPU=auto)
[[ -n ${KNA_PASSWORD:-} ]] && envs+=(KNA_PASSWORD="$KNA_PASSWORD")
[[ -n ${KNA_FFMPEG_SOURCE:-} ]] && envs+=(KNA_FFMPEG_SOURCE="$KNA_FFMPEG_SOURCE")
[[ -n ${KNA_COMPONENTS+x} ]] && envs+=(KNA_COMPONENTS="$KNA_COMPONENTS")
[[ -n ${KNA_VERSION:-} ]] && envs+=(KNA_VERSION="$KNA_VERSION")
[[ -n $IDMAP_UID ]] && envs+=(KNA_UID="$IDMAP_UID" KNA_GID="$IDMAP_GID")
[[ -n ${MEDIA_ROOTS%%;*} ]] && envs+=(KNA_UPLOAD_DIR="${MEDIA_ROOTS%%;*}/uploads")
log "$(t "Installing KnA inside container $CT_ID…" "Installazione di KnA nel container $CT_ID…")"
pct exec "$CT_ID" -- env "${envs[@]}" bash /root/kna-install.sh --unattended
pct exec "$CT_ID" -- rm -f /root/kna-install.sh

ip=$(pct exec "$CT_ID" -- hostname -I 2>/dev/null | awk '{print $1}')
echo
log "$(t "KnA is ready" "KnA è pronto"):  http://${ip:-<ip>}:${KNA_PORT:-8585}"
log "$(t "Container" "Container"): $CT_ID ($CT_HOSTNAME)  ·  $(t "shell" "shell"): pct enter $CT_ID"
log "$(t "Update KnA" "Aggiorna KnA"): pct exec $CT_ID -- kna-update"
log "$(t "Settings" "Impostazioni"): /etc/kna/kna.env $(t "inside the container" "nel container") (systemctl restart kna)"
if [[ -n $CT_BINDS && -z $IDMAP_UID ]] && ((CT_UNPRIV)) && [[ -n ${MEDIA_ROOTS} ]]; then
  warn "$(t "Writable bind mounts in an unprivileged container: the host folders must be writable by host uid 100000 + the uid of 'kna' inside (pct exec $CT_ID -- id kna)." \
            "Bind mount scrivibili in un container non privilegiato: le cartelle dell'host devono essere scrivibili dall'uid 100000 + l'uid di 'kna' nel container (pct exec $CT_ID -- id kna).")"
fi
