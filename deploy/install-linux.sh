#!/usr/bin/env bash
# Installs KnA web edition as a systemd service on a headless Debian/Ubuntu machine
# (Proxmox LXC, VM, bare metal). Run as root from a clone of the repository:
#
#   sudo ./deploy/install-linux.sh                 # install / update
#   sudo KNA_PORT=9000 KNA_MEDIA_ROOTS="/mnt/nas;/media" ./deploy/install-linux.sh
#
# Afterwards open http://<ip>:8585 (or your KNA_PORT).
set -euo pipefail

KNA_PORT="${KNA_PORT:-8585}"
KNA_MEDIA_ROOTS="${KNA_MEDIA_ROOTS:-/media;/mnt}"
INSTALL_DIR=/opt/kna
DATA_DIR=/var/lib/kna
SERVICE_USER=kna
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

log() { printf '\033[1;35m[KnA]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[KnA] %s\033[0m\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root (sudo)."
[[ -f "$REPO_DIR/src/KnA.Server/KnA.Server.csproj" ]] || die "Run this script from a clone of the KnA repository."
. /etc/os-release
export DEBIAN_FRONTEND=noninteractive

log "Installing packages for $PRETTY_NAME…"
apt-get update
apt-get install -y --no-install-recommends ca-certificates curl ffmpeg fontconfig fonts-dejavu-core \
  mesa-va-drivers vainfo libvulkan1 mesa-vulkan-drivers python3 python3-venv
# Standalone Fraunhofer AAC encoder (Ubuntu multiverse / Debian non-free); KnA can also install its own build from Components.
apt-get install -y --no-install-recommends fdkaac 2>/dev/null || log "fdkaac not in your repositories: install it later from Settings → Components."

# Upgrade from TnA 2.x: stop the old service and keep its data.
if systemctl list-unit-files tna.service >/dev/null 2>&1 && [[ -f /etc/systemd/system/tna.service ]]; then
  log "Migrating the TnA service to KnA…"
  systemctl disable --now tna.service || true
  rm -f /etc/systemd/system/tna.service
fi
if [[ -d /var/lib/tna && ! -d "$DATA_DIR" ]]; then mv /var/lib/tna "$DATA_DIR"; fi

if ! apt-cache show dotnet-sdk-10.0 >/dev/null 2>&1; then
  # Debian: .NET comes from the Microsoft repository.
  log "Adding the Microsoft package repository for .NET 10…"
  curl -fsSL "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/ms.deb \
    || die "No .NET 10 packages for ${PRETTY_NAME}. Use Ubuntu 24.04+ or Debian 12+."
  dpkg -i /tmp/ms.deb && rm -f /tmp/ms.deb
  apt-get update
fi
apt-get install -y --no-install-recommends dotnet-sdk-10.0 aspnetcore-runtime-10.0

if [[ "$(dpkg --print-architecture)" == "amd64" ]]; then
  # Intel Quick Sync / VA-API runtime (non-free driver lives in Ubuntu multiverse / Debian non-free).
  apt-get install -y --no-install-recommends intel-media-va-driver-non-free libvpl2 libmfx-gen1.2 2>/dev/null \
    || apt-get install -y --no-install-recommends intel-media-va-driver 2>/dev/null \
    || log "Intel media driver not available from your repositories (only needed for Intel GPUs)."
fi

log "Building KnA…"
dotnet publish "$REPO_DIR/src/KnA.Server/KnA.Server.csproj" -c Release -o "$INSTALL_DIR" --nologo -v quiet

log "Creating service user and folders…"
id -u "$SERVICE_USER" >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
for g in render video; do getent group "$g" >/dev/null && usermod -aG "$g" "$SERVICE_USER"; done
mkdir -p "$DATA_DIR"
chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"

log "Writing systemd unit…"
cat > /etc/systemd/system/kna.service <<EOF
[Unit]
Description=KnA - Konjiki no Autoencoder (web)
After=network-online.target remote-fs.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
SupplementaryGroups=render video
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/dotnet $INSTALL_DIR/kna-server.dll
Environment=KNA_DATA_DIR=$DATA_DIR
Environment="KNA_MEDIA_ROOTS=$KNA_MEDIA_ROOTS"
Environment=KNA_URLS=http://0.0.0.0:$KNA_PORT
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
# Environment=KNA_PASSWORD=change-me
Restart=on-failure
RestartSec=5
Nice=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now kna.service
systemctl restart kna.service

ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
log "Done! Open http://${ip:-<this-host>}:${KNA_PORT}"
log "Media roots: $KNA_MEDIA_ROOTS  (the 'kna' user needs read/write access there)"
if [[ -e /dev/dri/renderD128 ]]; then
  log "GPU render node found. Check supported codecs with: vainfo --display drm --device /dev/dri/renderD128"
else
  log "No /dev/dri/renderD128: hardware encoding needs GPU passthrough (see docs/PROXMOX.md)."
fi
log "Logs: journalctl -u kna -f"
