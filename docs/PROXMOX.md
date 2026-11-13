# KnA on Proxmox VE (LXC or VM, no desktop)

**English** · [Italiano](PROXMOX.it.md)

KnA's web edition runs headless: the encoder runs on the server, and you use it from any
browser (PC, Mac, phone) at `http://<container-ip>:8585`.

There are two ways to install it:

| | Docker (inside an LXC or VM) | Native service (LXC or VM) |
|---|---|---|
| Install | `docker compose up -d` | `sudo ./deploy/install-linux.sh` |
| Updates | rebuild the image | `git pull` + run the script again |
| GPU | `/dev/dri` (or `--gpus all`) in the compose file | the container's `/dev/dri` is used directly |

## 1. Create the container

An **unprivileged Ubuntu 24.04 LXC** is the simplest choice: .NET 10 comes from the Ubuntu repositories.

- CPU: as many cores as you can spare (x265 and SVT-AV1 use all of them).
- RAM: 2–4 GB.
- Disk: 8 GB plus space for temporary files.
- For Docker inside the LXC, enable *Options → Features → Nesting*.

## 2. Pass the GPU through (hardware encoding)

On the Minisforum UM890 Pro, the Radeon 780M iGPU (RDNA 3) encodes H.264, HEVC and AV1
through **VA-API**. AMF is not used on Linux.

1. On the **Proxmox host**, check that the render node exists: `ls -l /dev/dri` should list `renderD128`.
2. Find the `render` group id **inside the container**:
   ```bash
   pct exec <CTID> -- getent group render
   ```
3. Add the device. Use either the GUI (*Resources → Add → Device Passthrough*), or the CLI:
   ```bash
   pct set <CTID> -dev0 /dev/dri/renderD128,gid=<render-gid-inside-the-CT>
   ```
4. Restart the container. Device entries are applied when it starts.

> Don't combine a `devN:` entry with a manual `lxc.mount.entry` for the same device:
> Proxmox then fails with "mknod … File exists".

Check that it works inside the container:

```bash
apt install -y vainfo
vainfo --display drm --device /dev/dri/renderD128 | grep -i enc
```

Look for `VAEntrypointEncSlice` on `VAProfileAV1Profile0`, `VAProfileHEVCMain10` and `VAProfileH264High`.

**NVIDIA GPUs** in an LXC need the same driver version on the host and in the container,
plus the `nvidia*` devices passed through. A VM with PCIe passthrough is simpler.

## 3a. Native install (no Docker)

```bash
apt install -y git
git clone https://github.com/KonjikiNoYami1991/KnA.git && cd KnA
KNA_MEDIA_ROOTS="/mnt/anime;/mnt/nas" ./deploy/install-linux.sh
```

The script installs FFmpeg, the VA-API/QSV drivers and .NET 10, builds KnA into `/opt/kna`,
and creates the `kna.service` systemd unit. That unit runs as the `kna` user, which is in the `render` and `video` groups.

## 3b. Docker

```bash
git clone https://github.com/KonjikiNoYami1991/KnA.git && cd KnA
# edit docker-compose.yml: your media folder, RENDER_GID, optional KNA_PASSWORD
RENDER_GID=$(getent group render | cut -d: -f3) docker compose up -d --build
```

## 4. Media folders (NAS)

Mount your NAS shares in the container, for example the UGREEN DXP4800 Pro or the Synology DS420+.
Either bind-mount them from the host (`pct set <CTID> -mp0 /mnt/pve/nas-anime,mp=/mnt/anime`)
or use NFS/SMB inside a privileged container/VM. Then list them in `KNA_MEDIA_ROOTS`, separated by `;`.
The browser can only see files inside those roots.

The `kna` user (or the container) needs **write** access there: by default, outputs are written next to the sources.

## 5. First start

On the first start, KnA test-encodes a short clip with every encoder in the FFmpeg build.
The *Hardware* card then shows what actually works on this machine.
Use **Detect again** after changing GPU or driver.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `KNA_URLS` | `http://0.0.0.0:8585` | Listen address |
| `KNA_MEDIA_ROOTS` | `/media` (Docker), whole file system (native) | Read/write folders that can be browsed, separated by `;` |
| `KNA_READONLY_ROOTS` | – | Extra roots that can be browsed, downloaded and encoded from but never written |
| `KNA_UPLOAD_DIR` | `/media/uploads` (Docker), data folder | Default destination of browser uploads |
| `KNA_MAX_UPLOAD_GB` | no limit | Biggest accepted upload |
| `KNA_FDKAAC` | auto | Path to the standalone `fdkaac` encoder |
| `KNA_VULKAN_DEVICE` | auto | Vulkan GPU for libplacebo / Anime4K (index or name part; `llvmpipe` = CPU) |
| `KNA_AI_SCRIPT` | bundled | Path to `kna_upscale.py` (neural upscaler) |
| `KNA_DATA_DIR` | `/config` (Docker), `/var/lib/kna` | Settings, logs, temporary files |
| `KNA_PASSWORD` | – | Enables HTTP Basic authentication (any user name) |
| `KNA_ALLOW_ADVANCED` | `false` | Allows editing the FFmpeg path and extra arguments from the browser |
| `KNA_ALLOW_POWER_ACTIONS` | `false` | Allows "shut down / sleep when finished" |
| `KNA_FFMPEG` | auto | Path to a specific ffmpeg binary (e.g. jellyfin-ffmpeg) |
| `KNA_LOCK_COMPONENTS` | `false` | Forbids installing/updating FFmpeg and the HDR tools from the browser |
| `KNA_LANG` | auto | Forces the language (`it` or `en`); otherwise the browser decides, or *Settings → General* |
| `KNA_VSPIPE` | auto | Path to `vspipe` (VapourSynth) |

## VapourSynth and AviSynth on the server

- **VapourSynth**: install it in the container/VM (for example `pip install vapoursynth` followed by `vapoursynth config`, plus the source plugins your scripts use,
  such as L-SMASH Works or BestSource), make sure `vspipe` is on the PATH of the `kna` user or set `KNA_VSPIPE`.
- **AviSynth+** on Linux needs an FFmpeg built with `--enable-avisynth` and the AviSynth+ library installed.
  The Ubuntu FFmpeg package and the Docker image do not include it; KnA says so clearly when an `.avs` file cannot be read.
