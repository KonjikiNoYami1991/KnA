#!/usr/bin/env bash
# KnA container start: runs the server as PUID:PGID (like the containers of Synology, QNAP, UGREEN, Unraid…), so the
# files it writes on your shares belong to your user, not to root; joins the groups of the GPU device nodes found
# (/dev/dri/renderD*, /dev/dri/card*, /dev/nvidia*) so hardware encoding works without looking up group ids.
#   PUID / PGID   user and group id (e.g. 1000 / 1000; Synology: id <user> in an SSH session). Unset = root (as before 3.3).
#   UMASK         permissions of new files, default 002 (group can write)
set -euo pipefail
PUID="${PUID:-0}"
PGID="${PGID:-0}"
umask "${UMASK:-002}"
CONFIG="${KNA_DATA_DIR:-/config}"

if [[ $PUID == 0 ]]; then
  exec dotnet /app/kna-server.dll "$@"
fi

# a group and a user with the requested ids (names don't matter, ids do)
group=$(getent group "$PGID" | cut -d: -f1 || true)
if [[ -z $group ]]; then group=kna; groupadd -o -g "$PGID" "$group"; fi
user=$(getent passwd "$PUID" | cut -d: -f1 || true)
if [[ -z $user ]]; then user=kna; useradd -o -u "$PUID" -g "$PGID" -M -d "$CONFIG" -s /usr/sbin/nologin "$user"; fi

# GPU device nodes: their group ids come from the host and differ on every system.
extra=()
for dev in /dev/dri/renderD* /dev/dri/card* /dev/nvidia*; do
  [[ -e $dev ]] || continue
  gid=$(stat -c %g "$dev")
  [[ $gid == 0 ]] && continue
  getent group "$gid" >/dev/null || groupadd -o -g "$gid" "gpu$gid"
  extra+=("$gid")
done

# settings and the data KnA writes itself (not the media folders: those keep the rights of your share)
chown "$PUID:$PGID" "$CONFIG" 2>/dev/null || echo "[KnA] warning: cannot change the owner of $CONFIG: it must be writable by $PUID:$PGID" >&2
for d in "$CONFIG"/*; do
  [[ -e $d ]] || continue
  [[ $(stat -c %u "$d") == "$PUID" ]] || chown -R "$PUID:$PGID" "$d" 2>/dev/null || true
done

groups="$PGID"
for g in "${extra[@]}"; do groups="$groups,$g"; done
export HOME="$CONFIG"
exec setpriv --reuid="$PUID" --regid="$PGID" --groups="$groups" -- dotnet /app/kna-server.dll "$@"
