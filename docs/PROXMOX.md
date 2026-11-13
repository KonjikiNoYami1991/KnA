# KnA on Proxmox VE and Linux servers

**English** · [Italiano](PROXMOX.it.md)

KnA's web edition runs headless: the encoder runs on the server, and you use it from any browser
(PC, Mac, phone) at `http://<server-ip>:8585`. It is installed **natively** as a systemd service: no Docker.

There are two scripts:

| Script | Where you run it | What it does |
|---|---|---|
| [`deploy/proxmox/kna-lxc.sh`](../deploy/proxmox/kna-lxc.sh) | on the **Proxmox host** | creates an LXC container (resources, network, GPU, bind mounts, user mapping) and installs KnA inside |
| [`deploy/install-linux.sh`](../deploy/install-linux.sh) | **inside** an existing LXC, VM or Debian/Ubuntu machine | installs or updates KnA as the `kna` service |

Both have a wizard when started from a terminal, and an unattended mode driven by environment variables.

> Every path, address and id in this guide is an **example from a test environment**: use your own.

## 1. One command on the Proxmox host

Open the node's shell (*Datacenter → node → Shell*) and run:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/deploy/proxmox/kna-lxc.sh)"
```

The wizard first asks for the **language** (Italian / English), then shows **one review screen** with every setting
and its current value. Select a line to change it, then *Create the container*. Nothing is created before that.
Each value is checked when you enter it (id already used by a container or a VM, storage, free space, paths, IP).

| Line | What you set |
|---|---|
| ID / hostname | a free id (the next one is proposed) and the name |
| System | Debian 12 / 13, Ubuntu 24.04; unprivileged (recommended) or privileged |
| CPU / RAM / swap | defaults: half of the host's cores (all of them on small hosts), 4 GB, 512 MB |
| Disk | storage (with its free space) and size, 16 GB by default |
| Template | storage the OS template is downloaded to |
| Network | bridge, DHCP or a static address with gateway, VLAN |
| GPU | every render node of the host with vendor and model (Proxmox VE 8.2+) |
| Host folders | bind mounts, as many as you want: host folder → container folder, read/write or read-only. The wizard lists the mount points already set up on the host, read from Proxmox storages (`/etc/pve/storage.cfg`), `/etc/fstab`, systemd `.mount` units and current mounts, with type (`nfs4`, `cifs`, `zfs`…), origin and state (mounted / NOT mounted / runtime only); or *Other path*. A network share that is not mounted is refused: mount it first (*Datacenter → Storage*, or `/etc/fstab`) |
| Folders in CT | video folders on the container's own disk |
| Upload | where browser uploads go (default: `uploads` in the first writable folder; empty = KnA's data folder) |
| User mapping | optional, see [§3](#3-video-folders-and-permissions) |
| KnA | port, password, optional components |

Before creating the container the wizard shows the full summary with any warnings (space, permissions).
If something fails **after** the container was created, the script says which step stopped and offers to delete
the container or keep it to retry (`pct exec <id> -- bash /root/kna-install.sh`).

At the end it prints the address of the web UI.

Requirements: Proxmox VE 8 or newer (8.2+ for GPU passthrough to containers), internet access for the template and the KnA release.

### Unattended

All wizard answers can be given as variables. Example values from a test environment:

```bash
KNA_CT_ID=210 KNA_CT_CORES=6 KNA_CT_RAM=6144 KNA_CT_DISK=24 \
KNA_CT_GPU=auto \
KNA_CT_BINDS="/mnt/pve/media-share/videos:/media/videos;/mnt/pve/media-share/archive:/media/archive:ro" \
KNA_CT_IDMAP=1000:1000 KNA_PASSWORD='change-me' \
bash kna-lxc.sh --unattended
```

| Variable | Default | Meaning |
|---|---|---|
| `KNA_CT_ID` | next free id | Container id |
| `KNA_CT_HOSTNAME` | `kna` | Hostname |
| `KNA_CT_OS` | `debian-12` | `debian-12`, `debian-13` or `ubuntu-24.04` |
| `KNA_CT_CORES` · `KNA_CT_RAM` · `KNA_CT_SWAP` · `KNA_CT_DISK` | half the cores · 4096 · 512 · 16 | Cores, MB, MB, GB |
| `KNA_CT_STORAGE` · `KNA_CT_TEMPLATE_STORAGE` | first active | Storage for the disk and for the template |
| `KNA_CT_BRIDGE` · `KNA_CT_IP` · `KNA_CT_GW` · `KNA_CT_VLAN` | first bridge · `dhcp` | Network (`KNA_CT_IP` in CIDR form when static) |
| `KNA_CT_UNPRIVILEGED` | `1` | `0` for a privileged container |
| `KNA_CT_GPU` | `auto` | `auto` (every render node), `none`, or names such as `renderD128` |
| `KNA_CT_BINDS` | – | `host:container[:ro]` entries separated by `;` |
| `KNA_CT_FOLDERS` | `/srv/kna/videos` when there are no bind mounts | Video folders created inside the container |
| `KNA_CT_IDMAP` | – | `uid:gid` of the host user the container's `kna` user is mapped to |
| `KNA_UPLOAD_DIR` | `uploads` in the first writable folder | Upload folder inside the container (empty = KnA's data folder) |
| `KNA_INSTALL_LANG` | system locale | `it` or `en` (skips the language question) |

`KNA_PORT`, `KNA_PASSWORD`, `KNA_FFMPEG_SOURCE`, `KNA_COMPONENTS` and `KNA_VERSION` are passed to the installer inside.

## 2. Installing inside an existing container, VM or server

On Debian 12+ / Ubuntu 22.04+ (amd64 or arm64), as root:

```bash
curl -fsSL https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/deploy/install-linux.sh | bash
```

The wizard asks for the port, the **video folders** (read/write, `;`-separated, created when missing),
optional **read-only folders**, the upload folder, a password, the **GPU runtime** (the vendors found are preselected),
the FFmpeg build, the optional components and, if you want, the uid/gid of the service user.

What it installs:

- the self-contained server package of the newest KnA release in `/opt/kna` (no .NET SDK, nothing is compiled);
- fonts, Vulkan (Mesa, including the CPU fallback) and `vainfo`;
- Intel: the media driver (enabling Debian *non-free* / Ubuntu *multiverse* when needed) and oneVPL; AMD: Mesa VA-API;
- FFmpeg: by default KnA downloads the newest static build itself (*Settings → Updates & components* updates it later);
  the distribution package is the alternative, and the fallback when the download fails;
- the `kna` systemd service, running as the `kna` user (in the `render` and `video` groups);
- the `kna-update` command.

Unattended, with example values from a test environment:

```bash
KNA_MEDIA_ROOTS="/srv/videos;/mnt/share/videos" KNA_READONLY_ROOTS="/mnt/share/archive" \
KNA_PASSWORD='change-me' KNA_COMPONENTS="anime4k dovi_tool hdr10plus_tool" \
bash install-linux.sh --unattended
```

| Variable | Default | Meaning |
|---|---|---|
| `KNA_VERSION` | `latest` | Release to install (`3.0.0`…) |
| `KNA_PORT` | `8585` | Web UI port |
| `KNA_MEDIA_ROOTS` | `/srv/kna/videos` | Read/write video folders, `;`-separated |
| `KNA_READONLY_ROOTS` | – | Read-only folders |
| `KNA_UPLOAD_DIR` | data folder | Destination of browser uploads |
| `KNA_PASSWORD` | – | Web UI password (no single quotes) |
| `KNA_GPU` | `auto` | `auto`, `none`, or any of `intel amd nvidia` |
| `KNA_FFMPEG_SOURCE` | `btbn` | `btbn` (newest static build) or `distro` |
| `KNA_COMPONENTS` | `anime4k fsrcnnx dovi_tool hdr10plus_tool` | Also `ai-runtime` (Python + PyTorch, several GB); empty for none |
| `KNA_UID` · `KNA_GID` | system ids | Ids of the `kna` user |

### Updating, settings, removal

| | |
|---|---|
| Update | `kna-update` (inside the container; from the host: `pct exec <id> -- kna-update`) |
| Settings | `/etc/kna/kna.env`, then `systemctl restart kna` (variables [below](#environment-variables)) |
| Logs | `journalctl -u kna -f` |
| Reconfigure | run the installer again (the wizard starts from the current settings) |
| Remove | `bash /usr/local/sbin/kna-install --uninstall` (`--purge` also deletes settings and data, never the videos) |

## 3. Video folders and permissions

KnA only shows the folders listed in `KNA_MEDIA_ROOTS` (read/write) and `KNA_READONLY_ROOTS`.
By default outputs are written **next to the sources**, so the `kna` user needs write access to the read/write folders.
Existing folders are never changed. Missing ones are created by the `kna` user itself when it can (so a folder created inside an NFS/SMB share follows the share's rules, no `chown`); otherwise by root and given to `kna`. The installer warns when `kna` cannot write to a folder.

**Bind mounts in an unprivileged container.** Inside an unprivileged container, user *N* is user *100000 + N* on the host.
A folder owned by a host user (for example uid 1000) is therefore not writable by `kna`. You have three options:

- **User mapping** (the Proxmox script does this when you answer yes, or with `KNA_CT_IDMAP`). The container's `kna` user
  becomes exactly that host user: one line in `/etc/subuid` and `/etc/subgid` (`root:<uid>:1`), plus these lines in
  `/etc/pve/lxc/<id>.conf` (shown for uid/gid 1000):
  ```
  lxc.idmap: u 0 100000 1000
  lxc.idmap: u 1000 1000 1
  lxc.idmap: u 1001 101001 64535
  lxc.idmap: g 0 100000 1000
  lxc.idmap: g 1000 1000 1
  lxc.idmap: g 1001 101001 64535
  ```
- Give the host folder to the mapped id: `chown -R 100000+<kna uid> …` on the host. The container prints `id kna`.
- Use a privileged container. This is simpler, but less isolated.

Read-only bind mounts (`ro=1`) need no mapping: reading works when the files are readable by everyone.

Adding a bind mount later, from the host:

```bash
pct set <id> -mp2 /host/folder,mp=/media/new-folder
```

Then add `/media/new-folder` to `KNA_MEDIA_ROOTS` in `/etc/kna/kna.env` and restart the service.

## 4. GPU

KnA tests every encoder of its FFmpeg build at the first start. The *Hardware* card then shows what really works.
Use **Detect again** after changing GPU or driver.

| GPU | Inside the container | Notes |
|---|---|---|
| Intel (iGPU / Arc) | Quick Sync (oneVPL) and VA-API | the installer adds the media driver |
| AMD (APU / Radeon) | VA-API (Mesa), Vulkan | AMF is not used on Linux |
| NVIDIA | NVENC | the user-space driver must be installed in the container **at the same version as the host**, plus the `/dev/nvidia*` devices; a VM with PCIe passthrough is often simpler |

Passing a render node by hand (Proxmox VE 8.2+), from the host:

```bash
ls -l /dev/dri                                    # render nodes of the host
pct exec <id> -- getent group render              # gid of 'render' inside the container
pct set <id> -dev0 /dev/dri/renderD128,gid=<that-gid>
pct reboot <id>
```

> Don't combine a `devN:` entry with a manual `lxc.mount.entry` for the same device:
> Proxmox then fails with "mknod … File exists".

To check it inside the container: `vainfo --display drm --device /dev/dri/renderD128`.
`VAEntrypointEncSlice` lines list the codecs the GPU can encode.

## Environment variables

Read from `/etc/kna/kna.env` by the service.

| Variable | Default | Meaning |
|---|---|---|
| `KNA_URLS` | `http://0.0.0.0:8585` | Listen address |
| `KNA_MEDIA_ROOTS` | installer choice | Read/write folders that can be browsed, separated by `;` |
| `KNA_READONLY_ROOTS` | – | Extra roots that can be browsed, downloaded and encoded from but never written |
| `KNA_UPLOAD_DIR` | data folder | Default destination of browser uploads |
| `KNA_MAX_UPLOAD_GB` | no limit | Biggest accepted upload |
| `KNA_DATA_DIR` | `/var/lib/kna` | Settings, logs, temporary files, downloaded components |
| `KNA_PASSWORD` | – | Enables HTTP Basic authentication (any user name) |
| `KNA_FFMPEG` | auto | Path to a specific ffmpeg binary |
| `KNA_FDKAAC` | auto | Path to the standalone `fdkaac` encoder (bundled in the package) |
| `KNA_VULKAN_DEVICE` | auto | Vulkan GPU for libplacebo / Anime4K (index or name part; `llvmpipe` = CPU) |
| `KNA_AI_SCRIPT` | bundled | Path to `kna_upscale.py` (neural upscaler) |
| `KNA_VSPIPE` | auto | Path to `vspipe` (VapourSynth) |
| `KNA_ALLOW_ADVANCED` | `false` | Allows editing the FFmpeg path and extra arguments from the browser |
| `KNA_ALLOW_POWER_ACTIONS` | `false` | Allows "shut down / sleep when finished" |
| `KNA_LOCK_COMPONENTS` | `false` | Forbids installing/updating FFmpeg and the other components from the browser |
| `KNA_LANG` | auto | Forces the language (`it` or `en`); otherwise the browser decides, or *Settings → General* |

## VapourSynth and AviSynth on the server

- **VapourSynth**: install it in the container/VM (for example `pip install vapoursynth` followed by `vapoursynth config`, plus the source plugins your scripts use,
  such as L-SMASH Works or BestSource), make sure `vspipe` is on the PATH of the `kna` user or set `KNA_VSPIPE`.
- **AviSynth+** on Linux needs an FFmpeg built with `--enable-avisynth` and the AviSynth+ library installed.
  The usual FFmpeg builds do not include it; KnA says so clearly when an `.avs` file cannot be read.
