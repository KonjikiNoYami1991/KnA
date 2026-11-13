#!/usr/bin/env bash
# KnA web edition: native installer for Debian, Ubuntu, Fedora, Arch Linux and their derivatives
# (Proxmox LXC, VM or bare metal). No Docker.
#
#   Install (wizard):   curl -fsSL https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/deploy/install-linux.sh | sudo bash
#   Unattended:         sudo KNA_MEDIA_ROOTS="/srv/videos" KNA_PASSWORD='secret' bash install-linux.sh --unattended
#   Update:             sudo kna-update              (installed by this script)
#   Remove:             sudo bash install-linux.sh --uninstall [--purge]
#
# It downloads the self-contained server package of a KnA release (no .NET SDK, no build), installs the GPU
# runtime for the vendors it finds (Intel / AMD VA-API, Vulkan), creates the "kna" systemd service and lets
# KnA itself install FFmpeg and the optional components. Settings live in /etc/kna/kna.env.
#
# Unattended settings (environment variables, all optional):
#   KNA_VERSION        latest | 3.0.0 …                   KNA_PORT          8585
#   KNA_MEDIA_ROOTS    read/write video folders, ';'      KNA_READONLY_ROOTS  read-only folders, ';'
#   KNA_UPLOAD_DIR     browser uploads destination        KNA_PASSWORD      web UI password (no single quotes)
#   KNA_GPU            auto | none | "intel amd nvidia"   KNA_FFMPEG_SOURCE btbn | distro
#   KNA_COMPONENTS     "anime4k fsrcnnx dovi_tool hdr10plus_tool ai-runtime" (subset)
#   KNA_UID / KNA_GID  ids of the service user (match the owner of your video files / bind mounts)
#   KNA_RPMFUSION      Fedora only: auto | yes | no — RPM Fusion for the full AMD/Intel VA-API drivers (H.264/HEVC)
# shellcheck disable=SC1090  # settings files are read at run time
set -euo pipefail

REPO="${KNA_REPO:-KonjikiNoYami1991/KnA}"
BRANCH="${KNA_BRANCH:-master}"
INSTALL_DIR=/opt/kna
DATA_DIR=/var/lib/kna
CONF_DIR=/etc/kna
ENV_FILE=$CONF_DIR/kna.env
INSTALL_CONF=$CONF_DIR/install.conf
UNIT=/etc/systemd/system/kna.service
TITLE="KnA - Konjiki no Autoencoder"

# ---------------------------------------------------------------- language, output
case "${KNA_INSTALL_LANG:-${LC_ALL:-${LC_MESSAGES:-${LANG:-}}}}" in it*) IT=1 ;; *) IT=0 ;; esac
t() { if ((IT)); then printf '%s' "$2"; else printf '%s' "$1"; fi; }
log() { printf '\033[1;35m[KnA]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[KnA] %s\033[0m\n' "$*" >&2; }
die() { printf '\033[1;31m[KnA] %s\033[0m\n' "$*" >&2; exit 1; }

usage() {
  cat <<EOF
KnA installer
  (no option)      install or reconfigure, with a wizard when run in a terminal
  --unattended     no questions: settings from KNA_* variables (see the top of this script)
  --update         download the newest release and restart the service (keeps the settings)
  --force          with --update: reinstall even when already up to date
  --uninstall      remove the service and the program (keeps settings and data)
  --purge          with --uninstall: also delete $CONF_DIR and $DATA_DIR (never your videos)
EOF
}

MODE=install UNATTENDED=0 FORCE=0 PURGE=0
for a in "$@"; do
  case "$a" in
    --unattended) UNATTENDED=1 ;;
    --update) MODE=update ;;
    --force) FORCE=1 ;;
    --uninstall) MODE=uninstall ;;
    --purge) PURGE=1 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; die "$(t "Unknown option: $a" "Opzione sconosciuta: $a")" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "$(t "Run as root (sudo)." "Esegui come root (sudo).")"
[[ -r /etc/os-release ]] || die "/etc/os-release missing"
. /etc/os-release
# Package manager family: apt (Debian, Ubuntu…), dnf (Fedora…), pacman (Arch…).
case " $ID ${ID_LIKE:-} " in
  *" debian "*|*" ubuntu "*) PM=apt ;;
  *" fedora "*) PM=dnf ;;
  *" arch "*|*" archlinux "*) PM=pacman ;;
  *) PM="" ;;
esac
[[ $PM == apt ]] && ! command -v apt-get >/dev/null && PM=""
[[ $PM == dnf ]] && ! command -v dnf >/dev/null && PM=""
[[ $PM == pacman ]] && ! command -v pacman >/dev/null && PM=""
[[ -n $PM ]] || die "$(t "Supported: Debian, Ubuntu, Fedora, Arch Linux and their derivatives (found: ${PRETTY_NAME:-$ID})." \
                       "Supportate: Debian, Ubuntu, Fedora, Arch Linux e derivate (trovato: ${PRETTY_NAME:-$ID}).")"
export DEBIAN_FRONTEND=noninteractive
HAVE_SYSTEMD=0; [[ -d /run/systemd/system ]] && HAVE_SYSTEMD=1

# ---------------------------------------------------------------- uninstall
if [[ $MODE == uninstall ]]; then
  log "$(t "Removing KnA…" "Rimozione di KnA…")"
  if ((HAVE_SYSTEMD)); then systemctl disable --now kna.service 2>/dev/null || true; fi
  rm -f "$UNIT" /usr/local/sbin/kna-update /usr/local/sbin/kna-install
  ((HAVE_SYSTEMD)) && systemctl daemon-reload
  rm -rf "$INSTALL_DIR" "$INSTALL_DIR.old"
  if ((PURGE)); then
    rm -rf "$CONF_DIR" "$DATA_DIR"
    id -u kna >/dev/null 2>&1 && userdel kna 2>/dev/null || true
    log "$(t "Settings and data deleted. Your video folders were not touched." "Impostazioni e dati eliminati. Le cartelle dei video non sono state toccate.")"
  else
    log "$(t "Settings kept in $CONF_DIR and $DATA_DIR (use --purge to delete them)." "Impostazioni conservate in $CONF_DIR e $DATA_DIR (usa --purge per eliminarle).")"
  fi
  exit 0
fi

case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) die "$(t "Unsupported CPU architecture: $(uname -m) (x86_64 and aarch64 only)." "Architettura non supportata: $(uname -m) (solo x86_64 e aarch64).")" ;;
esac

# ---------------------------------------------------------------- current settings (reconfigure / update)
KNA_URLS=""
if [[ -f $ENV_FILE ]]; then set -a; . "$ENV_FILE"; set +a; fi
if [[ -f $INSTALL_CONF ]]; then . "$INSTALL_CONF"; fi
[[ -n "$KNA_URLS" && -z "${KNA_PORT:-}" ]] && KNA_PORT="${KNA_URLS##*:}"
KNA_VERSION="${KNA_VERSION:-latest}"
KNA_PORT="${KNA_PORT:-8585}"
KNA_MEDIA_ROOTS="${KNA_MEDIA_ROOTS:-/srv/kna/videos}"
KNA_READONLY_ROOTS="${KNA_READONLY_ROOTS:-}"
KNA_UPLOAD_DIR="${KNA_UPLOAD_DIR:-}"
KNA_PASSWORD="${KNA_PASSWORD:-}"
KNA_GPU="${KNA_GPU:-auto}"
KNA_FFMPEG_SOURCE="${KNA_FFMPEG_SOURCE:-btbn}"
KNA_COMPONENTS="${KNA_COMPONENTS-anime4k fsrcnnx dovi_tool hdr10plus_tool}"
KNA_UID="${KNA_UID:-}"
KNA_GID="${KNA_GID:-}"
KNA_RPMFUSION="${KNA_RPMFUSION:-auto}"

# GPU vendors visible in this machine / container (PCI vendor of each render node).
detect_gpus() {
  local d v found=()
  for d in /sys/class/drm/renderD*; do
    [[ -r $d/device/vendor ]] || continue
    v=$(<"$d/device/vendor")
    case "$v" in 0x8086) found+=(intel) ;; 0x1002) found+=(amd) ;; 0x10de) found+=(nvidia) ;; esac
  done
  [[ -e /dev/nvidia0 ]] && found+=(nvidia)
  printf '%s\n' "${found[@]}" | sort -u | tr '\n' ' ' | sed 's/ $//'
}

# ---------------------------------------------------------------- package manager
pm_has() { # does the package exist in the repositories?
  case $PM in
    apt) apt-cache show "$1" >/dev/null 2>&1 ;;
    dnf) dnf -q list --available "$1" >/dev/null 2>&1 || rpm -q "$1" >/dev/null 2>&1 ;;
    pacman) pacman -Si "$1" >/dev/null 2>&1 || pacman -Q "$1" >/dev/null 2>&1 ;;
  esac
}
pm_install() { # install packages, failing if one is missing
  ((${#@})) || return 0
  case $PM in
    apt) apt-get install -y --no-install-recommends "$@" ;;
    dnf) dnf install -y --setopt=install_weak_deps=False --allowerasing "$@" ;;
    pacman) pacman -S --noconfirm --needed "$@" ;;
  esac
}
pm_try() { # install what exists, never fail on a missing optional package
  local p ok=(); for p in "$@"; do [[ -n $p ]] && pm_has "$p" && ok+=("$p"); done
  ((${#ok[@]})) && pm_install "${ok[@]}" || true
}
pick_pkg() { # first package name that exists in the repositories
  local p; for p in "$@"; do pm_has "$p" && { echo "$p"; return; }; done
}
pm_refresh() {
  case $PM in
    apt) apt-get update -qq ;;
    dnf) dnf -q makecache >/dev/null ;;
    pacman)
      # Fresh containers (e.g. the Proxmox archlinux-base template) may ship without an initialised keyring.
      if ! pacman-key --list-keys >/dev/null 2>&1; then pacman-key --init >/dev/null && pacman-key --populate >/dev/null; fi
      pacman -Sy --noconfirm --needed archlinux-keyring >/dev/null
      # Arch supports only full upgrades: installing on top of an old system would be a partial upgrade.
      pacman -Su --noconfirm ;;
  esac
}

# ---------------------------------------------------------------- wizard
INTERACTIVE=0
if ((!UNATTENDED)) && [[ $MODE == install ]] && (exec </dev/tty) 2>/dev/null; then INTERACTIVE=1; fi

if ((INTERACTIVE)) && ! command -v whiptail >/dev/null; then
  log "$(t "Installing whiptail for the wizard…" "Installazione di whiptail per il wizard…")"
  case $PM in
    apt) apt-get update -qq && apt-get install -y -qq whiptail >/dev/null ;;
    dnf) dnf install -y -q newt >/dev/null ;;
    pacman) pacman -Sy --noconfirm --needed libnewt >/dev/null ;;
  esac
fi

wt() { whiptail --title "$TITLE" "$@" 3>&1 1>&2 2>&3 </dev/tty; }

# Language: asked once in the wizard (unless KNA_INSTALL_LANG is set), preselected from the system locale.
if ((INTERACTIVE)) && [[ -z ${KNA_INSTALL_LANG:-} ]]; then
  lang=$(wt --notags --default-item "$( ((IT)) && echo it || echo en)" --ok-button "OK" --cancel-button "Cancel / Annulla" \
    --menu "Language / Lingua" 10 50 2 it "Italiano" en "English") || { echo "Cancelled / Annullato" >&2; exit 1; }
  [[ $lang == it ]] && IT=1 || IT=0
  export KNA_INSTALL_LANG=$lang
fi
cancelled() { die "$(t "Installation cancelled." "Installazione annullata.")"; }
valid_paths() { # ';'-separated absolute paths without single quotes
  local p IFS=';'
  for p in $1; do [[ -z $p || ( $p == /* && $p != *"'"* ) ]] || return 1; done
}

if ((INTERACTIVE)); then
  wt --msgbox "$(t "This wizard installs the KnA web edition as a service on this machine:

 - the newest KnA release (self-contained, no .NET to install)
 - the GPU runtime for the graphics chips it finds
 - FFmpeg and the optional components you pick
 - the 'kna' systemd service, reachable from any browser

Nothing is installed with Docker. Your video folders are never deleted." \
"Questo wizard installa l'edizione web di KnA come servizio su questa macchina:

 - l'ultima release di KnA (autonoma, senza installare .NET)
 - i driver per le GPU trovate
 - FFmpeg e i componenti opzionali che scegli
 - il servizio systemd 'kna', raggiungibile da qualsiasi browser

Niente Docker. Le cartelle dei video non vengono mai cancellate.")" 17 76 || cancelled

  while :; do
    KNA_PORT=$(wt --inputbox "$(t "Web UI port:" "Porta dell'interfaccia web:")" 9 60 "$KNA_PORT") || cancelled
    [[ $KNA_PORT =~ ^[0-9]+$ ]] && ((KNA_PORT >= 1 && KNA_PORT <= 65535)) && break
    wt --msgbox "$(t "Enter a number between 1 and 65535." "Inserisci un numero tra 1 e 65535.")" 8 50
  done

  while :; do
    KNA_MEDIA_ROOTS=$(wt --inputbox "$(t "Video folders KnA can browse, read and WRITE (outputs go next to the sources).
Absolute paths separated by ';'. Missing folders are created.
Bind mounts and network shares must already be mounted here." \
"Cartelle dei video che KnA può sfogliare, leggere e SCRIVERE (gli output vanno accanto ai sorgenti).
Percorsi assoluti separati da ';'. Le cartelle mancanti vengono create.
Bind mount e condivisioni di rete devono essere già montati qui.")" 13 78 "$KNA_MEDIA_ROOTS") || cancelled
    [[ -n $KNA_MEDIA_ROOTS ]] && valid_paths "$KNA_MEDIA_ROOTS" && break
    wt --msgbox "$(t "At least one absolute path (starting with /), no single quotes." "Almeno un percorso assoluto (che inizia con /), senza apici singoli.")" 8 70
  done

  while :; do
    KNA_READONLY_ROOTS=$(wt --inputbox "$(t "Optional READ-ONLY folders (browse and encode from, never written). ';'-separated, empty for none:" \
"Cartelle opzionali in SOLA LETTURA (si sfogliano e si codificano, mai scritte). Separate da ';', vuoto per nessuna:")" 10 78 "$KNA_READONLY_ROOTS") || cancelled
    valid_paths "$KNA_READONLY_ROOTS" && break
    wt --msgbox "$(t "Absolute paths only, no single quotes." "Solo percorsi assoluti, senza apici singoli.")" 8 60
  done

  first_root="${KNA_MEDIA_ROOTS%%;*}"
  while :; do
    KNA_UPLOAD_DIR=$(wt --inputbox "$(t "Where files uploaded from the browser are saved:" "Dove salvare i file caricati dal browser:")" 9 78 "${KNA_UPLOAD_DIR:-$first_root/uploads}") || cancelled
    [[ -z $KNA_UPLOAD_DIR ]] || valid_paths "$KNA_UPLOAD_DIR" && break
  done

  if wt --yesno "$(t "Protect the web UI with a password? (recommended when other people can reach this network)" \
                    "Proteggere l'interfaccia web con una password? (consigliato se altre persone possono raggiungere questa rete)")" 9 70; then
    while :; do
      p1=$(wt --passwordbox "$(t "Password (no single quotes):" "Password (senza apici singoli):")" 9 60) || cancelled
      p2=$(wt --passwordbox "$(t "Repeat the password:" "Ripeti la password:")" 9 60) || cancelled
      if [[ -n $p1 && $p1 == "$p2" && $p1 != *"'"* ]]; then KNA_PASSWORD=$p1; break; fi
      wt --msgbox "$(t "Empty, different or containing a single quote. Try again." "Vuota, diversa o con un apice singolo. Riprova.")" 8 60
    done
  else
    KNA_PASSWORD=""
  fi

  det=$(detect_gpus)
  on() { [[ " $det " == *" $1 "* ]] && echo ON || echo OFF; }
  gpus=$(wt --checklist "$(t "GPU runtime to install (detected: ${det:-none}).
Hardware encoding needs the GPU passed through to this container/VM." \
"Driver GPU da installare (rilevate: ${det:-nessuna}).
La codifica hardware richiede la GPU passata a questo container/VM.")" 15 78 3 \
    intel "$(t "Intel: Quick Sync / VA-API (media driver, oneVPL)" "Intel: Quick Sync / VA-API (media driver, oneVPL)")" "$(on intel)" \
    amd "$(t "AMD: VA-API (Mesa)" "AMD: VA-API (Mesa)")" "$(on amd)" \
    nvidia "$(t "NVIDIA: NVENC (driver must match the host, see the guide)" "NVIDIA: NVENC (il driver deve coincidere con l'host, vedi guida)")" "$(on nvidia)") || cancelled
  KNA_GPU=$(echo "$gpus" | tr -d '"'); KNA_GPU=${KNA_GPU:-none}

  if [[ $PM == dnf && ( " $KNA_GPU " == *" amd "* || " $KNA_GPU " == *" intel "* ) ]]; then
    nodef=(); [[ $KNA_RPMFUSION == no ]] && nodef=(--defaultno)
    if wt "${nodef[@]}" --yesno "$(t "Fedora's own VA-API drivers have H.264 and HEVC removed (patents): hardware encoding of those codecs needs the drivers from RPM Fusion (mesa-va-drivers-freeworld for AMD, intel-media-driver for Intel).

Enable the RPM Fusion free and nonfree repositories and use them?" \
"I driver VA-API di Fedora non hanno H.264 e HEVC (brevetti): per la codifica hardware di questi codec servono i driver di RPM Fusion (mesa-va-drivers-freeworld per AMD, intel-media-driver per Intel).

Abilitare i repository RPM Fusion free e nonfree e usarli?")" 14 78; then KNA_RPMFUSION=yes; else KNA_RPMFUSION=no; fi
  fi

  KNA_FFMPEG_SOURCE=$(wt --default-item "$KNA_FFMPEG_SOURCE" --menu "$(t "FFmpeg build:" "Build di FFmpeg:")" 12 78 2 \
    btbn "$(t "Newest static build (recommended, updatable from KnA)" "Build statica più recente (consigliata, aggiornabile da KnA)")" \
    distro "$(t "Package of $PRETTY_NAME (older, fewer encoders)" "Pacchetto di $PRETTY_NAME (più vecchio, meno encoder)")") || cancelled

  c() { [[ " $KNA_COMPONENTS " == *" $1 "* ]] && echo ON || echo OFF; }
  comps=$(wt --checklist "$(t "Optional components (they can also be installed later from Settings → Components):" \
                             "Componenti opzionali (installabili anche dopo da Impostazioni → Componenti):")" 15 78 5 \
    anime4k "Anime4K (GPU upscaling shaders)" "$(c anime4k)" \
    fsrcnnx "FSRCNNX (GPU upscaling shader)" "$(c fsrcnnx)" \
    dovi_tool "dovi_tool (Dolby Vision)" "$(c dovi_tool)" \
    hdr10plus_tool "hdr10plus_tool (HDR10+)" "$(c hdr10plus_tool)" \
    ai-runtime "$(t "AI upscaler: Python + PyTorch (several GB)" "Upscaler AI: Python + PyTorch (diversi GB)")" "$(c ai-runtime)") || cancelled
  KNA_COMPONENTS=$(echo "$comps" | tr -d '"')

  if wt --defaultno --yesno "$(t "Choose the user/group ids of the 'kna' service user?

Useful when the video folders belong to a specific user, e.g. bind mounts from the host. Otherwise system ids are assigned automatically." \
"Scegliere gli id utente/gruppo del servizio 'kna'?

Utile quando le cartelle dei video appartengono a un utente preciso, per esempio bind mount dall'host. Altrimenti vengono assegnati id di sistema automatici.")" 12 76; then
    while :; do
      ids=$(wt --inputbox "UID:GID" 8 40 "${KNA_UID:-1000}:${KNA_GID:-1000}") || cancelled
      if [[ $ids =~ ^([0-9]+):([0-9]+)$ ]]; then KNA_UID=${BASH_REMATCH[1]}; KNA_GID=${BASH_REMATCH[2]}; break; fi
    done
  fi

  wt --yesno "$(t "Ready to install:" "Pronto per installare:")

  $(t "Port" "Porta"):            $KNA_PORT
  $(t "Video folders" "Cartelle video"):   $KNA_MEDIA_ROOTS
  $(t "Read-only" "Sola lettura"):       ${KNA_READONLY_ROOTS:--}
  Upload:          ${KNA_UPLOAD_DIR:--}
  Password:        $([[ -n $KNA_PASSWORD ]] && t "yes" "sì" || t "no" "no")
  GPU:             $KNA_GPU
  FFmpeg:          $KNA_FFMPEG_SOURCE
  $(t "Components" "Componenti"):      ${KNA_COMPONENTS:--}
  $(t "Service user" "Utente servizio"):    kna ${KNA_UID:+($KNA_UID:$KNA_GID)}" 18 78 || cancelled
fi

[[ $KNA_PORT =~ ^[0-9]+$ ]] || die "KNA_PORT: $KNA_PORT"
for v in KNA_MEDIA_ROOTS KNA_READONLY_ROOTS KNA_UPLOAD_DIR; do
  valid_paths "${!v}" || die "$v: $(t "absolute paths only, no single quotes" "solo percorsi assoluti, senza apici singoli")"
done
[[ $KNA_PASSWORD != *"'"* ]] || die "KNA_PASSWORD: $(t "single quotes are not allowed" "gli apici singoli non sono ammessi")"
[[ $KNA_GPU == auto ]] && { KNA_GPU=$(detect_gpus); KNA_GPU=${KNA_GPU:-none}; }

# ---------------------------------------------------------------- packages
add_components() { # add apt components (non-free, multiverse…) to every source line of a file
  local f=$1; shift
  awk -v want="$*" '
    BEGIN { n = split(want, W, " ") }
    /^[[:space:]]*Components:/ || /^[[:space:]]*deb(-src)?[[:space:]]/ {
      line = $0
      for (i = 1; i <= n; i++) if (line !~ ("[[:space:]]" W[i] "([[:space:]]|$)")) line = line " " W[i]
      print line; next
    }
    { print }' "$f" > "$f.kna-tmp" && cat "$f.kna-tmp" > "$f" && rm -f "$f.kna-tmp"
}
has_gpu() { [[ " $KNA_GPU " == *" $1 "* ]]; }

log "$(t "Installing packages for $PRETTY_NAME…" "Installazione dei pacchetti per $PRETTY_NAME…")"
case $PM in
apt)
  if has_gpu intel; then
    # Intel's full media driver is in Debian non-free / Ubuntu multiverse.
    if [[ $ID == debian || " ${ID_LIKE:-} " != *" ubuntu "* && $ID != ubuntu ]]; then comps="contrib non-free non-free-firmware"; else comps="multiverse"; fi
    for f in /etc/apt/sources.list /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources; do
      [[ -f $f ]] && grep -Eq "(debian|ubuntu)" "$f" && add_components "$f" $comps
    done
  fi
  pm_refresh
  # Runtime libraries of self-contained .NET (ICU, OpenSSL), fonts for subtitles, Vulkan (libplacebo) and vainfo.
  base=(ca-certificates curl unzip xz-utils tar fontconfig fonts-dejavu-core libvulkan1 mesa-vulkan-drivers vainfo)
  icu=$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -1 || true)
  ssl=$(pick_pkg libssl3t64 libssl3 libssl1.1)
  [[ -n $icu ]] && base+=("$icu")
  [[ -n $ssl ]] && base+=("$ssl")
  pm_install "${base[@]}"
  has_gpu intel && pm_try "$(pick_pkg intel-media-va-driver-non-free intel-media-va-driver)" i965-va-driver libvpl2 libmfx-gen1.2 libmfx-gen1
  # Newer releases ship the VA driver inside mesa-libgallium; mesa-va-drivers is then a transitional package.
  has_gpu amd && pm_try mesa-va-drivers
  PY_PKGS=(python3 python3-venv python3-pip)
  ;;
dnf)
  pm_refresh
  pm_install ca-certificates curl unzip xz tar findutils shadow-utils util-linux procps-ng fontconfig dejavu-sans-fonts \
             libicu openssl-libs vulkan-loader mesa-vulkan-drivers libva-utils
  if [[ $KNA_RPMFUSION == auto ]]; then
    if has_gpu amd || has_gpu intel; then KNA_RPMFUSION=yes; else KNA_RPMFUSION=no; fi
  fi
  if [[ $KNA_RPMFUSION == yes ]] && ! rpm -q rpmfusion-free-release rpmfusion-nonfree-release >/dev/null 2>&1; then
    log "$(t "Enabling RPM Fusion (free + nonfree) for the full VA-API drivers…" "Abilitazione di RPM Fusion (free + nonfree) per i driver VA-API completi…")"
    fv=$(rpm -E %fedora)
    dnf install -y "https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-$fv.noarch.rpm" \
                   "https://mirrors.rpmfusion.org/nonfree/fedora/rpmfusion-nonfree-release-$fv.noarch.rpm" \
      || { warn "$(t "RPM Fusion could not be enabled: using Fedora's drivers (no H.264/HEVC hardware encoding)." \
                     "Impossibile abilitare RPM Fusion: uso i driver di Fedora (niente codifica hardware H.264/HEVC).")"; KNA_RPMFUSION=no; }
    dnf -q makecache >/dev/null || true
  fi
  if has_gpu amd; then
    if [[ $KNA_RPMFUSION == yes ]]; then
      # Replaces Fedora's codec-stripped VA driver (where it is a separate package) with RPM Fusion's.
      if rpm -q mesa-va-drivers >/dev/null 2>&1; then dnf swap -y mesa-va-drivers mesa-va-drivers-freeworld || true
      else pm_try mesa-va-drivers-freeworld; fi
    else
      pm_try mesa-va-drivers
      warn "$(t "AMD on Fedora without RPM Fusion: H.264/HEVC hardware encoding is not available (AV1 is)." \
                "AMD su Fedora senza RPM Fusion: la codifica hardware H.264/HEVC non è disponibile (AV1 sì).")"
    fi
  fi
  if has_gpu intel; then
    if [[ $KNA_RPMFUSION == yes ]]; then pm_try intel-media-driver; else pm_try intel-media-driver-free; fi
    pm_try libvpl "$(pick_pkg intel-vpl-gpu-rt oneVPL-intel-gpu)" libva-intel-driver
  fi
  PY_PKGS=(python3 python3-pip)
  ;;
pacman)
  pm_refresh
  # The VA-API driver for AMD (and the other Mesa drivers) is part of the mesa package.
  pm_install ca-certificates curl unzip xz tar fontconfig ttf-dejavu icu openssl vulkan-icd-loader mesa libva-utils
  has_gpu amd && pm_try vulkan-radeon
  has_gpu intel && pm_try intel-media-driver vpl-gpu-rt libvpl vulkan-intel
  PY_PKGS=(python python-pip)
  ;;
esac
if [[ $KNA_FFMPEG_SOURCE == distro ]]; then
  # Fedora's own build is ffmpeg-free; with RPM Fusion the full one is called ffmpeg.
  pm_install "$(pick_pkg ffmpeg ffmpeg-free)"
fi
[[ " $KNA_COMPONENTS " == *" ai-runtime "* ]] && pm_install "${PY_PKGS[@]}"
has_gpu nvidia && ! command -v nvidia-smi >/dev/null && warn "$(t \
  "NVIDIA: the user-space driver (same version as the host) must be installed here by hand; see docs/PROXMOX.md." \
  "NVIDIA: il driver user-space (stessa versione dell'host) va installato qui a mano; vedi docs/PROXMOX.it.md.")"

# ---------------------------------------------------------------- download the release
if [[ $KNA_VERSION == latest ]]; then
  url=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest") \
    || die "$(t "Cannot reach GitHub." "GitHub non raggiungibile.")"
  TAG=${url##*/}
else
  TAG="v${KNA_VERSION#v}"
fi
[[ $TAG == v* ]] || die "$(t "No KnA release found on GitHub." "Nessuna release di KnA trovata su GitHub.")"
CURRENT=$(cat "$INSTALL_DIR/.kna-version" 2>/dev/null || true)

if [[ $MODE == update && $CURRENT == "$TAG" && $FORCE -eq 0 ]]; then
  log "$(t "KnA $TAG is already installed." "KnA $TAG è già installato.")"
else
  mkdir -p "$(dirname "$INSTALL_DIR")"
  # New version, old one kept as .old and the download: about three times the program. Fail early with a clear
  # message instead of halfway through (in a Proxmox container: kna-lxc.sh --resize <id> on the host).
  need_mb=$(( $(du -sm "$INSTALL_DIR" 2>/dev/null | cut -f1 || echo 300) * 3 + 300 ))
  free_mb=$(df -Pm "$(dirname "$INSTALL_DIR")" | awk 'NR==2 {print $4}')
  if [[ $free_mb =~ ^[0-9]+$ ]] && ((free_mb < need_mb)); then
    die "$(t "Not enough disk space: $free_mb MB free, about $need_mb MB needed. Free some space or grow the disk (Proxmox host: pct resize <id> rootfs +4G, or kna-lxc.sh --resize <id>)." \
             "Spazio su disco insufficiente: $free_mb MB liberi, ne servono circa $need_mb. Libera spazio o ingrandisci il disco (host Proxmox: pct resize <id> rootfs +4G, oppure kna-lxc.sh --resize <id>).")"
  fi
  log "$(t "Downloading KnA $TAG ($RID)…" "Download di KnA $TAG ($RID)…")"
  tmp=$(mktemp -d "$(dirname "$INSTALL_DIR")/.kna-download.XXXXXX")
  trap 'rm -rf "$tmp"' EXIT
  curl -fL --retry 3 --progress-bar -o "$tmp/kna.zip" "https://github.com/$REPO/releases/download/$TAG/KnA-server-$RID.zip" \
    || die "$(t "Download failed." "Download non riuscito.")"
  unzip -q "$tmp/kna.zip" -d "$tmp/kna"
  [[ -f $tmp/kna/kna-server ]] || die "$(t "The package does not contain kna-server." "Il pacchetto non contiene kna-server.")"
  chmod 755 "$tmp/kna/kna-server"; [[ -f $tmp/kna/tools/fdkaac ]] && chmod 755 "$tmp/kna/tools/fdkaac"
  echo "$TAG" > "$tmp/kna/.kna-version"
  if ((HAVE_SYSTEMD)) && systemctl is-active --quiet kna.service; then systemctl stop kna.service; fi
  rm -rf "$INSTALL_DIR.old"
  [[ -d $INSTALL_DIR ]] && mv "$INSTALL_DIR" "$INSTALL_DIR.old"
  mv "$tmp/kna" "$INSTALL_DIR"
  chown -R root:root "$INSTALL_DIR"
  log "$(t "KnA $TAG installed in $INSTALL_DIR." "KnA $TAG installato in $INSTALL_DIR.")"
fi

# ---------------------------------------------------------------- service user, folders, settings
if [[ $MODE == install ]]; then
  SERVICE_GROUP=kna
  if [[ -n $KNA_GID ]] && g=$(getent group "$KNA_GID" | cut -d: -f1) && [[ -n $g ]]; then SERVICE_GROUP=$g; fi
  # With an explicit id (e.g. the one an LXC user mapping points to) the user is not a "system" one:
  # useradd/groupadd would only warn that the id is above SYS_UID_MAX/SYS_GID_MAX.
  getent group "$SERVICE_GROUP" >/dev/null || groupadd ${KNA_GID:+--gid "$KNA_GID"} ${KNA_GID:---system} "$SERVICE_GROUP"
  SERVICE_USER=kna
  if [[ -n $KNA_UID ]] && u=$(getent passwd "$KNA_UID" | cut -d: -f1) && [[ -n $u ]]; then SERVICE_USER=$u; fi
  id -u "$SERVICE_USER" >/dev/null 2>&1 || useradd ${KNA_UID:+--uid "$KNA_UID"} ${KNA_UID:---system} --gid "$SERVICE_GROUP" \
    --home-dir "$DATA_DIR" --no-create-home --shell "$(command -v nologin || echo /usr/sbin/nologin)" "$SERVICE_USER"
  for g in render video; do getent group "$g" >/dev/null && usermod -aG "$g" "$SERVICE_USER"; done

  mkdir -p "$DATA_DIR" "$CONF_DIR"
  chown "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR"
  IFS=';' read -ra dirs <<< "$KNA_MEDIA_ROOTS;$KNA_UPLOAD_DIR"
  for d in "${dirs[@]}"; do
    [[ -z $d ]] && continue
    # Existing folders are never touched. A missing one is created by the service user itself when it can
    # (e.g. 'uploads' inside a writable NFS/SMB share: no chown, the share keeps its own rules); otherwise
    # by root and then given to the service user, which only works on local filesystems.
    if [[ ! -d $d ]]; then
      if runuser -u "$SERVICE_USER" -- mkdir -p "$d" 2>/dev/null; then
        log "$(t "Created $d" "Creata $d")"
      elif mkdir -p "$d" 2>/dev/null; then
        chown "$SERVICE_USER:$SERVICE_GROUP" "$d" 2>/dev/null \
          || warn "$(t "Created $d, but its owner cannot be changed (network share?): left as it is." \
                       "Creata $d, ma il proprietario non si può cambiare (condivisione di rete?): lasciata com'è.")"
        log "$(t "Created $d" "Creata $d")"
      else
        warn "$(t "Cannot create $d: create it yourself, then restart KnA (systemctl restart kna)." \
                  "Impossibile creare $d: creala tu, poi riavvia KnA (systemctl restart kna).")"
        continue
      fi
    fi
    runuser -u "$SERVICE_USER" -- test -w "$d" \
      || warn "$(t "The '$SERVICE_USER' user cannot write to $d: outputs and uploads there will fail." \
                   "L'utente '$SERVICE_USER' non può scrivere in $d: lì output e upload non riusciranno.")"
  done

  q() { printf "%s='%s'\n" "$1" "$2"; }
  {
    echo "# KnA settings. Edit, then: systemctl restart kna"
    echo "# Every variable: https://github.com/$REPO/blob/$BRANCH/docs/PROXMOX.md#environment-variables"
    q KNA_URLS "http://0.0.0.0:$KNA_PORT"
    q KNA_DATA_DIR "$DATA_DIR"
    q KNA_MEDIA_ROOTS "$KNA_MEDIA_ROOTS"
    [[ -n $KNA_READONLY_ROOTS ]] && q KNA_READONLY_ROOTS "$KNA_READONLY_ROOTS"
    [[ -n $KNA_UPLOAD_DIR ]] && q KNA_UPLOAD_DIR "$KNA_UPLOAD_DIR"
    [[ -n $KNA_PASSWORD ]] && q KNA_PASSWORD "$KNA_PASSWORD"
    true
  } > "$ENV_FILE"
  chown "root:$SERVICE_GROUP" "$ENV_FILE"; chmod 640 "$ENV_FILE"
  {
    echo "# Installer choices, reused by kna-update"
    q KNA_GPU "$KNA_GPU"; q KNA_FFMPEG_SOURCE "$KNA_FFMPEG_SOURCE"; q KNA_COMPONENTS "$KNA_COMPONENTS"
    [[ $PM == dnf ]] && q KNA_RPMFUSION "$KNA_RPMFUSION"
    q KNA_UID "$(id -u "$SERVICE_USER")"; q KNA_GID "$(getent group "$SERVICE_GROUP" | cut -d: -f3)"
    q SERVICE_USER "$SERVICE_USER"; q SERVICE_GROUP "$SERVICE_GROUP"
  } > "$INSTALL_CONF"

  groups=""; for g in render video; do getent group "$g" >/dev/null && groups+="$g "; done
  cat > "$UNIT" <<EOF
[Unit]
Description=KnA - Konjiki no Autoencoder (web)
After=network-online.target remote-fs.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_GROUP
${groups:+SupplementaryGroups=${groups% }}
WorkingDirectory=$INSTALL_DIR
EnvironmentFile=$ENV_FILE
ExecStart=$INSTALL_DIR/kna-server
UMask=0002
Restart=on-failure
RestartSec=5
Nice=5
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF
fi
SERVICE_USER=${SERVICE_USER:-kna}
SERVICE_GROUP=${SERVICE_GROUP:-kna}

# kna-update: fetches the newest installer, falls back to this copy when GitHub's raw files are unreachable.
# kna-install: a copy of this installer, for --uninstall and reconfiguration.
if [[ -f ${BASH_SOURCE[0]:-} ]]; then
  [[ ${BASH_SOURCE[0]} -ef /usr/local/sbin/kna-install ]] || install -m 755 "${BASH_SOURCE[0]}" /usr/local/sbin/kna-install
elif curl -fsSL "https://raw.githubusercontent.com/$REPO/$BRANCH/deploy/install-linux.sh" -o /usr/local/sbin/kna-install.tmp; then
  install -m 755 /usr/local/sbin/kna-install.tmp /usr/local/sbin/kna-install
fi
rm -f /usr/local/sbin/kna-install.tmp
cat > /usr/local/sbin/kna-update <<EOF
#!/bin/sh
# Updates KnA to the newest release. Options: --force (reinstall), see kna-install --help.
set -e
tmp=\$(mktemp)
trap 'rm -f "\$tmp"' EXIT
if curl -fsSL "https://raw.githubusercontent.com/$REPO/$BRANCH/deploy/install-linux.sh" -o "\$tmp"; then
  bash "\$tmp" --update "\$@"
elif [ -f /usr/local/sbin/kna-install ]; then
  bash /usr/local/sbin/kna-install --update "\$@"
else
  echo "Cannot download the installer." >&2; exit 1
fi
EOF
chmod 755 /usr/local/sbin/kna-update

# ---------------------------------------------------------------- start, components
auth=(); [[ -n ${KNA_PASSWORD:-} ]] && auth=(-u "kna:$KNA_PASSWORD")
api="http://127.0.0.1:$KNA_PORT"
TEMP_PID=""
if ((HAVE_SYSTEMD)); then
  systemctl daemon-reload
  systemctl enable kna.service >/dev/null 2>&1
  systemctl restart kna.service
elif [[ $MODE == install ]]; then
  warn "$(t "systemd is not running: the service is installed but not started. Starting KnA temporarily for the components…" \
            "systemd non è attivo: il servizio è installato ma non avviato. Avvio temporaneo di KnA per i componenti…")"
  # setpriv replaces itself with kna-server (no child left behind when the temporary server is stopped).
  (set -a; . "$ENV_FILE"; set +a
   exec setpriv --reuid="$SERVICE_USER" --regid="$SERVICE_GROUP" --init-groups "$INSTALL_DIR/kna-server") >/tmp/kna-install-server.log 2>&1 &
  TEMP_PID=$!
fi

up=0
((HAVE_SYSTEMD)) || [[ -n $TEMP_PID ]] || up=-1
for _ in $(seq 1 90); do
  ((up == 0)) || break
  if curl -fsS "${auth[@]}" "$api/api/info" >/dev/null 2>&1; then up=1; break; fi
  sleep 1
done
if ((up < 0)); then
  :
elif ((!up)); then
  warn "$(t "KnA did not answer on port $KNA_PORT. Check: journalctl -u kna -e" "KnA non risponde sulla porta $KNA_PORT. Controlla: journalctl -u kna -e")"
elif [[ $MODE == install ]]; then
  ids=""; [[ $KNA_FFMPEG_SOURCE == btbn ]] && ids="ffmpeg"
  ids="$ids $KNA_COMPONENTS"
  for id in $ids; do
    log "$(t "Installing component: $id…" "Installazione del componente: $id…")"
    code=$(curl -sS "${auth[@]}" -o /tmp/kna-component.json -w '%{http_code}' -X POST --max-time 3600 "$api/api/components/$id/install" || echo 000)
    if [[ $code == 200 ]]; then
      log "  ✓ $id"
    else
      warn "  ✗ $id ($code): $(head -c 300 /tmp/kna-component.json 2>/dev/null)"
      if [[ $id == ffmpeg ]]; then
        warn "$(t "Falling back to the FFmpeg package of $PRETTY_NAME." "Ripiego sul pacchetto FFmpeg di $PRETTY_NAME.")"
        pm_install "$(pick_pkg ffmpeg ffmpeg-free)" || true
      fi
    fi
  done
  rm -f /tmp/kna-component.json
fi
if [[ -n $TEMP_PID ]]; then kill "$TEMP_PID" 2>/dev/null || true; wait "$TEMP_PID" 2>/dev/null || true; fi
((HAVE_SYSTEMD)) && [[ $MODE == install ]] && systemctl restart kna.service

# ---------------------------------------------------------------- summary
ip=$(hostname -I 2>/dev/null | awk '{print $1}')
echo
log "$(t "Done!" "Fatto!")  →  http://${ip:-<ip>}:$KNA_PORT"
log "$(t "Video folders" "Cartelle video"): ${KNA_MEDIA_ROOTS}${KNA_READONLY_ROOTS:+  ·  $(t "read-only" "sola lettura"): $KNA_READONLY_ROOTS}"
log "$(t "Settings" "Impostazioni"): $ENV_FILE  ·  Log: journalctl -u kna -f  ·  $(t "Update" "Aggiornamento"): kna-update"
if compgen -G "/dev/dri/renderD*" >/dev/null; then
  log "$(t "GPU render node found: KnA tests every encoder at the first start (Hardware card)." \
           "Render node GPU trovato: KnA prova ogni encoder al primo avvio (scheda Hardware).")"
else
  log "$(t "No /dev/dri render node: only CPU encoding until a GPU is passed through." \
           "Nessun render node /dev/dri: solo codifica su CPU finché non viene passata una GPU.")"
fi
