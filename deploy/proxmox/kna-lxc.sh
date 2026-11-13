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

# Language: asked once in the wizard (unless KNA_INSTALL_LANG is set), preselected from the system locale.
if ((INTERACTIVE)) && [[ -z ${KNA_INSTALL_LANG:-} ]]; then
  lang=$(wt --notags --default-item "$( ((IT)) && echo it || echo en)" --ok-button "OK" --cancel-button "Cancel / Annulla" \
    --menu "Language / Lingua" 10 50 2 it "Italiano" en "English") || { echo "Cancelled / Annullato" >&2; exit 1; }
  [[ $lang == it ]] && IT=1 || IT=0
  export KNA_INSTALL_LANG=$lang
fi
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


# Free space of a storage, in GiB (empty when unknown).
storage_free() { pvesm status -storage "$1" 2>/dev/null | awk 'NR==2 && $6 ~ /^[0-9]+$/ {printf "%d", $6/1048576}'; }
# Filesystem type under a host path (nfs4, cifs, zfs, ext4…).
fs_of() { findmnt -no FSTYPE -T "$1" 2>/dev/null | head -1; }
# An id is free when no container or VM of the cluster uses it (/etc/pve/nodes covers every node).
id_free() {
  compgen -G "/etc/pve/nodes/*/lxc/$1.conf" >/dev/null && return 1
  compgen -G "/etc/pve/nodes/*/qemu-server/$1.conf" >/dev/null && return 1
  return 0
}
gpu_label() { local i; for i in "${!GPU_NODES[@]}"; do [[ ${GPU_NODES[$i]} == "$1" ]] && { echo "${GPU_LABELS[$i]}"; return; }; done; echo "$1"; }
cut_to() { local s=$2; ((${#s} > $1)) && s="${s:0:$(($1 - 1))}…"; printf '%s' "$s"; }

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
CT_FOLDERS="${KNA_CT_FOLDERS-}"
CT_IDMAP="${KNA_CT_IDMAP:-}"
# Upload folder: explicit (KNA_UPLOAD_DIR, empty = KnA's data folder on the container disk) or derived.
UPLOAD_SET=0; [[ -n ${KNA_UPLOAD_DIR+x} ]] && UPLOAD_SET=1
CT_UPLOAD="${KNA_UPLOAD_DIR:-}"
KNA_PORT="${KNA_PORT:-8585}"
KNA_COMPONENTS="${KNA_COMPONENTS-anime4k fsrcnnx dovi_tool hdr10plus_tool}"
[[ $CT_GPU == auto ]] && CT_GPU="${GPU_NODES[*]:-}"
[[ $CT_GPU == none ]] && CT_GPU=""
((DEV_PASSTHROUGH)) || CT_GPU=""

# Bind mounts as parallel arrays: host path, container path, read-only flag (1/0).
B_HOST=() B_CT=() B_RO=()
IFS=';' read -ra _bl <<< "${KNA_CT_BINDS:-}"
for b in "${_bl[@]}"; do
  [[ -z $b ]] && continue
  IFS=':' read -r hp cp ro <<< "$b"
  B_HOST+=("$hp"); B_CT+=("$cp"); [[ $ro == ro ]] && B_RO+=(1) || B_RO+=(0)
done

# Folders KnA will see, derived from the bind mounts and the folders inside the container.
derive() {
  local i
  MEDIA_ROOTS="" RO_ROOTS=""
  for i in "${!B_HOST[@]}"; do
    if ((B_RO[i])); then RO_ROOTS+="${RO_ROOTS:+;}${B_CT[$i]}"; else MEDIA_ROOTS+="${MEDIA_ROOTS:+;}${B_CT[$i]}"; fi
  done
  [[ -n $CT_FOLDERS ]] && MEDIA_ROOTS+="${MEDIA_ROOTS:+;}$CT_FOLDERS"
  [[ -z $MEDIA_ROOTS && ${#B_HOST[@]} -eq 0 ]] && MEDIA_ROOTS="/srv/kna/videos"
  ((UPLOAD_SET)) || CT_UPLOAD="${MEDIA_ROOTS:+${MEDIA_ROOTS%%;*}/uploads}"
  RW_BINDS=0; for i in "${!B_RO[@]}"; do ((B_RO[i])) || RW_BINDS=$((RW_BINDS + 1)); done
}

# Blocking problems, one per line (empty = all good).
problems() {
  local i v
  [[ $CT_ID =~ ^[0-9]+$ ]] && ((CT_ID >= 100)) || echo "$(t "Container ID must be a number ≥ 100" "L'ID del container deve essere un numero ≥ 100"): $CT_ID"
  [[ $CT_ID =~ ^[0-9]+$ ]] && ! id_free "$CT_ID" && echo "$(t "ID $CT_ID is already used by a container or VM" "L'ID $CT_ID è già usato da un container o una VM")"
  [[ $CT_HOSTNAME =~ ^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$ ]] || echo "Hostname: $CT_HOSTNAME"
  for v in CT_CORES CT_RAM CT_SWAP CT_DISK; do [[ ${!v} =~ ^[0-9]+$ ]] || echo "$v: ${!v}"; done
  [[ $CT_CORES =~ ^[0-9]+$ ]] && ((CT_CORES < 1 || CT_CORES > HOST_CORES)) && echo "$(t "Cores: 1 to $HOST_CORES" "Core: da 1 a $HOST_CORES")"
  [[ $CT_RAM =~ ^[0-9]+$ ]] && ((CT_RAM < 1024)) && echo "$(t "RAM: at least 1024 MB" "RAM: almeno 1024 MB")"
  [[ $CT_DISK =~ ^[0-9]+$ ]] && ((CT_DISK < 8)) && echo "$(t "Disk: at least 8 GB" "Disco: almeno 8 GB")"
  [[ " ${ROOT_STORAGES[*]} " == *" $CT_STORAGE "* ]] || echo "$(t "Storage not usable for container disks" "Storage non utilizzabile per dischi di container"): $CT_STORAGE"
  [[ " ${TMPL_STORAGES[*]} " == *" $CT_TSTORAGE "* ]] || echo "$(t "Storage not usable for templates" "Storage non utilizzabile per template"): $CT_TSTORAGE"
  [[ $CT_IP == dhcp || $CT_IP =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}/[0-9]{1,2}$ ]] || echo "IP: $CT_IP ($(t "'dhcp' or CIDR, e.g. 192.0.2.10/24" "'dhcp' o CIDR, es. 192.0.2.10/24"))"
  [[ -z $CT_GW || $CT_GW =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || echo "Gateway: $CT_GW"
  [[ -z $CT_VLAN || $CT_VLAN =~ ^[0-9]+$ ]] || echo "VLAN: $CT_VLAN"
  for v in $CT_GPU; do [[ -e /dev/dri/$v ]] || echo "$(t "GPU not found" "GPU non trovata"): /dev/dri/$v"; done
  for i in "${!B_HOST[@]}"; do
    [[ -d ${B_HOST[$i]} ]] || echo "$(t "Host folder not found" "Cartella dell'host non trovata"): ${B_HOST[$i]}"
    [[ ${B_CT[$i]} == /* ]] || echo "$(t "Container path must be absolute" "Il percorso nel container deve essere assoluto"): ${B_CT[$i]}"
  done
  ((${#B_HOST[@]} <= 10)) || echo "$(t "At most 10 bind mounts" "Al massimo 10 bind mount")"
  [[ $KNA_PORT =~ ^[0-9]+$ ]] && ((KNA_PORT >= 1 && KNA_PORT <= 65535)) || echo "$(t "Port" "Porta"): $KNA_PORT"
  [[ ${KNA_PASSWORD:-} != *"'"* ]] || echo "KNA_PASSWORD: $(t "no single quotes" "niente apici singoli")"
  if [[ -n $CT_IDMAP ]]; then
    if [[ $CT_IDMAP =~ ^([0-9]+):([0-9]+)$ ]]; then
      ((BASH_REMATCH[1] > 0 && BASH_REMATCH[1] < 65536 && BASH_REMATCH[2] > 0 && BASH_REMATCH[2] < 65536)) \
        || echo "$(t "User mapping: ids between 1 and 65535" "Mappatura utenti: id tra 1 e 65535")"
    else echo "$(t "User mapping: uid:gid" "Mappatura utenti: uid:gid")"; fi
  fi
  return 0
}

# Non-blocking remarks shown before creating.
remarks() {
  local free
  free=$(storage_free "$CT_STORAGE")
  [[ -n $free && $CT_DISK =~ ^[0-9]+$ ]] && ((CT_DISK > free)) \
    && echo "• $(t "$CT_STORAGE has only $free GB free (disk: $CT_DISK GB)." "$CT_STORAGE ha solo $free GB liberi (disco: $CT_DISK GB).")"
  ((CT_UNPRIV && RW_BINDS)) && [[ -z $CT_IDMAP ]] \
    && echo "• $(t "No user mapping: on host folders KnA writes as host uid 100000+N. Fine if they already allow it (e.g. NFS with all_squash or 777)." \
                   "Nessuna mappatura: nelle cartelle dell'host KnA scrive come uid 100000+N dell'host. Va bene se lo permettono già (es. NFS con all_squash o 777).")"
  [[ -z $MEDIA_ROOTS ]] && echo "• $(t "Every video folder is read-only: KnA needs another place for its outputs." "Tutte le cartelle video sono in sola lettura: a KnA serve un altro posto per gli output.")"
  return 0
}

describe_os() { case "$CT_OS" in debian-12) echo "Debian 12" ;; debian-13) echo "Debian 13" ;; ubuntu-24.04) echo "Ubuntu 24.04" ;; *) echo "$CT_OS" ;; esac; }
describe_priv() { ((CT_UNPRIV)) && t unprivileged "non privilegiato" || t privileged privilegiato; }
describe_disk() { local f; f=$(storage_free "$CT_STORAGE"); echo "$CT_DISK GB $(t on su) $CT_STORAGE${f:+ ($f GB $(t free liberi))}"; }
describe_net() { echo "$CT_BRIDGE, $( [[ $CT_IP == dhcp ]] && echo DHCP || echo "$CT_IP${CT_GW:+ gw $CT_GW}")${CT_VLAN:+, VLAN $CT_VLAN}"; }
describe_gpu() {
  local g out=""
  [[ -z $CT_GPU ]] && { t "none (CPU encoding)" "nessuna (codifica su CPU)"; return; }
  for g in $CT_GPU; do out+="${out:+, }$g $(gpu_label "$g")"; done; echo "$out"
}
describe_bind() {
  local fs; fs=$(fs_of "${B_HOST[$1]}")
  echo "${B_HOST[$1]} → ${B_CT[$1]} ($( ((B_RO[$1])) && t "read-only" "sola lettura" || t "read/write" "lettura/scrittura")${fs:+, $fs})"
}
describe_binds() {
  case ${#B_HOST[@]} in
    0) t "none" "nessuna" ;;
    1) describe_bind 0 ;;
    *) echo "${#B_HOST[@]}: $(describe_bind 0), …" ;;
  esac
}
describe_idmap() {
  if ((!CT_UNPRIV)); then t "not needed (privileged)" "non serve (privilegiato)"
  elif [[ -n $CT_IDMAP ]]; then echo "kna → $(t "host" "host") $CT_IDMAP"
  elif ((!RW_BINDS)); then t "not needed (no writable host folder)" "non serve (nessuna cartella host scrivibile)"
  else t "none: kna = host uid 100000+N" "nessuna: kna = uid 100000+N dell'host"; fi
}
describe_upload() { [[ -n $CT_UPLOAD ]] && echo "$CT_UPLOAD" || t "KnA data folder (container disk)" "cartella dati di KnA (disco del container)"; }
describe_kna() {
  echo "$(t port porta) $KNA_PORT, password $([[ -n ${KNA_PASSWORD:-} ]] && t yes sì || t no no), $(t components componenti): ${KNA_COMPONENTS:-$(t none nessuno)}"
}

summary() {
  local i
  S() { printf '  %-15s %s\n' "$1:" "$2"; }
  S "ID / hostname" "$CT_ID / $CT_HOSTNAME   ($(describe_os), $(describe_priv))"
  S "$(t Resources Risorse)" "$CT_CORES core, $CT_RAM MB RAM, $CT_SWAP MB swap"
  S "$(t Disk Disco)" "$(describe_disk)"
  S "$(t Network Rete)" "$(describe_net)"
  S "GPU" "$(describe_gpu)"
  if ((${#B_HOST[@]})); then for i in "${!B_HOST[@]}"; do S "$(t "Host folder" "Cartella host")" "$(describe_bind "$i")"; done
  else S "$(t "Host folders" "Cartelle host")" "$(t none nessuna)"; fi
  S "$(t "Video folders" "Cartelle video")" "${MEDIA_ROOTS:-–}${RO_ROOTS:+  ·  ro: $RO_ROOTS}"
  S "Upload" "$(describe_upload)"
  S "$(t "User mapping" "Mappatura")" "$(describe_idmap)"
  S "KnA" "$(describe_kna)"
}

# ---------------------------------------------------------------- wizard: one review screen, every value visible
ask() { local v; v=$(wt --inputbox "$1" "${3:-9}" 78 "$2") || return 1; printf '%s' "$v"; }
oops() { wt --msgbox "$1" 10 78; }

edit_id() {
  local v
  while v=$(ask "$(t "Container ID (free ones only; the next free id is $NEXT_ID):" "ID del container (solo liberi; il prossimo libero è $NEXT_ID):")" "$CT_ID"); do
    if [[ $v =~ ^[0-9]+$ ]] && ((v >= 100)) && id_free "$v"; then CT_ID=$v; break; fi
    oops "$(t "$v is not a valid free id (it must be ≥ 100 and unused by containers and VMs)." "$v non è un id libero valido (deve essere ≥ 100 e non usato da container o VM).")"
  done
  while v=$(ask "Hostname:" "$CT_HOSTNAME" 8); do
    [[ $v =~ ^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$ ]] && { CT_HOSTNAME=$v; break; }
    oops "$(t "Letters, digits and '-' only." "Solo lettere, cifre e '-'.")"
  done
  return 0
}

edit_os() {
  local v
  v=$(wt --default-item "$CT_OS" --menu "$(t "Operating system of the container:" "Sistema operativo del container:")" 12 60 3 \
    debian-12 "Debian 12 (bookworm)" debian-13 "Debian 13 (trixie)" ubuntu-24.04 "Ubuntu 24.04 LTS") || return 0
  CT_OS=$v
  if wt --yes-button "$(t Unprivileged "Non privilegiato")" --no-button "$(t Privileged Privilegiato)" \
       $( ((CT_UNPRIV)) || echo --defaultno ) --yesno "$(t "Unprivileged (recommended): safer; GPU and host folders work, writable host folders may need the user mapping.

Privileged: root inside is root on the host. Simpler with host folders, less isolated." \
"Non privilegiato (consigliato): più sicuro; GPU e cartelle dell'host funzionano, quelle scrivibili possono richiedere la mappatura utenti.

Privilegiato: root nel container è root sull'host. Più semplice con le cartelle dell'host, meno isolato.")" 13 78; then
    CT_UNPRIV=1
  else
    CT_UNPRIV=0; CT_IDMAP=""
  fi
}

edit_res() {
  local v
  while v=$(ask "$(t "CPU cores (this host has $HOST_CORES; encoders use all you give):" "Core CPU (questo host ne ha $HOST_CORES; gli encoder usano tutti quelli che dai):")" "$CT_CORES"); do
    [[ $v =~ ^[0-9]+$ ]] && ((v >= 1 && v <= HOST_CORES)) && { CT_CORES=$v; break; }; oops "1 – $HOST_CORES"
  done
  while v=$(ask "RAM (MB, ≥ 1024):" "$CT_RAM" 8); do [[ $v =~ ^[0-9]+$ ]] && ((v >= 1024)) && { CT_RAM=$v; break; }; oops "≥ 1024"; done
  while v=$(ask "Swap (MB):" "$CT_SWAP" 8); do [[ $v =~ ^[0-9]+$ ]] && { CT_SWAP=$v; break; }; oops "MB"; done
  return 0
}

edit_disk() {
  local v s items=()
  for s in "${ROOT_STORAGES[@]}"; do items+=("$s" "$(pvesm status -storage "$s" 2>/dev/null | awk 'NR==2 {print $2}')  $(storage_free "$s") GB $(t free liberi)"); done
  v=$(wt --default-item "$CT_STORAGE" --menu "$(t "Storage for the container disk:" "Storage per il disco del container:")" 16 70 8 "${items[@]}") || return 0
  CT_STORAGE=$v
  while v=$(ask "$(t "Root disk size in GB (temporary files and AI models live here, ≥ 8):" "Dimensione del disco root in GB (qui vanno file temporanei e modelli AI, ≥ 8):")" "$CT_DISK"); do
    [[ $v =~ ^[0-9]+$ ]] && ((v >= 8)) && { CT_DISK=$v; break; }; oops "≥ 8"
  done
  return 0
}

edit_tmpl() {
  local v s items=()
  for s in "${TMPL_STORAGES[@]}"; do items+=("$s" "$(storage_free "$s") GB $(t free liberi)"); done
  v=$(wt --default-item "$CT_TSTORAGE" --menu "$(t "Storage where the OS template is downloaded (not the container disk):" "Storage dove scaricare il template del sistema (non il disco del container):")" 16 70 8 "${items[@]}") || return 0
  CT_TSTORAGE=$v
}

edit_net() {
  local v b items=()
  for b in "${BRIDGES[@]}"; do items+=("$b" "$(ip -br -4 addr show "$b" 2>/dev/null | awk '{print $3}')"); done
  v=$(wt --default-item "$CT_BRIDGE" --menu "$(t "Network bridge:" "Bridge di rete:")" 15 60 6 "${items[@]}") || return 0
  CT_BRIDGE=$v
  while v=$(ask "$(t "IPv4: 'dhcp', or a static address with prefix (e.g. 192.0.2.10/24):" "IPv4: 'dhcp', oppure un indirizzo statico con prefisso (es. 192.0.2.10/24):")" "$CT_IP"); do
    [[ $v == dhcp || $v =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}/[0-9]{1,2}$ ]] && { CT_IP=$v; break; }; oops "dhcp | a.b.c.d/nn"
  done
  if [[ $CT_IP == dhcp ]]; then CT_GW=""; else
    while v=$(ask "Gateway:" "$CT_GW" 8); do [[ -z $v || $v =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] && { CT_GW=$v; break; }; oops "a.b.c.d"; done
  fi
  while v=$(ask "$(t "VLAN tag (empty for none):" "Tag VLAN (vuoto per nessuno):")" "$CT_VLAN" 8); do [[ -z $v || $v =~ ^[0-9]+$ ]] && { CT_VLAN=$v; break; }; oops "1 – 4094"; done
  return 0
}

edit_gpu() {
  local i items=() sel
  if ((!${#GPU_NODES[@]})); then oops "$(t "This host has no GPU render node (/dev/dri/renderD*): KnA will encode on the CPU." "Questo host non ha render node GPU (/dev/dri/renderD*): KnA codificherà su CPU.")"; return 0; fi
  if ((!DEV_PASSTHROUGH)); then oops "$(t "GPU passthrough to containers needs Proxmox VE 8.2+ (found $PVE_VER)." "Il passaggio della GPU ai container richiede Proxmox VE 8.2+ (trovato $PVE_VER).")"; return 0; fi
  for i in "${!GPU_NODES[@]}"; do items+=("${GPU_NODES[$i]}" "${GPU_LABELS[$i]}" "$([[ " $CT_GPU " == *" ${GPU_NODES[$i]} "* ]] && echo ON || echo OFF)"); done
  sel=$(wt --checklist "$(t "GPUs to pass through (space to toggle). NVIDIA also needs its driver inside the container: see the guide." \
                          "GPU da passare (spazio per selezionare). NVIDIA richiede anche il driver nel container: vedi la guida.")" 15 78 6 "${items[@]}") || return 0
  CT_GPU=$(echo "$sel" | tr -d '"')
}

# Folders already set up on this host that make sense as bind mounts, from every place that defines mounts:
# Proxmox storages (/etc/pve/storage.cfg), /etc/fstab, systemd .mount units and what is mounted right now.
# Fills MNT_LIST (paths, in that order) and MNT_TYPE / MNT_SRC / MNT_FROM (fs type, source, where it was found).
declare -A MNT_TYPE=() MNT_SRC=() MNT_FROM=()
MNT_LIST=() ZFS_SKIP=()
mnt_add() { # path fstype source origin
  local p=${1%/} f
  [[ $p == /?* ]] || return 0
  case "$p" in
    /boot|/boot/*|/proc*|/sys|/sys/*|/dev|/dev/*|/run|/run/*|/etc|/etc/*|/usr|/usr/*|/var/lib/lxcfs*|/tmp|/var/lib/vz|/var/log*) return 0 ;;
    */subvol-*|*/basevol-*) return 0 ;;
  esac
  case "$2" in
    proc|sysfs|devtmpfs|devpts|tmpfs|cgroup|cgroup2|pstore|bpf|securityfs|debugfs|tracefs|mqueue|hugetlbfs|configfs|fusectl|autofs|binfmt_misc|efivarfs|ramfs|rpc_pipefs|nsfs|fuse.lxcfs|overlay|squashfs|swap|none) return 0 ;;
  esac
  for f in "${ZFS_SKIP[@]}"; do [[ $3 == "$f" || $3 == "$f"/* ]] && return 0; done
  if [[ -z ${MNT_FROM[$p]+x} ]]; then
    MNT_LIST+=("$p"); MNT_TYPE[$p]=$2; MNT_SRC[$p]=$3; MNT_FROM[$p]=$4
  else
    [[ -z ${MNT_TYPE[$p]} || ${MNT_TYPE[$p]} == dir ]] && [[ -n $2 ]] && MNT_TYPE[$p]=$2
    [[ -z ${MNT_SRC[$p]} ]] && MNT_SRC[$p]=$3
    [[ ${MNT_FROM[$p]} == *"$4"* ]] || MNT_FROM[$p]+=", $4"
  fi
}
discover_mounts() {
  local type name path src where what fstype u
  MNT_LIST=(); MNT_TYPE=(); MNT_SRC=(); MNT_FROM=(); ZFS_SKIP=()
  # ZFS datasets holding guest disks or the host system are not video folders.
  [[ -r /etc/pve/storage.cfg ]] && mapfile -t ZFS_SKIP < <(awk '/^zfspool:/ {z=1; next} /^[a-z0-9]+:/ {z=0} z && $1=="pool" {print $2}' /etc/pve/storage.cfg)
  ZFS_SKIP+=(rpool/ROOT)
  # 1. Proxmox storages with a path (dir, nfs, cifs, cephfs, glusterfs…; network ones live in /mnt/pve/<id>).
  if [[ -r /etc/pve/storage.cfg ]]; then
    while IFS=$'\t' read -r type name path src; do
      [[ $path == - ]] && path=""; [[ $src == - ]] && src=""   # '-' = empty (read collapses empty tab fields)
      case "$type" in nfs|cifs|cephfs|glusterfs) path=${path:-/mnt/pve/$name} ;; esac
      [[ -n $path ]] || continue
      [[ $type == dir ]] && type=""
      mnt_add "$path" "$type" "$src" "Proxmox: $name"
    done < <(awk '
      function out() { if (name != "") printf "%s\t%s\t%s\t%s\n", t, name, (path != "" ? path : "-"), (ex != "" ? srv ":" ex : (shr != "" ? "//" srv "/" shr : "-")) }
      /^[a-z0-9]+:[ \t]/ { out(); t=$1; sub(/:$/, "", t); name=$2; path=""; srv=""; ex=""; shr=""; next }
      $1=="path" {path=$2} $1=="server" {srv=$2} $1=="export" {ex=$2} $1=="share" {shr=$2}
      END { out() }' /etc/pve/storage.cfg)
  fi
  # 2. /etc/fstab
  [[ -r /etc/fstab ]] && while read -r src where fstype _; do
    [[ -z $src || $src == \#* || $fstype == swap || $where == / ]] && continue
    mnt_add "$where" "$fstype" "$src" fstab
  done < /etc/fstab
  # 3. systemd mount units written by hand (Where= / What= / Type=)
  for u in /etc/systemd/system/*.mount; do
    [[ -f $u ]] || continue
    where=$(sed -n 's/^Where=//p' "$u" | head -1); what=$(sed -n 's/^What=//p' "$u" | head -1); fstype=$(sed -n 's/^Type=//p' "$u" | head -1)
    [[ -n $where ]] && mnt_add "$where" "$fstype" "$what" "systemd: $(basename "$u")"
  done
  # 4. Everything mounted now (disks, ZFS datasets, shares mounted by hand, autofs…)
  while read -r where fstype src; do
    where=$(printf '%b' "$where"); [[ $where == / ]] && continue
    if [[ -n ${MNT_FROM[$where]+x} ]]; then   # already listed above: only learn the real filesystem type
      [[ -z ${MNT_TYPE[$where]} ]] && MNT_TYPE[$where]=$fstype
      continue
    fi
    mnt_add "$where" "$fstype" "$src" "$(t "runtime only" "solo runtime")"   # not persistent: gone after a reboot
  done < <(findmnt -rn -o TARGET,FSTYPE,SOURCE 2>/dev/null)
  return 0
}
mnt_state() { # mounted | folder | missing
  if mountpoint -q "$1" 2>/dev/null; then echo mounted; elif [[ -d $1 ]]; then echo folder; else echo missing; fi
}
is_netfs() { case "$1" in nfs|nfs4|cifs|smb3|smbfs|cephfs|ceph|glusterfs|fuse.glusterfs|fuse.sshfs|9p|virtiofs) return 0 ;; *) return 1 ;; esac; }

# Choose the host folder of a bind mount: a known mount point, or a custom path. Prints the path.
pick_host_folder() {
  local items=() i p st ty label v hp
  discover_mounts
  for i in "${!MNT_LIST[@]}"; do
    p=${MNT_LIST[$i]}
    [[ " ${B_HOST[*]} " == *" $p "* ]] && continue
    st=$(mnt_state "$p"); ty=${MNT_TYPE[$p]:-$(fs_of "$p")}
    case $st in
      mounted) label=$(t mounted montato) ;;
      folder) if is_netfs "${MNT_TYPE[$p]}"; then label=$(t "NOT mounted" "NON montato"); else label=$(t folder cartella); fi ;;
      missing) label=$(t "NOT mounted" "NON montato") ;;
    esac
    items+=("$i" "$(cut_to 72 "$(printf '%-24s %-6s %-11s %s' "$(cut_to 24 "$p")" "$(cut_to 6 "${ty:-?}")" "$label" "${MNT_FROM[$p]}${MNT_SRC[$p]:+ · ${MNT_SRC[$p]}}")")")
  done
  items+=(custom "$(t "… Other path (type it)" "… Altro percorso (scrivilo tu)")")
  v=$(wt --notags --menu "$(t "Folder of THIS HOST to mount in the container. Found in Proxmox storages, /etc/fstab, systemd units and current mounts:" \
                             "Cartella di QUESTO HOST da montare nel container. Trovate negli storage Proxmox, /etc/fstab, unit systemd e mount attivi:")" 22 78 13 "${items[@]}") || return 1
  if [[ $v == custom ]]; then
    hp=$(ask "$(t "Folder on THIS HOST (absolute path):" "Cartella su QUESTO HOST (percorso assoluto):")" "/mnt/" 8) || return 1
  else
    hp=${MNT_LIST[$v]}
    if [[ $(mnt_state "$hp") != mounted ]] && is_netfs "${MNT_TYPE[$hp]}"; then
      oops "$(t "$hp (${MNT_TYPE[$hp]}) is NOT mounted right now: the container would see an empty folder or fail to start.
Mount it first (for Proxmox storages: enable it in Datacenter → Storage), then add it." \
"$hp (${MNT_TYPE[$hp]}) NON è montato adesso: il container vedrebbe una cartella vuota o non partirebbe.
Montalo prima (per gli storage Proxmox: abilitalo in Datacenter → Storage), poi aggiungilo.")"
      return 1
    fi
  fi
  printf '%s' "$hp"
}

add_bind() {
  local hp cp fs ro=0
  while :; do
    hp=$(pick_host_folder) || return 0
    hp=${hp%/}
    [[ $hp == /?* && $hp != *[:\;,]* ]] || { oops "$(t "Absolute path, not '/', without ':' ';' ','." "Percorso assoluto, non '/', senza ':' ';' ','.")"; continue; }
    if [[ ! -d $hp ]]; then
      wt --defaultno --yesno "$(t "$hp does not exist on the host.

If it should be a network share, it is NOT mounted: answer No and mount it first.
Create an empty local folder?" "$hp non esiste sull'host.

Se dovrebbe essere una condivisione di rete, NON è montata: rispondi No e montala prima.
Creare una cartella locale vuota?")" 12 78 || continue
      mkdir -p "$hp"
    fi
    break
  done
  fs=$(fs_of "$hp")
  while :; do
    cp=$(ask "$(t "Path INSIDE the container for $hp ($fs):" "Percorso DENTRO il container per $hp ($fs):")" "/media/$(basename "$hp")") || return 0
    cp=${cp%/}
    [[ $cp == /?* && $cp != *[:\;,]* ]] || { oops "$(t "Absolute path without ':' ';' ','." "Percorso assoluto senza ':' ';' ','.")"; continue; }
    [[ " ${B_CT[*]} " == *" $cp "* ]] && { oops "$(t "$cp is already used." "$cp è già usato.")"; continue; }
    break
  done
  wt --defaultno --yes-button "$(t Read-only "Sola lettura")" --no-button "$(t Read/write "Lettura/scrittura")" --yesno \
    "$(t "Read/write: KnA writes outputs next to the sources (and uploads, if this is the first folder).
Read-only: KnA only reads and encodes from it." \
"Lettura/scrittura: KnA scrive gli output accanto ai sorgenti (e gli upload, se è la prima cartella).
Sola lettura: KnA legge e codifica da lì, senza scriverci.")" 11 78 && ro=1
  B_HOST+=("$hp"); B_CT+=("$cp"); B_RO+=("$ro")
}

edit_binds() {
  local items i v
  while :; do
    items=()
    for i in "${!B_HOST[@]}"; do items+=("$i" "$(cut_to 70 "$(describe_bind "$i")")"); done
    items+=(add "$(t "+ Add a host folder" "+ Aggiungi una cartella dell'host")" done "$(t "← Back" "← Indietro")")
    v=$(wt --notags --default-item add --menu "$(t "Host folders in the container (bind mounts). Select one to remove it." \
                                                   "Cartelle dell'host nel container (bind mount). Scegline una per toglierla.")" 18 78 10 "${items[@]}") || return 0
    case $v in
      done) return 0 ;;
      add) add_bind ;;
      *) if wt --defaultno --yesno "$(t "Remove" "Rimuovere") $(describe_bind "$v")?" 9 78; then
           B_HOST=("${B_HOST[@]:0:v}" "${B_HOST[@]:v+1}"); B_CT=("${B_CT[@]:0:v}" "${B_CT[@]:v+1}"); B_RO=("${B_RO[@]:0:v}" "${B_RO[@]:v+1}")
         fi ;;
    esac
  done
}

edit_folders() {
  local v
  while v=$(ask "$(t "Video folders INSIDE the container, on its own disk (not on the host), ';'-separated. Empty for none." \
                    "Cartelle video DENTRO il container, sul suo disco (non sull'host), separate da ';'. Vuoto per nessuna.")" "$CT_FOLDERS" 10); do
    [[ -z $v || $v =~ ^/[^\;:]*(\;/[^\;:]*)*$ ]] && { CT_FOLDERS=$v; break; }; oops "$(t "Absolute paths separated by ';'." "Percorsi assoluti separati da ';'.")"
  done
  return 0
}

edit_upload() {
  local v
  while v=$(ask "$(t "Where files uploaded from the browser are saved (path INSIDE the container). Empty = KnA's data folder on the container disk.
A missing folder is created by KnA's user, so a network share keeps its own permissions." \
"Dove salvare i file caricati dal browser (percorso DENTRO il container). Vuoto = cartella dati di KnA sul disco del container.
Una cartella mancante viene creata dall'utente di KnA, quindi una condivisione di rete mantiene i suoi permessi.")" "$CT_UPLOAD" 11); do
    [[ -z $v || ($v == /?* && $v != *[\;:]*) ]] && { CT_UPLOAD=${v%/}; UPLOAD_SET=1; break; }; oops "$(t "Absolute path." "Percorso assoluto.")"
  done
  return 0
}

edit_idmap() {
  local i info="" v sugg=""
  ((CT_UNPRIV)) || { oops "$(t "Privileged container: no mapping needed." "Container privilegiato: la mappatura non serve.")"; return 0; }
  ((RW_BINDS)) || { oops "$(t "No writable host folder: no mapping needed." "Nessuna cartella dell'host scrivibile: la mappatura non serve.")"; return 0; }
  for i in "${!B_HOST[@]}"; do
    ((B_RO[i])) && continue
    info+="  ${B_HOST[$i]}  ($(fs_of "${B_HOST[$i]}"), $(t owner proprietario) $(stat -c '%u:%g' "${B_HOST[$i]}"))
"
    [[ -z $sugg ]] && sugg=$(stat -c '%u:%g' "${B_HOST[$i]}")
  done
  [[ $sugg == 0:* || $sugg == *:0 ]] && sugg=""
  while v=$(ask "$(t "Writable host folders:
$info
In an unprivileged container, KnA's user (uid N inside) is uid 100000+N on the host. The mapping makes it exactly a host user instead (one line in /etc/subuid and /etc/subgid, 'lxc.idmap' lines in the container).
NFS/SMB: the server's rules decide (all_squash, anonuid…). If the share is already writable by anyone, leave this EMPTY.

Host UID:GID for KnA's user (empty = no mapping):" \
"Cartelle dell'host scrivibili:
$info
In un container non privilegiato l'utente di KnA (uid N dentro) è l'uid 100000+N sull'host. La mappatura lo fa diventare esattamente un utente dell'host (una riga in /etc/subuid e /etc/subgid, righe 'lxc.idmap' nel container).
NFS/SMB: decidono le regole del server (all_squash, anonuid…). Se la condivisione è già scrivibile da chiunque, lascia VUOTO.

UID:GID dell'host per l'utente di KnA (vuoto = nessuna mappatura):")" "${CT_IDMAP:-$sugg}" 22); do
    if [[ -z $v ]]; then CT_IDMAP=""; break; fi
    if [[ $v =~ ^([0-9]+):([0-9]+)$ ]] && ((BASH_REMATCH[1] > 0 && BASH_REMATCH[1] < 65536 && BASH_REMATCH[2] > 0 && BASH_REMATCH[2] < 65536)); then CT_IDMAP=$v; break; fi
    oops "$(t "Two numbers between 1 and 65535 (root cannot be mapped), or empty." "Due numeri tra 1 e 65535 (root non si può mappare), oppure vuoto.")"
  done
  return 0
}

edit_kna() {
  local v p1 p2
  while v=$(ask "$(t "Web UI port:" "Porta dell'interfaccia web:")" "$KNA_PORT" 8); do
    [[ $v =~ ^[0-9]+$ ]] && ((v >= 1 && v <= 65535)) && { KNA_PORT=$v; break; }; oops "1 – 65535"
  done
  if wt $([[ -n ${KNA_PASSWORD:-} ]] || echo --defaultno) --yesno "$(t "Protect the web UI with a password?" "Proteggere l'interfaccia web con una password?")" 8 60; then
    while :; do
      p1=$(wt --passwordbox "$(t "Password (no single quotes):" "Password (senza apici singoli):")" 9 60) || break
      p2=$(wt --passwordbox "$(t "Repeat the password:" "Ripeti la password:")" 9 60) || break
      [[ -n $p1 && $p1 == "$p2" && $p1 != *"'"* ]] && { KNA_PASSWORD=$p1; break; }
      oops "$(t "Empty, different or containing a single quote." "Vuota, diversa o con un apice singolo.")"
    done
  else
    KNA_PASSWORD=""
  fi
  c() { [[ " $KNA_COMPONENTS " == *" $1 "* ]] && echo ON || echo OFF; }
  v=$(wt --checklist "$(t "Optional components:" "Componenti opzionali:")" 14 78 5 \
    anime4k "Anime4K (GPU upscaling shaders)" "$(c anime4k)" \
    fsrcnnx "FSRCNNX (GPU upscaling shader)" "$(c fsrcnnx)" \
    dovi_tool "dovi_tool (Dolby Vision)" "$(c dovi_tool)" \
    hdr10plus_tool "hdr10plus_tool (HDR10+)" "$(c hdr10plus_tool)" \
    ai-runtime "$(t "AI upscaler: Python + PyTorch (several GB)" "Upscaler AI: Python + PyTorch (diversi GB)")" "$(c ai-runtime)") || return 0
  KNA_COMPONENTS=$(echo "$v" | tr -d '"')
}

if ((INTERACTIVE)); then
  wt --msgbox "$(t "This creates ONE new LXC container and installs the KnA web edition in it (systemd service, no Docker).

Next you get a single screen with every setting and its current value. Change any line, then choose 'Create'. Nothing is created before that, and you can cancel at any moment.

On this host only the new container is added (plus, if you ask for the user mapping, two lines in /etc/subuid and /etc/subgid)." \
"Crea UN nuovo container LXC e ci installa l'edizione web di KnA (servizio systemd, niente Docker).

Poi vedrai una sola schermata con tutte le impostazioni e il loro valore attuale. Cambia le righe che vuoi, poi scegli 'Crea'. Prima di allora non viene creato nulla e puoi annullare in qualsiasi momento.

Su questo host viene aggiunto solo il nuovo container (più, se chiedi la mappatura utenti, due righe in /etc/subuid e /etc/subgid).")" 17 78 || cancelled

  last=create
  while :; do
    derive
    L() { printf '%-17s %s' "$1" "$(cut_to 54 "$2")"; }
    rows=(
      id      "$(L "ID / hostname" "$CT_ID / $CT_HOSTNAME")"
      os      "$(L "$(t System Sistema)" "$(describe_os), $(describe_priv)")"
      res     "$(L "CPU / RAM / swap" "$CT_CORES core, $CT_RAM MB, $CT_SWAP MB")"
      disk    "$(L "$(t Disk Disco)" "$(describe_disk)")"
      tmpl    "$(L "Template" "$(t "downloaded to" "scaricato in") $CT_TSTORAGE")"
      net     "$(L "$(t Network Rete)" "$(describe_net)")"
      gpu     "$(L "GPU" "$(describe_gpu)")"
      binds   "$(L "$(t "Host folders" "Cartelle host")" "$(describe_binds)")"
      folders "$(L "$(t "Folders in CT" "Cartelle nel CT")" "${CT_FOLDERS:-$( [[ $MEDIA_ROOTS == /srv/kna/videos ]] && echo "/srv/kna/videos" || t none nessuna)}")"
      upload  "$(L "Upload" "$(describe_upload)")"
      idmap   "$(L "$(t "User mapping" "Mappatura")" "$(describe_idmap)")"
      kna     "$(L "KnA" "$(describe_kna)")"
      sep     ""
      create  "    >>> $(t "Create the container" "Crea il container") <<<"
    )
    choice=$(wt --notags --default-item "$last" --ok-button "$(t Select Scegli)" --cancel-button "$(t Cancel Annulla)" \
      --menu "$(t "Review the settings. Select a line to change it, then 'Create'." "Controlla le impostazioni. Scegli una riga per cambiarla, poi 'Crea'.")" 24 78 14 "${rows[@]}") || cancelled
    last=$choice
    case $choice in
      id) edit_id ;; os) edit_os ;; res) edit_res ;; disk) edit_disk ;; tmpl) edit_tmpl ;; net) edit_net ;;
      gpu) edit_gpu ;; binds) edit_binds ;; folders) edit_folders ;; upload) edit_upload ;; idmap) edit_idmap ;; kna) edit_kna ;;
      create)
        derive
        errs=$(problems)
        if [[ -n $errs ]]; then oops "$(t "Fix these first:" "Correggi prima questi punti:")
$errs"; continue; fi
        notes=$(remarks)
        wt --yes-button "$(t Create Crea)" --no-button "$(t Back Indietro)" --yesno "$(t "Create this container?" "Creare questo container?")

$(summary)${notes:+

$notes}" 24 78 && break
        ;;
    esac
  done
fi

# ---------------------------------------------------------------- validation
derive
errs=$(problems)
[[ -z $errs ]] || die "$errs"
IDMAP_UID="" IDMAP_GID=""
if [[ -n $CT_IDMAP ]]; then
  if ((CT_UNPRIV)); then IDMAP_UID=${CT_IDMAP%%:*}; IDMAP_GID=${CT_IDMAP##*:}
  else warn "$(t "User mapping ignored: privileged container." "Mappatura utenti ignorata: container privilegiato.")"; fi
fi
MP_ARGS=()
for i in "${!B_HOST[@]}"; do MP_ARGS+=(--"mp$i" "${B_HOST[$i]},mp=${B_CT[$i]}$( ((B_RO[i])) && echo ,ro=1 || true)"); done
((INTERACTIVE)) || { notes=$(remarks); [[ -n $notes ]] && warn "$notes"; }

# ---------------------------------------------------------------- failure handling
# Once the container exists, a failure must not leave it behind silently.
CREATED=0 STAGE="" INSTALLER=""
on_exit() {
  local rc=$?
  [[ -n $INSTALLER ]] && rm -f "$INSTALLER"
  ((CREATED && rc != 0)) || return 0
  echo >&2
  warn "$(t "Failed while: $STAGE. Container $CT_ID exists but KnA is NOT fully installed." "Errore durante: $STAGE. Il container $CT_ID esiste ma KnA NON è installato completamente.")"
  warn "$(t "Retry the installer:  pct exec $CT_ID -- bash /root/kna-install.sh" "Riprova l'installer:  pct exec $CT_ID -- bash /root/kna-install.sh")"
  warn "$(t "Or remove it:         pct stop $CT_ID; pct destroy $CT_ID --purge" "Oppure eliminalo:     pct stop $CT_ID; pct destroy $CT_ID --purge")"
  if ((INTERACTIVE)) && wt --defaultno --yesno "$(t "The installation failed while: $STAGE.

Delete container $CT_ID now? (No keeps it, so you can look inside with 'pct enter $CT_ID' and retry.)" \
"L'installazione non è riuscita durante: $STAGE.

Eliminare ora il container $CT_ID? (No lo mantiene, così puoi guardarci dentro con 'pct enter $CT_ID' e riprovare.)")" 12 78; then
    pct stop "$CT_ID" >/dev/null 2>&1 || true
    pct destroy "$CT_ID" --purge && warn "$(t "Container $CT_ID deleted." "Container $CT_ID eliminato.")"
  fi
}
trap on_exit EXIT

# ---------------------------------------------------------------- template
STAGE=$(t "downloading the template" "download del template")
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
here="$(cd "$(dirname "${BASH_SOURCE[0]:-.}")" 2>/dev/null && pwd || true)"
if [[ -n $here && -f $here/../install-linux.sh && -z ${KNA_INSTALLER_URL:-} ]]; then
  cp "$here/../install-linux.sh" "$INSTALLER"
else
  curl -fsSL "${KNA_INSTALLER_URL:-https://raw.githubusercontent.com/$REPO/$BRANCH/deploy/install-linux.sh}" -o "$INSTALLER" \
    || die "$(t "Cannot download the KnA installer." "Impossibile scaricare l'installer di KnA.")"
fi

# ---------------------------------------------------------------- create
net="name=eth0,bridge=$CT_BRIDGE,ip=$CT_IP${CT_GW:+,gw=$CT_GW}${CT_VLAN:+,tag=$CT_VLAN}"
log "$(t "Creating container $CT_ID ($CT_HOSTNAME) on $CT_STORAGE…" "Creazione del container $CT_ID ($CT_HOSTNAME) su $CT_STORAGE…")"
id_free "$CT_ID" || die "$(t "ID $CT_ID was taken in the meantime." "L'ID $CT_ID è stato occupato nel frattempo.")"
pct create "$CT_ID" "$CT_TSTORAGE:vztmpl/$TEMPLATE" \
  --hostname "$CT_HOSTNAME" --cores "$CT_CORES" --memory "$CT_RAM" --swap "$CT_SWAP" \
  --rootfs "$CT_STORAGE:$CT_DISK" --net0 "$net" --unprivileged "$CT_UNPRIV" --features nesting=1 \
  --onboot 1 --timezone host --tags kna \
  --description "KnA - Konjiki no Autoencoder (web edition). https://github.com/$REPO" \
  "${MP_ARGS[@]}"
CREATED=1

if [[ -n $IDMAP_UID ]]; then
  STAGE=$(t "writing the user mapping" "scrittura della mappatura utenti")
  log "$(t "Mapping the container's kna user to host $IDMAP_UID:$IDMAP_GID…" "Mappatura dell'utente kna del container su $IDMAP_UID:$IDMAP_GID dell'host…")"
  for f in /etc/subuid /etc/subgid; do grep -q '^root:100000:65536$' "$f" 2>/dev/null || echo "root:100000:65536" >> "$f"; done
  grep -qx "root:$IDMAP_UID:1" /etc/subuid || echo "root:$IDMAP_UID:1" >> /etc/subuid
  grep -qx "root:$IDMAP_GID:1" /etc/subgid || echo "root:$IDMAP_GID:1" >> /etc/subgid
  u=$IDMAP_UID g=$IDMAP_GID
  cat >> "/etc/pve/lxc/$CT_ID.conf" <<EOC
lxc.idmap: u 0 100000 $u
lxc.idmap: u $u $u 1
lxc.idmap: u $((u + 1)) $((100000 + u + 1)) $((65535 - u))
lxc.idmap: g 0 100000 $g
lxc.idmap: g $g $g 1
lxc.idmap: g $((g + 1)) $((100000 + g + 1)) $((65535 - g))
EOC
fi

STAGE=$(t "starting the container" "avvio del container")
log "$(t "Starting the container…" "Avvio del container…")"
pct start "$CT_ID"
for _ in $(seq 1 60); do pct exec "$CT_ID" -- true 2>/dev/null && break; sleep 1; done

# GPU passthrough: device nodes owned by the container's render group.
if [[ -n $CT_GPU ]]; then
  STAGE=$(t "passing the GPU through" "passaggio della GPU")
  gids=$(pct exec "$CT_ID" -- sh -c 'getent group render >/dev/null || groupadd -r render; echo "$(getent group render | cut -d: -f3) $(getent group video | cut -d: -f3)"')
  render_gid=${gids%% *}; n=0
  for g in $CT_GPU; do pct set "$CT_ID" --"dev$n" "/dev/dri/$g,gid=$render_gid"; n=$((n + 1)); done
  log "$(t "GPU passed through: $CT_GPU (render gid $render_gid). Restarting…" "GPU passata: $CT_GPU (gid render $render_gid). Riavvio…")"
  pct reboot "$CT_ID"
  for _ in $(seq 1 60); do pct exec "$CT_ID" -- true 2>/dev/null && break; sleep 1; done
fi

STAGE=$(t "waiting for the network" "attesa della rete")
log "$(t "Waiting for the network…" "Attesa della rete…")"
for _ in $(seq 1 60); do pct exec "$CT_ID" -- getent hosts github.com >/dev/null 2>&1 && break; sleep 2; done
pct exec "$CT_ID" -- getent hosts github.com >/dev/null 2>&1 \
  || die "$(t "The container cannot resolve github.com: check its network/DNS." "Il container non risolve github.com: controlla rete/DNS.")"

# ---------------------------------------------------------------- install KnA inside
STAGE=$(t "installing KnA inside the container" "installazione di KnA nel container")
pct push "$CT_ID" "$INSTALLER" /root/kna-install.sh --perms 0755
envs=(LANG=C.UTF-8 KNA_INSTALL_LANG="$( ((IT)) && echo it || echo en)" KNA_REPO="$REPO" KNA_BRANCH="$BRANCH"
      KNA_MEDIA_ROOTS="$MEDIA_ROOTS" KNA_READONLY_ROOTS="$RO_ROOTS" KNA_PORT="$KNA_PORT" KNA_GPU=auto
      KNA_COMPONENTS="$KNA_COMPONENTS")
[[ -n ${KNA_PASSWORD:-} ]] && envs+=(KNA_PASSWORD="$KNA_PASSWORD")
[[ -n ${KNA_FFMPEG_SOURCE:-} ]] && envs+=(KNA_FFMPEG_SOURCE="$KNA_FFMPEG_SOURCE")
[[ -n ${KNA_VERSION:-} ]] && envs+=(KNA_VERSION="$KNA_VERSION")
[[ -n $IDMAP_UID ]] && envs+=(KNA_UID="$IDMAP_UID" KNA_GID="$IDMAP_GID")
[[ -n $CT_UPLOAD ]] && envs+=(KNA_UPLOAD_DIR="$CT_UPLOAD")
log "$(t "Installing KnA inside container $CT_ID…" "Installazione di KnA nel container $CT_ID…")"
pct exec "$CT_ID" -- env "${envs[@]}" bash /root/kna-install.sh --unattended
pct exec "$CT_ID" -- rm -f /root/kna-install.sh
CREATED=0   # success: nothing to clean up

ip=$(pct exec "$CT_ID" -- hostname -I 2>/dev/null | awk '{print $1}')
echo
log "$(t "KnA is ready" "KnA è pronto"):  http://${ip:-<ip>}:$KNA_PORT"
log "$(t "Container" "Container"): $CT_ID ($CT_HOSTNAME)  ·  $(t "shell" "shell"): pct enter $CT_ID"
log "$(t "Update KnA" "Aggiorna KnA"): pct exec $CT_ID -- kna-update"
log "$(t "Settings" "Impostazioni"): /etc/kna/kna.env $(t "inside the container" "nel container") (systemctl restart kna)"
if ((CT_UNPRIV && RW_BINDS)) && [[ -z $IDMAP_UID ]]; then
  warn "$(t "Writable host folders without user mapping: KnA writes there as host uid 100000 + the uid of 'kna' inside (pct exec $CT_ID -- id kna). The installer above warned if it cannot write." \
            "Cartelle host scrivibili senza mappatura: KnA ci scrive come uid 100000 + l'uid di 'kna' nel container (pct exec $CT_ID -- id kna). L'installer qui sopra ha avvisato se non può scrivere.")"
fi
