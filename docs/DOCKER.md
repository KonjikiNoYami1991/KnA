# KnA with Docker

[Italiano](DOCKER.it.md)

KnA's web edition runs in one container: open `http://<host>:8585` in any browser. The image is ready-made and
published with every release for **amd64** (Intel/AMD PCs and NAS) and **arm64** (Raspberry Pi 5, ARM NAS, Apple
Silicon with Docker Desktop):

```
ghcr.io/konjikinoyami1991/kna:latest      # the newest release
ghcr.io/konjikinoyami1991/kna:3.3.0       # a fixed version
```

Inside: KnA, FFmpeg (BtbN static build), fdkaac, NVEncC / QSVEncC (amd64), the VA-API / Quick Sync / Vulkan drivers
and Python for the AI upscaler. 32-bit ARM devices (armv7) are not supported.

## The three things to set

| What | Where | Why |
|---|---|---|
| **Your video folder** | bind mount → `/media` | KnA reads the sources and writes the results there |
| **PUID / PGID** | environment | KnA writes files as this user: they stay yours on the share |
| **KNA_PASSWORD** | environment | password of the administrator `admin` created at the first start; without it the first person who opens KnA creates the administrator |

Find PUID/PGID: `id -u` and `id -g` on Linux; on a NAS, `id <your user>` in an SSH session (Synology: SSH must be
enabled in Control Panel → Terminal & SNMP). Without PUID/PGID the container runs as root (files owned by root).

The settings, presets, queue, logs and AI models live in the folder mounted on `/config`: back it up like any folder.

## docker compose (any Linux host)

```bash
mkdir -p ~/kna && cd ~/kna
curl -fsSLO https://raw.githubusercontent.com/KonjikiNoYami1991/KnA/master/docker-compose.yml
nano docker-compose.yml        # video folder, PUID/PGID, KNA_PASSWORD
docker compose up -d
```

Update: `docker compose pull && docker compose up -d`. Logs: `docker logs -f kna`.

## docker run

```bash
docker run -d --name kna --restart unless-stopped -p 8585:8585 \
  -e PUID=1000 -e PGID=1000 -e KNA_PASSWORD=change-me -e TZ=Europe/Rome \
  --mount type=bind,src=/srv/kna/config,dst=/config \
  --mount type=bind,src=/mnt/videos,dst=/media \
  --device /dev/dri:/dev/dri \
  ghcr.io/konjikinoyami1991/kna:latest
```

## GPU

| GPU | What to add | Notes |
|---|---|---|
| Intel (Quick Sync), AMD (VA-API) | `--device /dev/dri:/dev/dri` (compose: `devices:`) | the device groups are joined by the container itself |
| NVIDIA (NVENC, CUDA) | `--gpus all` (compose: the `deploy:` block) | needs the NVIDIA driver and *nvidia-container-toolkit* on the host |
| none | nothing | CPU encoding (x264, x265, SVT-AV1) works everywhere |

After the first start, the **Hardware** card in KnA shows which encoders passed a test encode.

## Synology (DSM 7.2, Container Manager)

1. Install **Container Manager** from the Package Center.
2. File Station: create `docker/kna` (for the settings). Your videos stay where they are, e.g. `/volume1/video`.
3. Container Manager → **Project** → **Create**: name `kna`, path `docker/kna`, source **Create docker-compose.yml**,
   paste [docker-compose.yml](../docker-compose.yml) and change:
   - `source: ./config` stays (it becomes `docker/kna/config`);
   - `source: /mnt/anime` → `/volume1/video` (your shared folder);
   - `PUID` / `PGID`: from `id <your user>` over SSH (often `1026` / `100`);
   - Intel models with Quick Sync (e.g. DS920+, DS420+, DS224+): keep `devices: /dev/dri`; models without `/dev/dri`
     (AMD Ryzen, Realtek ARM): remove the `devices:` lines.
4. Next → Done. Open `http://<nas>:8585`.

Update: Container Manager → **Image** → `ghcr.io/konjikinoyami1991/kna` → **Update** (shown when a newer image
exists), then restart the project.

## QNAP (Container Station 3)

1. Install **Container Station** from the App Center.
2. Applications → **Create** → paste [docker-compose.yml](../docker-compose.yml), change the video path to your share
   (e.g. `/share/Multimedia`), the config path to e.g. `/share/Container/kna`, PUID/PGID (`id <user>` over SSH).
3. Intel models with Quick Sync: keep `devices: /dev/dri`. ARM 32-bit models (e.g. TS-431P, Annapurna Alpine)
   cannot run KnA: use another machine of the network as the server and the NAS only for the files.

## UGREEN (UGOS Pro, Docker app)

1. Install **Docker** from the App Center.
2. Docker → **Project** → **Create**: name `kna`, a folder for the project (e.g. `docker/kna`), paste
   [docker-compose.yml](../docker-compose.yml).
3. Change the video path to your shared folder as UGOS shows it (e.g. `/volume1/Video`), PUID/PGID (`id <user>` over
   SSH). Intel models: keep `devices: /dev/dri` for Quick Sync.

## Unraid

Docker → **Add Container**: repository `ghcr.io/konjikinoyami1991/kna:latest`, port `8585`, paths
`/config` → `/mnt/user/appdata/kna` and `/media` → `/mnt/user/videos`, variables `PUID=99`, `PGID=100`,
`KNA_PASSWORD`; Intel/AMD GPU: extra parameter `--device=/dev/dri`; NVIDIA: the *Nvidia Driver* plugin and
`--runtime=nvidia` with `NVIDIA_VISIBLE_DEVICES=all`.

## Portainer

Stacks → **Add stack** → Web editor: paste [docker-compose.yml](../docker-compose.yml), set the paths and variables,
**Deploy**. Update: the stack → **Pull and redeploy**.

## Proxmox

Prefer the LXC installer (native, no Docker, GPU passthrough handled): [PROXMOX.md](PROXMOX.md). Docker inside an
LXC works too but needs nesting and the device passthrough done twice.

## HTTPS and reverse proxy

nginx, Caddy, Traefik or Nginx Proxy Manager in front of KnA (on a domain or under `/kna`): set
`KNA_TRUSTED_PROXIES` to the proxy's Docker network (or `private`), see [REVERSE-PROXY.md](REVERSE-PROXY.md).

## Desktop app on this server

The desktop app finds kna-server on the local network by a UDP broadcast on port 8585: broadcasts don't reach a
container on a bridge network, so in Docker type the server's address in Settings → KnA server (or use
`network_mode: host`).

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Permission denied" writing outputs | PUID/PGID must be a user that can write the video share; check with `ls -ln` on the host |
| No hardware encoder in the Hardware card | `/dev/dri` not passed (Intel/AMD), or `--gpus all` / nvidia-container-toolkit missing (NVIDIA) |
| The page asks for a password you don't know | user `admin` and `KNA_PASSWORD` of the first start; lost: `docker exec -it kna dotnet /app/kna-server.dll --reset-password admin` |
| `exec format error` | 32-bit ARM host: not supported |
| Everything else | `docker logs kna` and the job's log (📄 in the queue) |

Every `KNA_*` variable: [PROXMOX.md → Environment variables](PROXMOX.md#environment-variables).
